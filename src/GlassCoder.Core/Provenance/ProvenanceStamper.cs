using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using GlassCoder.Core.Context;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Core.Provenance;

/// <summary>What a run was produced by and from (workplan task 35).</summary>
/// <param name="HarnessVersion">Version of the harness that ran.</param>
/// <param name="RepoCommit">Commit the working tree was on, when it could be read.</param>
/// <param name="RepoBranch">Branch name, when it could be read.</param>
/// <param name="ConfigHash">Hash of the effective configuration, so an arm is identifiable.</param>
/// <param name="ContextFresh">Whether the always-loaded context is at least as new as the code.</param>
/// <param name="StaleReason">Why the context is considered stale, when it is.</param>
/// <param name="StampedAt">When the stamp was taken.</param>
public sealed record ProvenanceStamp(
    string HarnessVersion,
    string? RepoCommit,
    string? RepoBranch,
    string ConfigHash,
    bool ContextFresh,
    string? StaleReason,
    DateTimeOffset StampedAt);

/// <summary>Freshness settings (CLAUDE.md §17 phase 6, workplan task 35).</summary>
public sealed class ProvenanceOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "GlassCoder:Provenance";

    /// <summary>Whether runs are stamped at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Paths the freshness check ignores. These are the harness's <em>own</em> output: counting
    /// them as source changes would make every run look stale immediately after the previous
    /// run wrote its logs - a trigger loop that never settles.
    /// </summary>
    public IList<string> TriggerExclusions { get; } =
    [
        "logs",
        "metrics",
        "runs",
        "bin",
        "obj",
        ".git",
    ];

    /// <summary>File extensions counted as source when judging freshness.</summary>
    public IList<string> SourceExtensions { get; } = [".cs", ".csproj", ".props", ".targets"];
}

/// <summary>Stamps runs with their provenance and judges context freshness.</summary>
public interface IProvenanceStamper
{
    /// <summary>Takes a stamp for the current working tree and configuration.</summary>
    ProvenanceStamp Stamp();
}

/// <summary>
/// Default <see cref="IProvenanceStamper"/> (workplan task 35).
/// <para>
/// Reads the commit straight out of <c>.git</c> rather than shelling out: it is faster, it works
/// with no git on the PATH, and provenance stamping must never itself be the thing that fails a
/// run. Freshness compares the always-loaded context against the newest source file, excluding
/// the harness's own output so a run cannot invalidate itself.
/// </para>
/// </summary>
public sealed class ProvenanceStamper : IProvenanceStamper
{
    private readonly IPathGuard _guard;
    private readonly ProvenanceOptions _options;
    private readonly ContextOptions _context;
    private readonly string _configHash;
    private readonly ILogger<ProvenanceStamper> _logger;

    /// <summary>Creates the stamper.</summary>
    public ProvenanceStamper(
        IPathGuard guard,
        IOptions<ProvenanceOptions> options,
        IOptions<ContextOptions> context,
        Microsoft.Extensions.Configuration.IConfiguration? configuration = null,
        ILogger<ProvenanceStamper>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);

