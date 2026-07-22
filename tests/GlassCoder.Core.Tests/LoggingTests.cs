using GlassCoder.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Tests;

/// <summary>
/// Logging and redaction (workplan task 5). Secrets must never reach the log store, and the
/// content switch must be enforced by the pipeline rather than by callers remembering it.
/// </summary>
public sealed class LoggingTests
{
    [Theory]
    [InlineData("Authorization: Bearer abc123DEF456ghi789jkl", "Bearer [redacted]")]
    [InlineData("key is sk-proj-0123456789abcdefghij", "[redacted]")]
    [InlineData("""{"api_key": "super-secret-value"}""", "[redacted]")]
    public void Secret_shaped_values_are_scrubbed(string input, string expected)
    {
        SecretRedactor.Scrub(input).ShouldContain(expected);
    }

    [Fact]
    public void Ordinary_text_survives_scrubbing_unchanged()
    {
        const string source = "public sealed class AgentLoop : IAgentLoop { }";

        SecretRedactor.Scrub(source).ShouldBe(source);
    }

    [Fact]
    public void Long_values_are_truncated_with_the_dropped_length_reported()
    {
        string? truncated = SecretRedactor.Truncate(new string('x', 100), 10);

        truncated.ShouldStartWith("xxxxxxxxxx ");
        truncated.ShouldContain("90 more characters");
    }

    [Fact]
    public void Turning_content_logging_off_keeps_the_step_skeleton_and_drops_the_content()
    {
        CapturingLogger logger = new();
        StepLogger stepLogger = new(logger, Options.Create(new LoggingOptions { LogSourceContent = false }));

        stepLogger.LogStep(Sample());

        StepRecord recorded = logger.Records.ShouldHaveSingleItem();
        recorded.StepIndex.ShouldBe(4);
        recorded.TotalTokens.ShouldBe(120);
        recorded.ToolCalls[0].Name.ShouldBe("read_file");
        recorded.Prompt[0].Text.ShouldBe(SecretRedactor.ContentDisabledMarker);
        recorded.ResponseText.ShouldBe(SecretRedactor.ContentDisabledMarker);
        recorded.ToolCalls[0].Result.ShouldBe(SecretRedactor.ContentDisabledMarker);
        recorded.ToolCalls[0].Arguments.ShouldBeNull();
    }

    [Fact]
    public void With_content_logging_on_the_content_is_kept_but_still_scrubbed()
    {
        CapturingLogger logger = new();
        StepLogger stepLogger = new(logger, Options.Create(new LoggingOptions()));

        stepLogger.LogStep(Sample() with { ResponseText = "the key is sk-proj-0123456789abcdefghij" });

        StepRecord recorded = logger.Records.ShouldHaveSingleItem();
        recorded.Prompt[0].Text.ShouldBe("read src/Program.cs");
        recorded.ResponseText.ShouldNotBeNull();
        recorded.ResponseText.ShouldNotContain("sk-proj");
    }

    [Fact]
    public void The_serilog_pipeline_writes_both_a_jsonl_and_a_text_file()
    {
        string directory = Path.Combine(Path.GetTempPath(), "glasscoder-tests", Guid.NewGuid().ToString("n"));
        try
        {
            LoggingOptions options = new() { Directory = directory, Console = false };
            using Serilog.Core.Logger logger = SerilogBootstrap.CreateLogger(options);

            logger.Information("plain event");
            logger.Information("glasscoder.step {@Step}", Sample());
            logger.Dispose();

            string[] files = Directory.GetFiles(directory);
            files.ShouldContain(f => f.EndsWith(".jsonl", StringComparison.Ordinal));
            files.ShouldContain(f => f.EndsWith(".log", StringComparison.Ordinal));

            string jsonl = File.ReadAllText(files.First(f => f.EndsWith(".jsonl", StringComparison.Ordinal)));
            string text = File.ReadAllText(files.First(f => f.EndsWith(".log", StringComparison.Ordinal)));

            // The transcript sink gets the step record; the human sink is spared the blob.
            jsonl.ShouldContain("read_file");
            text.ShouldContain("plain event");
            text.ShouldNotContain("read_file");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static StepRecord Sample() => new()
    {
        RunId = "run-1",
        TaskId = "task-1",
        StepIndex = 4,
        Role = "worker",
        StartedAt = DateTimeOffset.UnixEpoch,
        Prompt = [new TranscriptMessage("user", "read src/Program.cs")],
        ResponseText = "calling read_file",
        ToolCalls =
        [
            new ToolCallRecord(
                "call-1",
                "read_file",
                new Dictionary<string, object?> { ["path"] = "src/Program.cs" },
                "Succeeded",
                Parsed: true,
                DurationMs: 3,
                Result: """{"ok":true}""",
                Error: null),
        ],
        InputTokens = 100,
        OutputTokens = 20,
        TotalTokens = 120,
        ModelLatencyMs = 42,
        StepLatencyMs = 45,
        Outcome = "continued",
    };

    /// <summary>Captures the destructured step record out of the logging call.</summary>
    private sealed class CapturingLogger : ILogger<StepLogger>
    {
        public List<StepRecord> Records { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
            {
                foreach ((string key, object? value) in values)
                {
                    // Depending on the provider the destructuring hint may or may not be
                    // stripped from the property name.
                    if (key.TrimStart('@') == "Step" && value is StepRecord record)
                    {
                        Records.Add(record);
                    }
                }
            }
        }
    }
}
