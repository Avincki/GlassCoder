using System.Reflection;
using GlassCoder.Core.Diagnostics;
using GlassCoder.Core.Verification;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The digest is the instrument this harness learns through, and it must not lose the harness's own
/// steering on the way.
/// <para>
/// Four surfaces have now mapped a rich model-facing fact onto a lossy retelling: the model-facing
/// verdict header (2026-08-09), the critique tally (2026-08-11), the verification verdict
/// (2026-08-15), and the refusal hint - found by a reviewer of run <c>dd11ef7c</c> that had built a
/// careful argument about the wording of a message it had been shown half of. Its conclusion
/// survived; the recommendation it implied did not. Half the text pointed at wording. The whole
/// text pointed at persistence.
/// </para>
/// <para>
/// So this is not a test that the hint renders. It is a test that <em>every</em> field declaring
/// itself model-facing renders, driven by reflection over the record rather than by a list kept
/// here - a list would be the fifth place to forget. Marking a new field and not rendering it fails
/// the build; rendering it in a way another field can shadow fails it too, because they are all
/// populated at once.
/// </para>
/// </summary>
public sealed class RetrospectiveDigestTests
{
    [Fact]
    public void Every_model_facing_field_of_a_tool_call_survives_into_the_digest()
    {
        PropertyInfo[] marked =
        [
            .. typeof(ToolCallRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<ModelFacingAttribute>() is not null),
        ];

        marked.ShouldNotBeEmpty("a record with no model-facing field would pass this test vacuously");

        // One distinctive sentinel per field, all present at once: a renderer that prints the
        // summary *or* the error passes a per-field test and still drops one here.
        Dictionary<string, string> sentinels = marked.ToDictionary(
            p => p.Name,
            p => $"<<{p.Name}-was-here>>",
            StringComparer.Ordinal);

        ToolCallRecord call = new(
            "call-1",
            "dotnet_project",
            null,
            "Failed",
            Parsed: true,
            DurationMs: 12,
            Result: null,
            Error: sentinels[nameof(ToolCallRecord.Error)],
            Summary: sentinels[nameof(ToolCallRecord.Summary)],
            Hint: sentinels[nameof(ToolCallRecord.Hint)]);

        string digest = RetrospectiveTranscript.Render(
            [
                new StepRecord
                {
                    RunId = "run-1",
                    TaskId = "desktop",
                    StepIndex = 2,
                    Role = "worker",
                    StartedAt = DateTimeOffset.UnixEpoch,
                    Prompt = [],
                    ToolCalls = [call],
                    ModelLatencyMs = 1,
                    StepLatencyMs = 1,
                    Outcome = "continued",
                },
            ],
            new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        List<string> missing =
        [
            .. sentinels.Where(pair => !digest.Contains(pair.Value, StringComparison.Ordinal))
                .Select(pair => pair.Key),
        ];

        missing.ShouldBeEmpty(
            "the digest is what a retrospective reasons from, and these fields were shown to the " +
            $"model and not to it: {string.Join(", ", missing)}. Render them, or - if one genuinely " +
            "does not belong in a digest, the way a raw result does not - take the marker off it " +
            "and say why in its own documentation.");
    }

    [Fact]
    public void The_hint_is_the_instance_this_was_built_for()
    {
        // Step 2 of run dd11ef7c, verbatim in shape: a refusal carrying a filename, a tool name and
        // an ordering, none of which reached either reviewer.
        string digest = RetrospectiveTranscript.Render(
            [
                new StepRecord
                {
                    RunId = "run-1",
                    TaskId = "desktop",
                    StepIndex = 2,
                    Role = "worker",
                    StartedAt = DateTimeOffset.UnixEpoch,
                    Prompt = [],
                    ToolCalls =
                    [
                        new ToolCallRecord(
                            "call-1",
                            "dotnet_project",
                            null,
                            "Failed",
                            Parsed: true,
                            DurationMs: 12,
                            Result: null,
                            Error: "A solution cannot be created below the repository root.",
                            Summary: "A solution cannot be created below the repository root.",
                            Hint: "Create it at the root instead - path 'TemperatureConverter.slnx' - " +
                                  "and then add each project with add_to_solution."),
                    ],
                    ModelLatencyMs = 1,
                    StepLatencyMs = 1,
                    Outcome = "continued",
                },
            ],
            new RetrospectiveRequest("run-1") { StopReason = "Completed", Steps = 1 });

        digest.ShouldContain("hint:");
        digest.ShouldContain("TemperatureConverter.slnx");
        digest.ShouldContain("add_to_solution");
    }
}