        _guard = guard;
        _options = options.Value;
        _context = context.Value;
        _configHash = HashConfiguration(configuration);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ProvenanceStamper>.Instance;
    }

    /// <inheritdoc />
    public ProvenanceStamp Stamp()
    {
        (string? commit, string? branch) = ReadGitHead();
        (bool fresh, string? reason) = JudgeFreshness();

        return new ProvenanceStamp(
            typeof(ProvenanceStamper).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.0.0",
            commit,
            branch,
            _configHash,
            fresh,
            reason,
            DateTimeOffset.UtcNow);
    }

    private (string? Commit, string? Branch) ReadGitHead()
    {
        try
        {
            string gitDirectory = Path.Combine(_guard.RepoRoot, ".git");
            string headPath = Path.Combine(gitDirectory, "HEAD");
            if (!File.Exists(headPath))
            {
                return (null, null);
            }

            string head = File.ReadAllText(headPath).Trim();
            if (!head.StartsWith("ref:", StringComparison.Ordinal))
            {
                return (head, null);   // detached HEAD holds the commit directly
            }

            string reference = head[4..].Trim();
            string branch = reference.Split('/')[^1];
            string refPath = Path.Combine(gitDirectory, reference.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(refPath))
            {
                return (File.ReadAllText(refPath).Trim(), branch);
            }

            // Packed refs: the loose file is absent once git has packed it away.
            string packed = Path.Combine(gitDirectory, "packed-refs");
            if (File.Exists(packed))
            {
                foreach (string line in File.ReadLines(packed))
                {
                    if (line.EndsWith(" " + reference, StringComparison.Ordinal))
                    {
                        return (line.Split(' ')[0], branch);
                    }
                }
            }

            return (null, branch);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "Could not read git provenance");
            return (null, null);
        }
    }

    /// <summary>
    /// Context is fresh when it is at least as new as the newest source file. Stale context is
    /// the quiet failure mode this measures: the agent reasons confidently from a description of
    /// a codebase that has since moved on.
    /// </summary>
    private (bool Fresh, string? Reason) JudgeFreshness()
    {
        if (_context.RootContextFiles.Count == 0)
        {
            return (true, null);
        }

        DateTime newestContext = DateTime.MinValue;
        foreach (string file in _context.RootContextFiles)
        {
            PathGuardResult verdict = _guard.Resolve(file, PathAccess.Read);
            if (verdict.Allowed && verdict.FullPath is not null && File.Exists(verdict.FullPath))
            {
                newestContext = Max(newestContext, File.GetLastWriteTimeUtc(verdict.FullPath));
            }
        }

        if (newestContext == DateTime.MinValue)
        {
            return (false, "No root context file could be read.");
        }

        DateTime newestSource = DateTime.MinValue;
        string? newestPath = null;

        foreach (string file in EnumerateSource())
        {
            DateTime written = File.GetLastWriteTimeUtc(file);
            if (written > newestSource)
            {
                newestSource = written;
                newestPath = file;
            }
        }

        if (newestSource <= newestContext)
        {
            return (true, null);
        }

        return (false,
            $"Source changed after the context was written: '{_guard.ToRelativePath(newestPath!)}' " +
            $"at {newestSource.ToString("u", CultureInfo.InvariantCulture)} vs context at " +
            $"{newestContext.ToString("u", CultureInfo.InvariantCulture)}.");
    }

    private IEnumerable<string> EnumerateSource()
    {
        HashSet<string> extensions = new(_options.SourceExtensions, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> files;

        try
        {
            files = Directory.EnumerateFiles(_guard.RepoRoot, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            if (!extensions.Contains(Path.GetExtension(file)) || IsExcluded(file))
            {
                continue;
            }

            yield return file;
        }
    }

    private bool IsExcluded(string file)
    {
        string relative = _guard.ToRelativePath(file);
        foreach (string exclusion in _options.TriggerExclusions)
        {
            if (relative.StartsWith(exclusion + "/", StringComparison.OrdinalIgnoreCase) ||
                relative.Contains("/" + exclusion + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;

    private static string HashConfiguration(Microsoft.Extensions.Configuration.IConfiguration? configuration)
    {
        if (configuration is null)
        {
            return "unknown";
        }

        // Walks the whole tree so that any setting an arm changes changes the hash.
        List<string> entries = [];
        Flatten(configuration.GetSection("GlassCoder"), entries);
        entries.Sort(StringComparer.Ordinal);

        StringBuilder flattened = new();
        foreach (string entry in entries)
        {
            flattened.Append(entry).Append(';');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(flattened.ToString())))[..16];
    }

    private static void Flatten(Microsoft.Extensions.Configuration.IConfigurationSection section, List<string> entries)
    {
        if (section.Value is not null)
        {
            entries.Add($"{section.Path}={section.Value}");
        }

        foreach (Microsoft.Extensions.Configuration.IConfigurationSection child in section.GetChildren())
        {
            Flatten(child, entries);
        }
    }
}
