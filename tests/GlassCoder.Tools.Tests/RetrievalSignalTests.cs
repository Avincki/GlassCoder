using GlassCoder.Tools.Retrieval;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// What admits a lookup (workplan task 59): a compile error naming something no source here
/// declares. The distinction is the whole feature - a missing package type is a question
/// documentation answers, and the model's own typo is one documentation makes worse.
/// </summary>
public sealed class RetrievalSignalTests
{
    [Fact]
    public void An_unresolved_type_no_workspace_source_declares_indicates_retrieval()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Observe(
            [Error("CS0246", "The type or namespace name 'ChannelReader' could not be found")],
            declaredInWorkspace: _ => false);

        signals.ExternalKnowledgeIndicated.ShouldBeTrue();
        signals.Indication.ShouldContain("ChannelReader");
        signals.Indication.ShouldContain("CS0246");
    }

    /// <summary>
    /// The failure to design against. Documentation cannot help with a name the model itself
    /// invented four steps ago, and admitting a lookup for it spends a step to learn nothing.
    /// </summary>
    [Fact]
    public void A_name_this_workspace_declares_does_not_indicate_retrieval()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Observe(
            [Error("CS0103", "The name 'MultiplyViewModel' does not exist in the current context")],
            declaredInWorkspace: name => name == "MultiplyViewModel");

        signals.ExternalKnowledgeIndicated.ShouldBeFalse();
    }

    [Fact]
    public void A_missing_member_on_a_known_type_indicates_retrieval()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Observe(
            [Error("CS1061", "'Channel<int>' does not contain a definition for 'ReadAllAsync'")],
            declaredInWorkspace: _ => false);

        signals.ExternalKnowledgeIndicated.ShouldBeTrue();
    }

    [Fact]
    public void Errors_that_are_not_about_a_missing_name_indicate_nothing()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Observe(
            [Error("CS1002", "; expected"), Error("CS0161", "not all code paths return a value")],
            declaredInWorkspace: _ => false);

        signals.ExternalKnowledgeIndicated.ShouldBeFalse();
    }

    [Fact]
    public void Warnings_do_not_indicate_retrieval()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Observe(
            [new CodeDiagnostic("CS0246", CodeSeverity.Warning, "The type or namespace name 'Foo' could not be found")],
            declaredInWorkspace: _ => false);

        signals.ExternalKnowledgeIndicated.ShouldBeFalse();
    }

    /// <summary>A run whose build is green has no unanswered external question.</summary>
    [Fact]
    public void A_clean_verification_clears_the_signal()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Observe([Error("CS0246", "The type or namespace name 'Widget' could not be found")], _ => false);
        signals.ExternalKnowledgeIndicated.ShouldBeTrue();

        signals.Observe([], _ => false);
        signals.ExternalKnowledgeIndicated.ShouldBeFalse();
    }

    /// <summary>
    /// The suite path: a fixture built to need external knowledge says so directly, rather than
    /// waiting for a compiler to raise the right error (workplan task 60).
    /// </summary>
    [Fact]
    public void A_suite_task_can_require_retrieval_outright()
    {
        DiagnosticRetrievalSignals signals = new();

        signals.Require("suite task declares RequiresExternalDocs");

        signals.ExternalKnowledgeIndicated.ShouldBeTrue();
        signals.Indication.ShouldContain("RequiresExternalDocs");

        signals.Clear();
        signals.ExternalKnowledgeIndicated.ShouldBeFalse();
    }

    /// <summary>
    /// End to end through the policy: the signal is what turns the default refusal into an
    /// admission, with AllowProactive still false.
    /// </summary>
    [Fact]
    public void The_signal_is_what_the_policy_admits_on()
    {
        DiagnosticRetrievalSignals signals = new();
        RetrievalOptions options = new() { Enabled = true, AllowProactive = false, MaxCallsWithoutAppliedChange = 0 };
        options.Learn.Enabled = true;

        RetrievalPolicy policy = new(new Monitor(options), signals, new Changes.ChangeLog());
        RetrievalRequest request = new(RetrievalServer.Learn, "learn_search", RetrievalReason.UnknownApi);

        policy.TryAdmit(request, out RetrievalDenial? denial).ShouldBeFalse();
        denial!.Code.ShouldBe(ToolErrorCodes.RetrievalNotIndicated);

        signals.Observe([Error("CS0246", "The type or namespace name 'Channel' could not be found")], _ => false);

        policy.TryAdmit(request, out _).ShouldBeTrue();
    }

    private static CodeDiagnostic Error(string id, string message) =>
        new(id, CodeSeverity.Error, message);

    private sealed class Monitor(RetrievalOptions options)
        : Microsoft.Extensions.Options.IOptionsMonitor<RetrievalOptions>
    {
        public RetrievalOptions CurrentValue => options;

        public RetrievalOptions Get(string? name) => options;

        public IDisposable? OnChange(Action<RetrievalOptions, string?> listener) => null;
    }
}
