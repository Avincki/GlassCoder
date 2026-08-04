using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using GlassCoder.Tools.Guardrails;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// In-process Roslyn implementation of rungs 1 and 2 (workplan task 14).
/// </summary>
/// <remarks>
/// <para>
/// The compile rung is deliberately <em>approximate</em>. It parses the C# it finds under a
/// project directory and compiles it against the harness's own reference assemblies, because
/// resolving a real MSBuild graph costs seconds and drags in a whole toolchain. It catches what
/// it is meant to catch - hallucinated APIs, wrong signatures, type errors - in well under a
/// second, and <c>dotnet build</c> (task 17) remains the authoritative gate that matches CI
/// exactly (CLAUDE.md §8.1).
/// </para>
/// <para>
/// Parsed syntax trees are cached against file timestamps, so re-checking one edit in a large
/// project re-parses one file rather than all of them.
/// </para>
/// </remarks>
public sealed class RoslynCodeAnalyzer : ICodeAnalyzer
{
    /// <summary>
    /// The global usings <c>Microsoft.NET.Sdk</c> generates when <c>ImplicitUsings</c> is on.
    /// <para>
    /// The SDK writes these into <c>obj/</c>, which the workspace deny list excludes from every
    /// access - so without synthesising them here, this compilation sees a project whose files
    /// have no <c>using System;</c> anywhere. Existing files are unaffected, because their
    /// resulting errors are present before and after an edit alike and only <em>introduced</em>
    /// errors gate. New code is not so lucky: a new file is the one place fresh
    /// <c>System</c> references appear, so every well-formed new file was being refused.
    /// </para>
    /// <para>
    /// Deliberately only the base SDK's set. The Web and Worker SDKs add namespaces that live in
    /// packages this compilation does not reference, so emitting those would manufacture CS0246s
    /// of our own making.
    /// </para>
    /// </summary>
    private const string ImplicitUsingsSource = """
        global using global::System;
        global using global::System.Collections.Generic;
        global using global::System.IO;
        global using global::System.Linq;
        global using global::System.Net.Http;
        global using global::System.Threading;
        global using global::System.Threading.Tasks;
        """;

    private static readonly CSharpParseOptions ParseOptions =
        new(LanguageVersion.Preview, DocumentationMode.None);

