namespace GlassCoder.Tools.Registry;

/// <summary>
/// Thrown at registration time when a tool method breaks the contract in CLAUDE.md §7 -
/// a missing <c>[Description]</c>, a duplicate name, or a signature whose generated schema is
/// unusable.
/// <para>
/// This fires during startup, never mid-run: a contract defect is a build-time problem, and a
/// model must never discover it by receiving a malformed tool definition.
/// </para>
/// </summary>
public sealed class ToolContractException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public ToolContractException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and inner cause.</summary>
    public ToolContractException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Parameterless constructor required by the exception design guidelines.</summary>
    public ToolContractException()
    {
    }
}
