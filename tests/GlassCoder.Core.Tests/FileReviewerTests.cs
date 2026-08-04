using GlassCoder.Core.Verification;
using GlassCoder.TestSupport;
using GlassCoder.Tools.Processes;

namespace GlassCoder.Core.Tests;

/// <summary>
/// The on-demand file review (workplan task 43): headless Claude Code, driven through the same
/// process seam as the git tools.
/// <para>
/// Two properties are worth defending here, and neither is about the review itself. The first is
/// that the subprocess is read-only by construction - the allow-list is what makes running a
/// coding agent on the host defensible, and a test is the only thing that stops <c>Bash</c>
/// quietly appearing in it. The second is that nothing throws: this is reached from a button on
/// a viewer window, and every failure has to come back as a result that explains itself.
/// </para>
/// </summary>
public sealed class FileReviewerTests
{
    private const string Version = "2.1.221 (Claude Code)";

    [Fact]
    public async Task The_reviewer_can_only_read()
    {
        // The whole safety argument for shelling out to a coding agent on the host. If this test
        // fails because someone added a tool, that is the test working.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Envelope());

        await Reviewer(runner, workspace).ReviewAsync(new FileReviewRequest("src/A.cs"));

        List<string> arguments = [.. runner.Requests[^1].Arguments];
        string tools = arguments[arguments.IndexOf("--allowedTools") + 1];

        tools.Split(',').ShouldBe(["Read", "Grep", "Glob"]);
        tools.ShouldNotContain("Bash");
        tools.ShouldNotContain("Write");
        tools.ShouldNotContain("Edit");
        arguments[arguments.IndexOf("--permission-mode") + 1].ShouldBe("plan");
    }

    [Fact]
    public async Task The_directive_goes_on_standard_input_rather_than_the_command_line()
    {
        // Arguments are readable by anything that can list processes, and the directive carries
        // whatever the operator typed into the focus box.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Envelope());

        await Reviewer(runner, workspace).ReviewAsync(
            new FileReviewRequest("src/A.cs") { Instructions = "look at the threading" });

        ProcessRunRequest launch = runner.Requests[^1];
        launch.StandardInput.ShouldNotBeNull();
        launch.StandardInput.ShouldContain("src/A.cs");
        launch.StandardInput.ShouldContain("look at the threading");
        launch.Arguments.ShouldNotContain(a => a.Contains("look at the threading", StringComparison.Ordinal));
        launch.WorkingDirectory.ShouldBe(workspace.Guard().RepoRoot);
    }

    [Fact]
    public async Task Extra_roots_are_passed_last_because_the_flag_is_variadic()
    {
        // --add-dir takes any number of values, so anything after it is eaten as another
        // directory rather than read as the flag it is.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Envelope());
        FileReviewOptions options = new();
        options.AddDirectories.Add("../Sibling");

        await Reviewer(runner, workspace, options).ReviewAsync(new FileReviewRequest("src/A.cs"));

        IReadOnlyList<string> arguments = runner.Requests[^1].Arguments;
        arguments[^2].ShouldBe("--add-dir");
        arguments[^1].ShouldBe("../Sibling");
    }

    [Fact]
    public async Task No_key_is_injected_unless_one_was_configured()
    {
        // The CLI has its own credentials. Handing it a key it did not ask for silently moves
        // where the run is billed, so the default has to be "leave it alone".
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Envelope());

        await Reviewer(runner, workspace).ReviewAsync(new FileReviewRequest("src/A.cs"));