    private static readonly Lazy<List<MetadataReference>> FrameworkReferences =
        new(LoadFrameworkReferences, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly ConcurrentDictionary<string, CachedTree> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly IPathGuard _guard;
    private readonly VerificationOptions _options;
    private readonly ILogger<RoslynCodeAnalyzer> _logger;

    /// <summary>Creates the analyzer.</summary>
    public RoslynCodeAnalyzer(
        IPathGuard guard,
        IOptions<VerificationOptions> options,
        ILogger<RoslynCodeAnalyzer>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _options = options.Value;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RoslynCodeAnalyzer>.Instance;
    }

    /// <inheritdoc />
    public bool Handles(string filePath) =>
        !string.IsNullOrWhiteSpace(filePath) &&
        Path.GetExtension(filePath).Equals(".cs", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public DiagnosticReport CheckSyntax(string filePath, string text)
    {
        long start = Stopwatch.GetTimestamp();

        if (!Handles(filePath))
        {
            return DiagnosticReport.Success(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: filePath);
        List<CodeDiagnostic> diagnostics = Convert(tree.GetDiagnostics());

        return DiagnosticReport.FromDiagnostics(diagnostics, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <inheritdoc />
    public Task<DiagnosticReport> CheckEditAsync(
        string filePath,
        string proposedText,
        CancellationToken cancellationToken = default)
    {
        if (!Handles(filePath))
        {
            return Task.FromResult(DiagnosticReport.Success());
        }

        string full = Path.GetFullPath(filePath);
        string? projectDirectory = FindProjectDirectory(full);
        if (projectDirectory is null)
        {
            return Task.FromResult(DiagnosticReport.Inconclusive(
                $"No project directory found above '{_guard.ToRelativePath(full)}'."));
        }

        return Task.Run(
            () => Compile(projectDirectory, full, proposedText, cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<DiagnosticReport> CompileAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        PathGuardResult verdict = _guard.Resolve(projectDirectory, PathAccess.Read);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Task.FromResult(DiagnosticReport.Inconclusive(verdict.Reason ?? "Path is not readable."));
        }

        // Callers name a build target, which is as often a .csproj or .sln as a directory. This
        // compiles a directory of sources, so take the one the target lives in - enumerating a
        // file as though it were a directory throws rather than returning nothing.
        string directory = Directory.Exists(verdict.FullPath)
            ? verdict.FullPath
            : Path.GetDirectoryName(verdict.FullPath) ?? verdict.FullPath;

        return Task.Run(
            () => Compile(directory, overridePath: null, overrideText: null, cancellationToken),
            cancellationToken);
    }

    private DiagnosticReport Compile(
        string projectDirectory,
        string? overridePath,
        string? overrideText,
        CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();

        List<SyntaxTree> trees = [];
        bool overrideApplied = false;

        foreach (string file in EnumerateSources(projectDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (trees.Count >= _options.MaxCompileFiles)
            {
                return DiagnosticReport.Inconclusive(
                    $"'{_guard.ToRelativePath(projectDirectory)}' has more than {_options.MaxCompileFiles} source files; " +
                    "run the build tool instead.",
                    Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            if (overridePath is not null && file.Equals(overridePath, StringComparison.OrdinalIgnoreCase))
            {
                trees.Add(CSharpSyntaxTree.ParseText(
                    overrideText ?? string.Empty, ParseOptions, path: file, cancellationToken: cancellationToken));
                overrideApplied = true;
            }
            else
            {
                SyntaxTree? tree = ParseCached(file, cancellationToken);
                if (tree is not null)
                {
                    trees.Add(tree);
                }
            }
        }

        // A brand-new file is not on disk yet, so it will not have been enumerated.
        if (overridePath is not null && !overrideApplied)
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                overrideText ?? string.Empty, ParseOptions, path: overridePath, cancellationToken: cancellationToken));
        }

        if (trees.Count == 0)
        {
            return DiagnosticReport.Inconclusive(
                $"No C# sources found under '{_guard.ToRelativePath(projectDirectory)}'.",
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        // Added after the emptiness check so it can never make an empty project look populated.
        if (ImplicitUsingsEnabled(projectDirectory))
        {
            trees.Add(CSharpSyntaxTree.ParseText(
                ImplicitUsingsSource,
                ParseOptions,
                path: Path.Combine(projectDirectory, "GlassCoder.ImplicitUsings.g.cs"),
                cancellationToken: cancellationToken));
        }

        (List<MetadataReference> metadata, HashSet<string> resolved) = References(projectDirectory);

        // Before trusting any diagnostic, ask whether this compilation could possibly be right.
        // The reference set is scavenged from build output, not evaluated from the project file,
        // so a project whose dependencies have not been built yet produces CS0246 for every type
        // it imports - including correct ones. Reporting that as "your file does not compile"
        // blocks the write, and the agent cannot fix an error that is not in its file.
        if (UnresolvedReferences(projectDirectory, resolved) is { } unresolved)
        {
            return DiagnosticReport.Inconclusive(unresolved, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        CSharpCompilation compilation = CSharpCompilation.Create(
            $"glasscoder-{Path.GetFileName(projectDirectory)}",
            trees,
            metadata,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));

        List<CodeDiagnostic> diagnostics = Convert(compilation.GetDiagnostics(cancellationToken));
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        _logger.LogDebug(
            "In-memory compile of {Project}: {FileCount} files, {ErrorCount} errors in {Elapsed:F0} ms",
            projectDirectory, trees.Count, diagnostics.Count(d => d.IsError), elapsed);

        return DiagnosticReport.FromDiagnostics(diagnostics, elapsed);
    }

    private IEnumerable<string> EnumerateSources(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            // The guard already excludes bin, obj and friends, and generated output would
            // otherwise be compiled twice.
            if (_guard.Resolve(file, PathAccess.Read).Allowed)
            {
                yield return Path.GetFullPath(file);
            }
        }
    }

    private SyntaxTree? ParseCached(string file, CancellationToken cancellationToken)
    {
        FileInfo info = new(file);
        if (!info.Exists)
        {
            return null;
        }

        if (_trees.TryGetValue(file, out CachedTree cached) &&
            cached.LastWriteUtc == info.LastWriteTimeUtc &&
            cached.Length == info.Length)
        {
            return cached.Tree;
        }

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        SyntaxTree tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: file, cancellationToken: cancellationToken);
        _trees[file] = new CachedTree(tree, info.LastWriteTimeUtc, info.Length);
        return tree;
    }

    /// <summary>
    /// Whether the project in this directory has <c>ImplicitUsings</c> switched on.
    /// </summary>
    /// <remarks>
    /// Reads the project file directly rather than evaluating MSBuild, so a value inherited from
    /// <c>Directory.Build.props</c> is not seen. That is the conservative direction to be wrong
    /// in: the usings are simply not synthesised, which is exactly today's behaviour.
    /// </remarks>
    private static bool ImplicitUsingsEnabled(string projectDirectory)
    {
        try
        {
            foreach (string project in Directory.EnumerateFiles(projectDirectory, "*.csproj"))
            {
                string? value = XDocument.Load(project)
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName.Equals("ImplicitUsings", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    .Trim();

                if (value is not null)
                {
                    return value.Equals("enable", StringComparison.OrdinalIgnoreCase) ||
                           value.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // A missing or malformed project file is not worth failing a compile over.
        }

        return false;
    }

    /// <summary>
    /// Walks up from a file to the nearest directory holding a project file, so "the project" is
    /// whatever the repository itself says it is.
    /// </summary>
    private static string? FindProjectDirectory(string filePath)
    {
        DirectoryInfo? directory = new FileInfo(filePath).Directory;
        while (directory is not null)
        {
            // A file that is being created may not have its directory yet, and enumerating one
            // that is not there throws. Walk past it: the project is further up regardless.
            if (directory.Exists && directory.EnumerateFiles("*.csproj").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// The assemblies to compile against, and the names of the ones that were scavenged from
    /// build output. The names are what tells the caller whether the set is complete: framework
    /// assemblies are always there, so only the scavenged ones say anything about this project.
    /// </summary>
    private (List<MetadataReference> Metadata, HashSet<string> Resolved) References(string projectDirectory)
    {
        List<MetadataReference> references = [.. FrameworkReferences.Value];
        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);

        foreach (string directory in _options.ExtraReferenceDirectories.Prepend(Path.Combine(projectDirectory, "bin")))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string dll in Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(dll));
                    resolved.Add(Path.GetFileNameWithoutExtension(dll));
                }
                catch (Exception ex) when (ex is IOException or BadImageFormatException)
                {
                    // A native or locked DLL in an output folder is not a reference; skip it.
                }
            }
        }

        return (references, resolved);
    }

    /// <summary>
    /// Why this compilation cannot be trusted, or null when it can.
    /// <para>
    /// Two cases, both meaning "the answer would be about the reference set, not about the code".
    /// A <c>ProjectReference</c> whose assembly is nowhere in the output is definitive. Packages
    /// are judged more coarsely - a package id is not an assembly name, so the only reliable
    /// reading is that nothing has been built yet at all.
    /// </para>
    /// </summary>
    private static string? UnresolvedReferences(string projectDirectory, HashSet<string> resolved)
    {
        foreach (string projectFile in ProjectLocator.EnumerateProjects(projectDirectory))
        {
            ProjectReferences declared = ProjectLocator.ReadReferences(projectFile);
            if (!declared.Any)
            {
                continue;
            }

            List<string> missing = [.. declared.Projects.Where(p => !resolved.Contains(p))];
            if (missing.Count > 0)
            {
                return $"'{Path.GetFileName(projectFile)}' references {string.Join(", ", missing)}, " +
                    "which has not been built yet, so an in-memory compile cannot see those types. " +
                    "Use the build tool for an authoritative answer.";
            }

            if (declared.Packages.Count > 0 && resolved.Count == 0)
            {
                return $"'{Path.GetFileName(projectFile)}' references NuGet packages that have not been " +
                    "restored or built yet, so an in-memory compile cannot see those types. " +
                    "Use the build tool for an authoritative answer.";
            }
        }

        return null;
    }

    private static List<MetadataReference> LoadFrameworkReferences()
    {
        List<MetadataReference> references = [];

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string trusted)
        {
            return references;
        }

        foreach (string path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(path))
                {
                    references.Add(MetadataReference.CreateFromFile(path));
                }
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException)
            {
                // Skip anything that is not a managed assembly.
            }
        }

        return references;
    }

    private static List<CodeDiagnostic> Convert(IEnumerable<Diagnostic> diagnostics)
    {
        List<CodeDiagnostic> converted = [];

        foreach (Diagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden)
            {
                continue;
            }

            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            bool hasLocation = diagnostic.Location.IsInSource;

            converted.Add(new CodeDiagnostic(
                diagnostic.Id,
                diagnostic.Severity switch
                {
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Error => CodeSeverity.Error,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => CodeSeverity.Warning,
                    Microsoft.CodeAnalysis.DiagnosticSeverity.Info => CodeSeverity.Info,
                    _ => CodeSeverity.Hidden,
                },
                diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                hasLocation ? span.Path : null,
                hasLocation ? span.StartLinePosition.Line + 1 : 0,
                hasLocation ? span.StartLinePosition.Character + 1 : 0));
        }

        return converted;
    }

    private readonly record struct CachedTree(SyntaxTree Tree, DateTime LastWriteUtc, long Length);
}
