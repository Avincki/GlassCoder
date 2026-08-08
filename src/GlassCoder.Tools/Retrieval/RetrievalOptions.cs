using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Retrieval;

/// <summary>Which upstream a retrieval tool speaks to.</summary>
public enum RetrievalServer
{
    /// <summary>Microsoft Learn. A trusted publisher with one versioned answer.</summary>
    Learn,

    /// <summary>Public GitHub. Useful, and categorically not a trusted publisher.</summary>
    GitHub,
}

/// <summary>
/// How a retrieval call reaches its upstream. Orthogonal to which server is switched on: Learn
/// enabled in <see cref="Live"/> is still a non-reproducible arm.
/// </summary>
public enum RetrievalMode
{
    /// <summary>
    /// Served from the cache; a miss is a hard failure and never a live call. What
    /// <c>suite</c> and <c>ablate</c> run, because a cache that quietly reaches the network
    /// gives non-reproducible runs and the belief that they are reproducible.
    /// </summary>
    Replay,

    /// <summary>Call out, and write every exchange to the cache. Run once, to build a corpus.</summary>
    Record,

    /// <summary>Call out without recording. Interactive desktop work.</summary>
    Live,
}

/// <summary>
/// Retrieval over MCP (workplan tasks 54-63).
/// <para>
/// Two levels of switch, because they answer different questions. <see cref="Enabled"/> is the
/// kill switch — off means nothing is constructed and nothing is registered. The per-server
/// flags are the levers an arm moves, so "does Microsoft Learn help" and "does public code
/// search help" are two questions with two numbers.
/// </para>
/// </summary>
public sealed class RetrievalOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Retrieval";

    /// <summary>
    /// Whether any retrieval tool is registered at all. Off by default, like <c>bash</c> and the
    /// git tools — and off means <em>absent from the schema</em>, not present and refusing. A
    /// tool the model can see is a tool it can pick, so a switch that only blocked execution
    /// would measure nothing.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>How calls reach their upstream. Replay is the default because the Lab is.</summary>
    public RetrievalMode Mode { get; set; } = RetrievalMode.Replay;

    /// <summary>
    /// Upstream calls one run may make. Small on purpose: retrieval is a hint, and a run that
    /// spends five steps searching has stopped doing the task.
    /// </summary>
    public int MaxCallsPerRun { get; set; } = 3;

    /// <summary>
    /// Hard cap on characters one call may return into the conversation. Uncapped article
    /// injection is how run c5eb67f6 met the token limit.
    /// </summary>
    public int MaxResultChars { get; set; } = 3000;

    /// <summary>
    /// Whether the model may retrieve without a signal that external knowledge is actually
    /// needed. False by default: models over-call optional tools, and three transcripts show
    /// this one answering a refutation by reaching for tools rather than fixing anything.
    /// <para>
    /// Until task 59 wires real diagnostics into <see cref="IRetrievalSignals"/>, nothing
    /// supplies a signal — so this is also the switch that makes a live trial possible before
    /// then, and an arm that wants to measure unrestricted retrieval sets it deliberately.
    /// </para>
    /// </summary>
    public bool AllowProactive { get; set; }

    /// <summary>
    /// Admitted calls allowed to accumulate without any change being applied. The anti-search
    /// loop: a run that retrieves twice and writes nothing is not researching, it is stalling.
    /// Zero disables the check.
    /// </summary>
    public int MaxCallsWithoutAppliedChange { get; set; } = 2;

    /// <summary>
    /// Where recorded exchanges live. Relative or empty anchors under the app data root, like
    /// every other directory the harness owns.
    /// </summary>
    public string CacheDirectory { get; set; } = string.Empty;

    /// <summary>Microsoft Learn: authoritative, versioned, and free of an injection surface.</summary>
    public RetrievalServerOptions Learn { get; } = new()
    {
        Endpoint = "https://learn.microsoft.com/api/mcp",
    };

    /// <summary>
    /// Public GitHub. Read-only by default and by intent: search and read, never a write to
    /// somebody else's system.
    /// </summary>
    public RetrievalServerOptions GitHub { get; } = new()
    {
        Endpoint = "https://api.githubcopilot.com/mcp/",
        ApiKeyEnvironmentVariable = "GLASSCODER_GITHUB_TOKEN",
    };

    /// <summary>The options for one server.</summary>
    public RetrievalServerOptions For(RetrievalServer server) =>
        server == RetrievalServer.Learn ? Learn : GitHub;

    /// <summary>Servers switched on, in the order their tools are advertised.</summary>
    public IEnumerable<RetrievalServer> EnabledServers()
    {
        if (Learn.Enabled)
        {
            yield return RetrievalServer.Learn;
        }

        if (GitHub.Enabled)
        {
            yield return RetrievalServer.GitHub;
        }
    }
}

