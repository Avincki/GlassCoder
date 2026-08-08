using System.Text.RegularExpressions;
using GlassCoder.Tools.Changes;
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

        // CS0103 - "the name does not exist in the current context" - is deliberately absent.
        // It was here, guarded by the declaredInWorkspace predicate, and the guard cannot work:
        // that predicate is FindSymbolTool.Declares, which reads the outline and therefore sees
        // only member declarations. A local, a parameter and a using-alias are all "not declared
        // here", so a mistyped local - `var resutl = ...; return result;` - read as a question
        // for the documentation, which is the exact case this class exists to keep out. The
        // three codes above are type- and member-shaped, where the predicate does hold.
    };

    /// <summary>The identifier a diagnostic is complaining about, in quotes in every message.</summary>
    private static readonly Regex Quoted = new(
        "'(?<name>[A-Za-z_][A-Za-z0-9_.<>]*)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>How many runs' signals to keep, bounded like the policy's budgets.</summary>
    private const int MaximumTrackedRuns = 64;

    private readonly Lock _gate = new();

    /// <summary>
    /// One signal per run, not one signal.
    /// <para>
    /// This is a singleton, and a single field made one run's diagnostic admit retrieval in
    /// another: during a fan-out, sub-agent A's CS0246 was the reason sub-agent B - whose build
    /// was green - was allowed to call out. The gate that <c>AllowProactive = false</c> exists to
    /// enforce then silently stops holding, and the metrics record no refusal because none
    /// happened.
    /// </para>
    /// </summary>
    private readonly Dictionary<string, string> _indications = new(StringComparer.Ordinal);

    private readonly List<string> _order = [];

    /// <inheritdoc />
    public bool ExternalKnowledgeIndicated => Indication is not null;

    /// <inheritdoc />
    public string? Indication
    {
        get
        {
            lock (_gate)
            {
                return _indications.GetValueOrDefault(RunContext.Current.RunId);
            }
        }
    }

    /// <summary>
    /// Records what a verification found.
    /// </summary>
    /// <param name="diagnostics">What the compiler said.</param>
    /// <param name="declaredInWorkspace">
    /// Whether a name is declared by some source file here. The pre-write gate already answers
    /// this through <see cref="SymbolHints"/>; passing it in keeps this class free of the
    /// analyzer and therefore cheap to test.
    /// </param>
    /// <param name="complete">
    /// Whether this is everything the compiler had to say - a build or a whole-project compile -
    /// rather than one file's syntax or the errors a single edit introduced.
    /// <para>
    /// Only a complete result may clear the signal, and that distinction is load-bearing. Every
    /// call to the summarizer used to clear it, including the narrow batches the pre-write gate
    /// produces, so the ordinary repair sequence erased its own admission: a build raises CS0246,
    /// the next edit is refused for an unrelated CS1002, that refusal's two-diagnostic batch
    /// clears the signal, and the retrieval call the CS0246 justified is then refused as not
    /// indicated. With <c>AllowProactive</c> false by default, that made the feature close to
    /// unreachable.
    /// </para>
    /// </param>
    public void Observe(
        IEnumerable<CodeDiagnostic> diagnostics,
        Func<string, bool> declaredInWorkspace,
        bool complete = false)
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

        if (found is not null)
        {
            Set(found);
        }
        else if (complete)
        {
            // A green build has no unanswered external question, whatever it was asking a moment
            // ago. A narrow batch that found nothing has no opinion either way.
            Clear();
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
        Set(reason);
    }

    /// <summary>Forgets this run's signal. A green climb has no unanswered external question.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _indications.Remove(RunContext.Current.RunId);
        }
    }

    private void Set(string indication)
    {
        lock (_gate)
        {
            string runId = RunContext.Current.RunId;
            if (!_indications.ContainsKey(runId))
            {
                if (_order.Count >= MaximumTrackedRuns)
                {
                    _indications.Remove(_order[0]);
                    _order.RemoveAt(0);
                }

                _order.Add(runId);
            }

            _indications[runId] = indication;
        }
    }
}
