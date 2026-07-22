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

        IReadOnlyDictionary<string, object?>? arguments = call.Arguments is null
            ? null
            : new Dictionary<string, object?>(call.Arguments, StringComparer.Ordinal);

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