/// <summary>One MCP server, and the narrow slice of it worth registering.</summary>
public sealed class RetrievalServerOptions
{
    /// <summary>Whether this server's tools are registered. Independently switchable, so an
    /// ablation arm can move exactly one of them.</summary>
    public bool Enabled { get; set; }

    /// <summary>Where the server answers.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// Whether to ask the server for a read-only session. Kept true: retrieval reads, and a
    /// tool that can open an issue is a different feature with a different risk.
    /// </summary>
    public bool ReadOnly { get; set; } = true;

    /// <summary>
    /// Environment variable holding the bearer token, when the server needs one. The token
    /// never lives in a settings file — <c>.glasscoder.json</c> is meant to be committed.
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>
    /// The tools to register, and what to call them. Not a catalogue: GitHub advertises 27
    /// tools totalling 27,431 characters, nearly twice this harness's entire tool block, so the
    /// allow-list is the difference between a feature and a budget breach (task 54).
    /// </summary>
    public IList<RetrievalToolOptions> Tools { get; } = [];
}

/// <summary>
/// One advertised tool, renamed and re-described locally.
/// <para>
/// The description is ours because a server's is prompt written by someone optimising for a
/// different agent, and it lands in the model's context on every request. Learn's three
/// descriptions are 2,675 characters against 900 of schema — three quarters of what that server
/// would cost is prose we did not write and do not need (task 54). It lives in configuration
/// rather than in code so that wording is an ablation lever, which is what this application is
/// for.
/// </para>
/// </summary>
public sealed class RetrievalToolOptions
{
    /// <summary>The name the server advertises, matched exactly.</summary>
    public string ServerTool { get; set; } = string.Empty;

    /// <summary>The name the model is shown. Namespaced, so transcripts stay filterable.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What the model is told the tool is for. Short: this is prefill, every step.</summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Refuses a configuration that would fail later and less clearly - at first call, mid-run,
/// instead of at startup (CLAUDE.md §13).
/// </summary>
public sealed class RetrievalOptionsValidator : IValidateOptions<RetrievalOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RetrievalOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Nothing is constructed when the master switch is off, so nothing here can be wrong.
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        if (options.MaxCallsPerRun < 0)
        {
            failures.Add("Retrieval:MaxCallsPerRun cannot be negative.");
        }

        if (options.MaxResultChars <= 0)
        {
            failures.Add("Retrieval:MaxResultChars must be positive - a zero cap returns nothing.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (RetrievalServer server in options.EnabledServers())
        {
            RetrievalServerOptions settings = options.For(server);
            string section = $"Retrieval:{server}";

            if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out _))
            {
                failures.Add($"{section}:Endpoint is not an absolute URI: '{settings.Endpoint}'.");
            }

            if (settings.Tools.Count == 0)
            {
                failures.Add($"{section} is enabled but lists no tools. Registering a server's whole " +
                    "surface is never right; name the two or three worth their schema.");
            }

            foreach (RetrievalToolOptions tool in settings.Tools)
            {
                if (string.IsNullOrWhiteSpace(tool.ServerTool) || string.IsNullOrWhiteSpace(tool.Name))
                {
                    failures.Add($"{section}:Tools needs both ServerTool and Name on every entry.");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tool.Description))
                {
                    failures.Add($"{section}:Tools '{tool.Name}' has no Description. The server's own " +
                        "would be used instead, which is the cost this allow-list exists to avoid.");
                }

                if (!names.Add(tool.Name))
                {
                    failures.Add($"Retrieval tool name '{tool.Name}' is declared more than once; the " +
                        "registry rejects duplicates at startup.");
                }
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
