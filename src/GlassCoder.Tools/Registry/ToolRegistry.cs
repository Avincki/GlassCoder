using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace GlassCoder.Tools.Registry;

/// <summary>
/// Default <see cref="IToolRegistry"/>: owns the generated schemas and is the one place a tool
/// call is executed (CLAUDE.md §7, workplan task 7).
/// <para>
/// Every failure mode - unknown tool, arguments that will not bind, a tool that throws - leaves
/// this class as a <see cref="ToolObservation{TData}"/>. Nothing propagates as an exception,
/// because a tool failure is information the agent should act on, not a reason for the run to
/// end (CLAUDE.md §14).
/// </para>
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    /// <summary>
    /// Wrong names whose intent is unambiguous, rewritten to the tool the model meant. Only
    /// names proven in run logs belong here - an alias is a bet that the model's habit is
    /// stable, and each one is a name the "did you mean" hint failed to convert.
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["todo_write"] = "update_todos",
        ["write_todos"] = "update_todos",
        ["todos"] = "update_todos",
    };

    /// <summary>
    /// Wrong <em>argument</em> names whose intent is unambiguous, per tool - the same contract
    /// as <see cref="Aliases"/>, one level down. Run c5eb67f6 called
    /// <c>read_file(offset: 70)</c> - another harness's name for <c>startLine</c> - thirteen
    /// times; the binder silently dropped the unknown key and returned the head of the file
    /// thirteen times, each answer marked Succeeded.
    /// </summary>
    private static readonly Dictionary<(string Tool, string Argument), string> ArgumentAliases = new()
    {
        [("read_file", "offset")] = "startLine",
    };

    /// <summary>Names that mean "give me a shell" - which no alias can honour.</summary>
    private static readonly HashSet<string> ShellNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "bash", "shell", "sh", "cmd", "powershell", "exec", "terminal",
    };

    private readonly Dictionary<string, AIFunction> _byName;
    private readonly ILogger<ToolRegistry> _logger;

    /// <summary>Creates a registry over an already-built function list.</summary>
    public ToolRegistry(IReadOnlyList<AIFunction> functions, ILogger<ToolRegistry>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(functions);

        Functions = functions;
        Tools = [.. functions.Cast<AITool>()];
        _byName = functions.ToDictionary(f => f.Name, StringComparer.Ordinal);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolRegistry>.Instance;
    }

    /// <summary>Creates a registry by reflecting over the supplied tool sets.</summary>
    public ToolRegistry(IEnumerable<IToolSet> toolSets, ILogger<ToolRegistry>? logger = null)
        : this(ToolFunctionFactory.Create(toolSets), logger)
    {
    }

    /// <inheritdoc />
    public IReadOnlyList<AIFunction> Functions { get; }

    /// <inheritdoc />
    public IReadOnlyList<AITool> Tools { get; }

    /// <inheritdoc />
    public bool TryGetFunction(string name, out AIFunction? function) => _byName.TryGetValue(name, out function);

    /// <inheritdoc />
    public async Task<ToolInvocation> InvokeAsync(FunctionCallContent call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);

        // Unwrapped for the record, not for the invocation: a JsonElement serialises through the
        // log as {"ValueKind":"String"} with its value gone, which makes a transcript that cannot
        // answer what the model actually asked for (CLAUDE.md §9). Two diagnoses of a failing run
        // had to infer an argument from artefacts on disk because of it.
        IReadOnlyDictionary<string, object?>? arguments = Describe(call.Arguments);

        // Known aliases are rewritten and invoked, not hinted at. Run f4ed50e0 called
        // todo_write twice - with byte-identical update_todos arguments - and the "did you
        // mean" hint converted the first miss but not the second: a suggestion the model
        // ignores twice is not a mechanism. The rewrite is only for names whose intent is
        // unambiguous; everything else still earns the hint below.
        if (!_byName.ContainsKey(call.Name) &&
            Aliases.TryGetValue(call.Name, out string? canonical) &&
            _byName.ContainsKey(canonical))
        {
            _logger.LogInformation(
                "Model called {Alias}; rewritten to {Canonical} and invoked", call.Name, canonical);
            call = new FunctionCallContent(call.CallId, canonical, call.Arguments);
        }

        if (!_byName.TryGetValue(call.Name, out AIFunction? function))
        {
            // A wrong tool name is nearly always a near-miss on a right one - `run` for
            // `run_tests` cost a step in run d18c0e57. Naming the likely intent in the message
            // turns the retry into the call the model meant to make. Shell-shaped names are
            // the exception: run 008007e1 sent `run` meaning `rm -rf`, run 216360bf sent it
            // meaning `copy`, and "did you mean run_tests?" answers neither - what the model
            // wants there is a shell, and the honest answer is that there is none.
            string known = string.Join(", ", _byName.Keys);
            string message;
            if (ShellNames.Contains(call.Name))
            {
                message = $"There is no shell and no '{call.Name}' tool. Copy a file by reading it and " +
                    "writing it with create_file; delete or move one with file_operation; run tests with " +
                    "run_tests. The application is launched by the operator's Run app button, never by you.";
            }
            else
            {
                string? nearest = Closest(call.Name);
                message = nearest is null
                    ? $"No tool named '{call.Name}'."
                    : $"No tool named '{call.Name}'. Did you mean '{nearest}'?";
            }

            _logger.LogWarning("Model called unknown tool {ToolName}", call.Name);
            return new ToolInvocation
            {
                CallId = call.CallId,
                ToolName = call.Name,
                Status = ToolCallStatus.UnknownTool,
                Arguments = arguments,
                Duration = TimeSpan.Zero,
                ErrorMessage = message,
                Result = Observation.Fail<object>(
                    call.Name,
                    ToolErrorCodes.UnknownTool,
                    message,
                    $"Available tools: {known}."),
            };
        }

        // Arguments are validated against the schema before they bind, because the binder
        // silently drops unknown keys: run c5eb67f6 paged a file with 'offset' - another
        // harness's name for startLine - thirteen times, got the head of the file thirteen
        // times, and every answer read Succeeded. A known alias is rewritten and invoked; any
        // other unknown name fails loudly with the real parameter list; integer parameters
        // forgive the numeric shapes models actually send ("70", 70.0).
        if (call.Arguments is { Count: > 0 } &&
            NormalizeArguments(call.Name, function, call.Arguments) is { } normalized)
        {
            if (normalized.Error is not null)
            {
                _logger.LogWarning(
                    "Arguments for tool {ToolName} were refused before binding: {Reason}",
                    call.Name, normalized.Error);
                return new ToolInvocation
                {
                    CallId = call.CallId,
                    ToolName = call.Name,
                    Status = ToolCallStatus.InvalidArguments,
                    Arguments = arguments,
                    Duration = TimeSpan.Zero,
                    ErrorMessage = normalized.Error,
                    Result = Observation.Fail<object>(
                        call.Name, ToolErrorCodes.InvalidArgument, normalized.Error, normalized.Hint),
                };
            }

            call = new FunctionCallContent(call.CallId, call.Name, normalized.Arguments);
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            AIFunctionArguments functionArguments = call.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(call.Arguments);

            object? result = await function.InvokeAsync(functionArguments, cancellationToken).ConfigureAwait(false);
            TimeSpan duration = Stopwatch.GetElapsedTime(start);

            return new ToolInvocation
            {
                CallId = call.CallId,
                ToolName = call.Name,
                Status = ReportsSuccess(result) ? ToolCallStatus.Succeeded : ToolCallStatus.Failed,
                Arguments = arguments,
                Duration = duration,
                Result = result,

                // Carried up so the loop can tell one failure from the same failure again,
                // without reaching into the observation's payload type.
                ErrorMessage = ReportsSuccess(result) ? null : DescribeFailure(result),
                Summary = SummaryOf(result),

                // The AI function layer hands results back as JsonElement, so the flag is read
                // off the wire shape - where it appears only when false.
                OutcomeOk = result switch
                {
                    IToolObservation observation => observation.OutcomeOk,
                    JsonElement { ValueKind: JsonValueKind.Object } element =>
                        !element.TryGetProperty("outcomeOk", out JsonElement flag) ||
                        flag.ValueKind != JsonValueKind.False,
                    _ => true,
                },
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled run is the loop's business, not an observation.
            throw;
        }
        catch (Exception ex) when (IsArgumentBindingFailure(ex))
        {
            _logger.LogWarning(ex, "Arguments for tool {ToolName} did not bind to its schema", call.Name);
            return Faulted(call, arguments, start, ToolCallStatus.InvalidArguments, ToolErrorCodes.InvalidArgument, ex,
                "Re-read the tool schema and send arguments that match it exactly.");
        }
        catch (Exception ex)
        {
            // A tool that throws is a defect - the contract says errors are observations. Keep
            // the run alive, tell the model what happened, and leave the defect in the log.
            _logger.LogError(ex, "Tool {ToolName} threw instead of returning an observation", call.Name);
            return Faulted(call, arguments, start, ToolCallStatus.Faulted, ToolErrorCodes.Unexpected, ex, null);
        }
    }

    /// <summary>
    /// The supplied arguments checked against the function's schema: aliased names rewritten,
    /// integer values coerced from the shapes models send, unknown names refused with the real
    /// parameter list. Null when nothing needed changing.
    /// </summary>
    private static NormalizedArguments? NormalizeArguments(
        string toolName, AIFunction function, IDictionary<string, object?> supplied)
    {
        JsonElement schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("properties", out JsonElement properties) ||
            properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        Dictionary<string, JsonElement> known = new(StringComparer.Ordinal);
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            known[property.Name] = property.Value;
        }

        Dictionary<string, object?>? rewritten = null;
        foreach ((string name, object? value) in supplied)
        {
            string target = name;
            if (!known.ContainsKey(name))
            {
                if (ArgumentAliases.TryGetValue((toolName, name), out string? canonical) &&
                    known.ContainsKey(canonical))
                {
                    target = canonical;
                }
                else
                {
                    return new NormalizedArguments(
                        null,
                        $"'{toolName}' has no parameter named '{name}'.",
                        $"Its parameters are: {string.Join(", ", known.Keys)}.");
                }
            }

            object? coerced = CoerceInteger(known[target], value, out string? refusal);
            if (refusal is not null)
            {
                return new NormalizedArguments(null, $"'{name}' {refusal}", null);
            }

            if (!string.Equals(target, name, StringComparison.Ordinal) || !ReferenceEquals(coerced, value))
            {
                rewritten ??= new Dictionary<string, object?>(supplied, StringComparer.Ordinal);
                rewritten.Remove(name);
                rewritten[target] = coerced;
            }
        }

        return rewritten is null ? null : new NormalizedArguments(rewritten, null, null);
    }

    /// <summary>
    /// The value an integer parameter can bind, from the shapes models actually send: a JSON
    /// number, "70", or 70.0. A fractional value is refused rather than truncated - 70.5 lines
    /// is a confusion, not a request. Anything unrecognised passes through for the binder to
    /// judge, so this can only widen what binds, never narrow it.
    /// </summary>
    private static object? CoerceInteger(JsonElement property, object? value, out string? refusal)
    {
        refusal = null;
        if (property.ValueKind != JsonValueKind.Object ||
            !property.TryGetProperty("type", out JsonElement type) ||
            type.ValueKind != JsonValueKind.String ||
            type.GetString() != "integer")
        {
            return value;
        }

        double? numeric = value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
            JsonElement { ValueKind: JsonValueKind.String } element when double.TryParse(
                element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            string text when double.TryParse(
                text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };

        if (numeric is not { } number)
        {
            return value;
        }

        if (Math.Abs(number - Math.Round(number)) > 0.000001)
        {
            refusal = $"must be a whole number, got {number.ToString(CultureInfo.InvariantCulture)}.";
            return value;
        }

        return (int)Math.Round(number);
    }

    /// <summary>Outcome of the pre-bind argument check: a rewrite, or a refusal, never both.</summary>
    private sealed record NormalizedArguments(
        IDictionary<string, object?>? Arguments, string? Error, string? Hint);

    /// <summary>
    /// The registered name the model most likely meant: a substring relation first, then a
    /// small edit distance. Null when nothing is close enough to be worth suggesting.
    /// </summary>
    private string? Closest(string requested)
    {
        List<string> related = [.. _byName.Keys
            .Where(name => name.Contains(requested, StringComparison.OrdinalIgnoreCase) ||
                           requested.Contains(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => Math.Abs(name.Length - requested.Length))];
        if (related.Count > 0)
        {
            return string.Join("' or '", related.Take(2));
        }

        (string Name, int Distance) best = _byName.Keys
            .Select(name => (Name: name, Distance: EditDistance(name, requested)))
            .OrderBy(candidate => candidate.Distance)
            .First();

        return best.Distance <= 3 ? best.Name : null;
    }

    /// <summary>Levenshtein distance, case-insensitive. Tool names are short; O(n·m) is nothing.</summary>
    private static int EditDistance(string left, string right)
    {
        left = left.ToLowerInvariant();
        right = right.ToLowerInvariant();

        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int substitution = previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static ToolInvocation Faulted(
        FunctionCallContent call,
        IReadOnlyDictionary<string, object?>? arguments,
        long start,
        ToolCallStatus status,
        string code,
        Exception exception,
        string? hint) =>
        new()
        {
            CallId = call.CallId,
            ToolName = call.Name,
            Status = status,
            Arguments = arguments,
            Duration = Stopwatch.GetElapsedTime(start),
            ErrorMessage = exception.Message,
            Result = Observation.Fail<object>(call.Name, code, exception.Message, hint),
        };

    private static bool IsArgumentBindingFailure(Exception exception) =>
        exception is JsonException or ArgumentException or FormatException or InvalidCastException ||
        (exception is TargetInvocationException invocation && invocation.InnerException is ArgumentException);

    /// <summary>
    /// Reads the <c>ok</c> flag out of whatever shape the function marshalled its observation
    /// into, so a handled tool failure is not counted as a hard fault.
    /// </summary>
    /// <summary>
    /// Copies the model's arguments into values a log can actually hold.
    /// <para>
    /// The values arrive as <see cref="JsonElement"/>, whose public surface is its kind rather
    /// than its content - so serialising one records that a string was passed and not which
    /// string. Unwrapping here keeps the transcript reconstructable, which is the whole point of
    /// recording arguments at all.
    /// </para>
    /// </summary>
    private static IReadOnlyDictionary<string, object?>? Describe(IEnumerable<KeyValuePair<string, object?>>? arguments)
    {
        if (arguments is null)
        {
            return null;
        }

        Dictionary<string, object?> described = new(StringComparer.Ordinal);
        foreach ((string name, object? value) in arguments)
        {
            described[name] = Unwrap(value);
        }

        return described;
    }

    private static object? Unwrap(object? value) => value switch
    {
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Null } => null,
        JsonElement { ValueKind: JsonValueKind.Number } element =>
            element.TryGetInt64(out long whole) ? whole : element.GetDouble(),

        // Objects and arrays keep their JSON text: structured enough to read back, and it cannot
        // drag an arbitrary object graph into the log.
        JsonElement element => element.GetRawText(),
        _ => value,
    };

    /// <summary>
    /// A stable description of <em>how</em> a call failed: its error code and summary, which
    /// together are the same string every time the same thing goes wrong. That sameness is what
    /// lets the loop notice it is repeating itself.
    /// </summary>
    private static string? DescribeFailure(object? result)
    {
        (string? code, string? summary) = Read(result);
        return Combine(code, summary);
    }

    /// <summary>
    /// The observation's own one-line account of what it did, whether or not it went well.
    /// <para>
    /// Carried up so the transcript's console line can say what happened rather than only whether
    /// the call ran. A build that compiled nothing logged as <c>build:Succeeded</c> - true of the
    /// call, and read by every human as a claim about the build.
    /// </para>
    /// </summary>
    private static string? SummaryOf(object? result) => Read(result).Summary;

    /// <summary>Pulls the error code and summary off an observation, whatever shape it arrived in.</summary>
    private static (string? Code, string? Summary) Read(object? result)
    {
        if (result is null)
        {
            return (null, null);
        }

        if (result is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return (
                element.TryGetProperty("error", out JsonElement error) &&
                    error.TryGetProperty("code", out JsonElement code)
                        ? code.GetString()
                        : null,
                element.TryGetProperty("summary", out JsonElement summary) ? summary.GetString() : null);
        }

        Type type = result.GetType();
        object? errorValue = type.GetProperty("Error", BindingFlags.Public | BindingFlags.Instance)?.GetValue(result);

        return (
            errorValue?.GetType().GetProperty("Code", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(errorValue) as string,
            type.GetProperty("Summary", BindingFlags.Public | BindingFlags.Instance)?.GetValue(result) as string);
    }

    private static string? Combine(string? code, string? summary) => (code, summary) switch
    {
        (null, null) => null,
        (null, { } only) => only,
        ({ } only, null) => only,
        _ => $"{code}: {summary}",
    };

    private static bool ReportsSuccess(object? result)
    {
        switch (result)
        {
            case null:
                return true;

            case JsonElement { ValueKind: JsonValueKind.Object } element:
                return !element.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.False;

            default:
                PropertyInfo? okProperty = result.GetType().GetProperty("Ok", BindingFlags.Public | BindingFlags.Instance);
                return okProperty?.GetValue(result) is not false;
        }
    }
}
