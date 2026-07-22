namespace GlassCoder.Host;

/// <summary>
/// Exit codes the console host returns (CLAUDE.md §17, workplan task 30).
/// <para>
/// Distinct codes because CI has to branch on them: "the agent could not solve it" and "the
/// configuration is wrong" call for completely different responses, and collapsing both into 1
/// makes a pipeline retry a run that will never pass.
/// </para>
/// </summary>
public static class HostExitCode
{
    /// <summary>The run finished and, where an oracle existed, it passed.</summary>
    public const int Success = 0;

    /// <summary>The run finished but the oracle did not pass.</summary>
    public const int TaskFailed = 1;

    /// <summary>The command line or configuration was wrong. Retrying will not help.</summary>
    public const int ConfigurationError = 2;

    /// <summary>A budget or limit stopped the run before it could finish.</summary>
    public const int LimitExceeded = 3;

    /// <summary>The model endpoint could not be reached, or failed the call.</summary>
    public const int ModelError = 4;

    /// <summary>The run was cancelled.</summary>
    public const int Cancelled = 5;

    /// <summary>Something unexpected went wrong inside the harness itself.</summary>
    public const int InternalError = 70;
}