        runner.Requests[^1].Environment.ShouldBeNull();
    }

    [Fact]
    public async Task A_configured_key_is_injected_through_the_environment()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Envelope());
        FileReviewOptions options = new() { ApiKeyEnvironmentVariable = "GLASSCODER_TEST_REVIEW_KEY" };

        Environment.SetEnvironmentVariable("GLASSCODER_TEST_REVIEW_KEY", "sk-ant-test");
        try
        {
            await Reviewer(runner, workspace, options).ReviewAsync(new FileReviewRequest("src/A.cs"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLASSCODER_TEST_REVIEW_KEY", null);
        }

        ProcessRunRequest launch = runner.Requests[^1];
        IReadOnlyDictionary<string, string?> environment = launch.Environment.ShouldNotBeNull();
        environment["ANTHROPIC_API_KEY"].ShouldBe("sk-ant-test");
        launch.Arguments.ShouldNotContain(a => a.Contains("sk-ant-test", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Actions_come_back_ranked_and_capped()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0, Envelope(
            report: "# Findings\n\nThe guard is missing.",
            actions: """
                {"id":"tidy","title":"Rename","detail":"","priority":"Optional"},
                {"id":"guard","title":"Reject '..'","detail":"line 233","priority":"High"},
                {"id":"cover","title":"Add a test","detail":"","priority":"Medium"}
                """));
        FileReviewOptions options = new() { MaxActions = 2 };

        FileReview review = await Reviewer(runner, workspace, options)
            .ReviewAsync(new FileReviewRequest("src/A.cs"));

        review.Reviewed.ShouldBeTrue();
        review.Report.ShouldContain("The guard is missing.");
        review.EstimatedCostUsd.ShouldBe(0.214m);
        review.SessionId.ShouldBe("sess-1");

        // Highest first, and cut at the cap rather than at whatever order the model chose.
        review.Actions.Select(a => a.Id).ShouldBe(["guard", "cover"]);
        review.Actions[0].Priority.ShouldBe(ReviewActionPriority.High);
    }

    [Fact]
    public async Task An_answer_in_prose_is_kept_as_the_report_rather_than_thrown_away()
    {
        // --json-schema is version-dependent. An older CLI that ignored it would otherwise turn
        // a perfectly readable review into a hard failure.
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(0,
            """{"is_error":false,"result":"The file is fine, but the guard is missing."}""");

        FileReview review = await Reviewer(runner, workspace).ReviewAsync(new FileReviewRequest("src/A.cs"));

        review.Reviewed.ShouldBeTrue();
        review.Report.ShouldContain("the guard is missing");
        review.Actions.ShouldBeEmpty();
        review.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_failed_run_is_reported_rather_than_thrown()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed().Enqueue(1, standardError: "credit balance too low");

        FileReview review = await Reviewer(runner, workspace).ReviewAsync(new FileReviewRequest("src/A.cs"));

        review.Reviewed.ShouldBeFalse();
        review.Failure.ShouldContain("credit balance too low");
    }

    [Fact]
    public async Task A_missing_cli_is_reported_before_anything_is_launched()
    {
        // The point of the probe: the button greys out with a reason instead of failing on press.
        using TempWorkspace workspace = new();
        ThrowingProcessRunner runner = new();

        ClaudeCodeFileReviewer reviewer = Reviewer(runner, workspace);
        ReviewerAvailability availability = await reviewer.ProbeAsync();
        FileReview review = await reviewer.ReviewAsync(new FileReviewRequest("src/A.cs"));

        availability.IsAvailable.ShouldBeFalse();
        availability.Reason.ShouldContain("Install Claude Code");
        review.Reviewed.ShouldBeFalse();
        runner.Attempts.ShouldBe(1, "the probe is cached, so a failed probe is not retried per review");
    }

    [Fact]
    public async Task Switching_the_feature_off_launches_nothing()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = new();

        FileReview review = await Reviewer(runner, workspace, new FileReviewOptions { Enabled = false })
            .ReviewAsync(new FileReviewRequest("src/A.cs"));

        review.Reviewed.ShouldBeFalse();
        runner.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_review_that_outran_its_timeout_says_so()
    {
        using TempWorkspace workspace = new();
        FakeProcessRunner runner = Probed();
        runner.Default = new ProcessRunResult(-1, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: true);

        FileReview review = await Reviewer(runner, workspace).ReviewAsync(new FileReviewRequest("src/A.cs"));

        review.Reviewed.ShouldBeFalse();
        review.Failure.ShouldContain("TimeoutSeconds");
    }

    private static ClaudeCodeFileReviewer Reviewer(
        IProcessRunner runner, TempWorkspace workspace, FileReviewOptions? options = null) =>
        new(runner, workspace.Guard(), TempWorkspace.Wrap(options ?? new FileReviewOptions()));

    /// <summary>A runner whose first scripted answer is the version probe.</summary>
    private static FakeProcessRunner Probed() => new FakeProcessRunner().Enqueue(0, Version);

    /// <summary>The CLI's --output-format json envelope, as the reviewer expects to find it.</summary>
    private static string Envelope(
        string report = "# Findings",
        string actions = """{"id":"guard","title":"Reject '..'","detail":"line 233","priority":"High"}""") =>
        // Concatenated rather than interpolated: the JSON's own braces fight raw-string
        // interpolation, and escaping them is less readable than this.
        "{\"type\":\"result\",\"is_error\":false,\"session_id\":\"sess-1\",\"total_cost_usd\":0.214,"
        + "\"result\":\"see structured output\","
        + "\"structured_output\":{\"report\":" + System.Text.Json.JsonSerializer.Serialize(report)
        + ",\"actions\":[" + actions + "]}}";

    /// <summary>Stands in for a CLI that is not installed: launching it throws.</summary>
    private sealed class ThrowingProcessRunner : IProcessRunner
    {
        public int Attempts { get; private set; }

        public Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new System.ComponentModel.Win32Exception("The system cannot find the file specified.");
        }
    }
}
