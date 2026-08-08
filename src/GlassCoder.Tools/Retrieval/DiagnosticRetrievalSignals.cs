using System.Text.RegularExpressions;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Tools.Retrieval;

/// <summary>
/// Decides when the answer is genuinely outside the workspace (workplan task 59).
/// <para>
/// The distinction the policy needs is between a symbol this repository should know about and
/// one it cannot: <c>CS0246</c> on a package type is a question documentation answers, and
/// <c>CS0103</c> on a class the model wrote four steps ago is a typo that documentation makes
/// worse. The harness already computes the difference - the pre-write gate uses it to word its
/// refusals - so this asks that machinery rather than growing a second opinion about it.
/// </para>
/// <para>
/// Fed by whoever ran the last verification. Nothing pushes into it on a schedule, because a
/// signal that outlives the diagnostic that raised it would admit retrieval for a build that
/// went green two steps ago.
/// </para>
/// </summary>
public sealed class DiagnosticRetrievalSignals : IRetrievalSignals
{
    /// <summary>
    /// Errors that mean "this name resolves to nothing here". Deliberately short: every entry
    /// is a code whose cause can be a type the workspace never declares.
    /// </summary>
    private static readonly HashSet<string> UnresolvedNameCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // The type or namespace could not be found - the archetypal missing-package error.
        "CS0246",

        // No such member on a type that does exist: an API shape question.
        "CS1061",

        // An extension method is missing its using, which is usually a package's namespace.
        "CS1929",

        // The name does not exist in the current context. Included, but it is the code most
        // often raised by the model's own typo, so it only signals when the name is not one the
        // workspace declares - see Indicates.
        "CS0103",
    };

    /// <summary>The identifier a diagnostic is complaining about, in quotes in every message.</summary>
    private static readonly Regex Quoted = new(
        "'(?<name>[A-Za-z_][A-Za-z0-9_.<>]*)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Lock _gate = new();
    private string? _indication;

    /// <inheritdoc />
    public bool ExternalKnowledgeIndicated
    {
        get
        {
            lock (_gate)
            {
                return _indication is not null;
            }
        }
    }

    /// <inheritdoc />
    public string? Indication
    {
        get
        {
            lock (_gate)
            {
                return _indication;
            }
        }
    }

    /// <summary>
    /// Records what the last verification found. A clean result clears the signal: a run whose
    /// build is green has no unanswered external question, whatever it was asking a moment ago.
    /// </summary>
    /// <param name="diagnostics">What the compiler said.</param>
    /// <param name="declaredInWorkspace">
    /// Whether a name is declared by some source file here. The pre-write gate already answers
    /// this through <see cref="SymbolHints"/>; passing it in keeps this class free of the
    /// analyzer and therefore cheap to test.
    /// </param>
    public void Observe(IEnumerable<CodeDiagnostic> diagnostics, Func<string, bool> declaredInWorkspace)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(declaredInWorkspace);

        string? found = null;

        foreach (CodeDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity != CodeSeverity.Error || !UnresolvedNameCodes.Contains(diagnostic.Id))
            {
                continue;
            }

            foreach (Match match in Quoted.Matches(diagnostic.Message))
            {
                string name = match.Groups["name"].Value;

                // The whole test. A name this repository declares is a name whose answer is
                // here, and no amount of documentation improves on reading the file.
                if (declaredInWorkspace(name))
                {
                    continue;
                }

                found = $"{diagnostic.Id} on '{name}', which no source in this workspace declares";
                break;
            }

            if (found is not null)
            {
                break;
            }
        }

        lock (_gate)
        {
            _indication = found;
        }
    }

    /// <summary>
    /// Marks external knowledge as required for reasons outside the compiler - a suite task
    /// flagged <c>RequiresExternalDocs</c>, which is how an arm measures retrieval on a fixture
    /// built to need it.
    /// </summary>
    public void Require(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        lock (_gate)
        {
            _indication = reason;
        }
    }

    /// <summary>Forgets the signal. A green climb has no unanswered external question.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _indication = null;
        }
    }
}
