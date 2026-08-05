using System.Diagnostics;
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

        if (!_byName.TryGetValue(call.Name, out AIFunction? function))
        {
            string known = string.Join(", ", _byName.Keys);
            _logger.LogWarning("Model called unknown tool {ToolName}", call.Name);
            return new ToolInvocation
            {
                CallId = call.CallId,
                ToolName = call.Name,
                Status = ToolCallStatus.UnknownTool,
                Arguments = arguments,
                Duration = TimeSpan.Zero,
                ErrorMessage = $"No tool named '{call.Name}'.",
                Result = Observation.Fail<object>(
                    call.Name,
                    ToolErrorCodes.UnknownTool,
                    $"No tool named '{call.Name}'.",
                    $"Available tools: {known}."),
            };
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
