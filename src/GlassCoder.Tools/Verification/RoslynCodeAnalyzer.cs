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

    /// <summary>
    /// The parsed tree for a file on disk, or null when it cannot be read.
    /// <para>
    /// Exposed so structural tools (workplan task 47) sweep the workspace through the cache this
    /// class already keeps, rather than opening a second one. A repository-wide symbol search and
    /// a pre-write compile want the same trees, and parsing them twice would be paying twice for
    /// the same answer.
    /// </para>
    /// </summary>
    public SyntaxTree? ParseFile(string fullPath, CancellationToken cancellationToken = default) =>
        Handles(fullPath) ? ParseCached(fullPath, cancellationToken) : null;

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

        // WPF is the other place the SDK hides code this compilation needs: the markup compiler
        // declares InitializeComponent and every x:Name field in obj/, which the deny list
        // excludes. Run 5c071f37 refused one correct code-behind ten times over exactly that.
        if (UseWpfEnabled(projectDirectory))
        {
            string? xamlGap = AddXamlGeneratedPartials(projectDirectory, trees, cancellationToken);
            if (xamlGap is not null)
            {
                return DiagnosticReport.Inconclusive(xamlGap, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
        }

        (List<MetadataReference> metadata, Dictionary<string, DateTime> resolved) = References(projectDirectory);

        // Before trusting any diagnostic, ask whether this compilation could possibly be right.
        // The reference set is scavenged from build output, not evaluated from the project file,
        // so a project whose dependencies have not been built yet produces CS0246 for every type
        // it imports - including correct ones. Reporting that as "your file does not compile"
        // blocks the write, and the agent cannot fix an error that is not in its file.
        if (UnresolvedReferences(projectDirectory, resolved) is { } unresolved)
        {
            return DiagnosticReport.Inconclusive(unresolved, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        // A reference can also exist and be old. From run e8f9186a: a library gained a
        // parameter, and every edit fixing the call sites in its test project was refused with
        // "no overload takes 2 arguments" - judged against the library's last-built DLL, which
        // no tool call could refresh, because the test project cannot build until the very edit
        // being refused has landed. A gate that cannot know must not gate.
        if (StaleReferences(projectDirectory, resolved) is { } stale)
        {
            return DiagnosticReport.Inconclusive(stale, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
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
    /// Whether the project in this directory sets <c>UseWPF</c>. Read the way
    /// <see cref="ImplicitUsingsEnabled"/> reads its flag: from the project file directly, no
    /// MSBuild, and a value inherited from <c>Directory.Build.props</c> is not seen - which for
    /// this flag errs toward today's behaviour, exactly like the usings.
    /// </summary>
    private static bool UseWpfEnabled(string projectDirectory)
    {
        try
        {
            foreach (string project in Directory.EnumerateFiles(projectDirectory, "*.csproj"))
            {
                string? value = XDocument.Load(project)
                    .Descendants()
                    .FirstOrDefault(e => e.Name.LocalName.Equals("UseWPF", StringComparison.OrdinalIgnoreCase))
                    ?.Value
                    .Trim();

                if (value is not null)
                {
                    return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                           value.Equals("enable", StringComparison.OrdinalIgnoreCase);
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
    /// Adds the newest XAML-generated partial for every page under the project, or says why the
    /// compile cannot be trusted without them.
    /// <para>
    /// The markup compiler declares <c>InitializeComponent</c> and every <c>x:Name</c> field at
    /// build time - generated code this compilation cannot produce itself and, until a build has
    /// run, cannot find. From run 5c071f37: a correct WPF code-behind was refused ten times over
    /// CS0103 for exactly those names, while the build tool kept answering green in between, and
    /// the run spent itself to the token limit and shipped a window with no handler. So a page
    /// whose generated partial is missing or older than its markup makes the whole compile
    /// inconclusive - the same answer an unbuilt project reference gets, for the same reason:
    /// a gate that cannot know must not gate.
    /// </para>
    /// </summary>
    private string? AddXamlGeneratedPartials(
        string projectDirectory,
        List<SyntaxTree> trees,
        CancellationToken cancellationToken)
    {
        string objDirectory = Path.Combine(projectDirectory, "obj");
        HashSet<string> added = new(StringComparer.OrdinalIgnoreCase);

        foreach (string xaml in EnumerateXamlPages(projectDirectory))
        {
            string? generated = NewestGeneratedPartial(objDirectory, Path.GetFileNameWithoutExtension(xaml));
            if (generated is null)
            {
                return $"'{_guard.ToRelativePath(xaml)}' has no XAML-generated partial class yet - the markup " +
                    "compiler declares InitializeComponent and the x:Name fields during build - so an " +
                    "in-memory compile cannot see those names. Use the build tool for an authoritative answer.";
            }

            if (File.GetLastWriteTimeUtc(generated) < File.GetLastWriteTimeUtc(xaml))
            {
                return $"'{_guard.ToRelativePath(xaml)}' changed after its generated partial was last built, " +
                    "so this compile would judge the edit against the old markup. Use the build tool for " +
                    "an authoritative answer.";
            }

            if (added.Add(generated) && ParseCached(generated, cancellationToken) is { } tree)
            {
                trees.Add(tree);
            }
        }

        return null;
    }

    /// <summary>The project's XAML pages - the .xaml files that name a code-behind class.</summary>
    private IEnumerable<string> EnumerateXamlPages(string projectDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            // The guard skips obj and bin, where the markup compiler leaves copies of the pages.
            if (_guard.Resolve(file, PathAccess.Read).Allowed && DeclaresXamlClass(file))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Whether a .xaml file names a code-behind class. A resource dictionary does not, and no
    /// partial is ever generated for it. Matched on the attribute's local name alone: a Class
    /// attribute in a slightly wrong namespace still means the author expects a partial, and the
    /// lenient reading errs toward standing aside rather than refusing.
    /// </summary>
    private static bool DeclaresXamlClass(string xamlPath)
    {
        try
        {
            return XDocument.Load(xamlPath).Root?
                .Attributes()
                .Any(a => a.Name.LocalName.Equals("Class", StringComparison.Ordinal)) == true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // Malformed markup is the real build's error to report, not this rung's.
            return false;
        }
    }

    /// <summary>
    /// The newest generated partial for one page, wherever a configuration and target framework
    /// put it. Newest, because obj can hold a .g.cs from the last real build and a .g.i.cs from
    /// a design-time one, and they declare the same class.
    /// </summary>
    private static string? NewestGeneratedPartial(string objDirectory, string pageName)
    {
        if (!Directory.Exists(objDirectory))
        {
            return null;
        }

        string? newest = null;
        DateTime newestWrite = DateTime.MinValue;
        string[] patterns = [$"{pageName}.g.cs", $"{pageName}.g.i.cs"];

        try
        {
            foreach (string pattern in patterns)
            {
                foreach (string file in Directory.EnumerateFiles(objDirectory, pattern, SearchOption.AllDirectories))
                {
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (written > newestWrite)
                    {
                        newest = file;
                        newestWrite = written;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return newest;
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
    /// build output, each with the newest write time seen for that name. The names are what
    /// tells the caller whether the set is complete: framework assemblies are always there, so
    /// only the scavenged ones say anything about this project. The write times are what say
    /// whether a reference predates the sources it was built from.
    /// </summary>
    private (List<MetadataReference> Metadata, Dictionary<string, DateTime> Resolved) References(string projectDirectory)
    {
        List<MetadataReference> references = [.. FrameworkReferences.Value];
        Dictionary<string, DateTime> resolved = new(StringComparer.OrdinalIgnoreCase);

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

                    string name = Path.GetFileNameWithoutExtension(dll);
                    DateTime written = File.GetLastWriteTimeUtc(dll);
                    if (!resolved.TryGetValue(name, out DateTime seen) || written > seen)
                    {
                        resolved[name] = written;
                    }
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
    private static string? UnresolvedReferences(string projectDirectory, Dictionary<string, DateTime> resolved)
    {
        foreach (string projectFile in ProjectLocator.EnumerateProjects(projectDirectory))
        {
            ProjectReferences declared = ProjectLocator.ReadReferences(projectFile);
            if (!declared.Any)
            {
                continue;
            }

            List<string> missing = [.. declared.Projects.Where(p => !resolved.ContainsKey(p))];
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

    /// <summary>
    /// The scavenged reference that is older than the sources of the project it was built from,
    /// or null when they are all current. Diagnostics computed against a stale reference are
    /// about a version of the dependency that no longer exists, so the compile is inconclusive -
    /// the same answer an unbuilt reference gets, for the same reason.
    /// </summary>
    private string? StaleReferences(string projectDirectory, Dictionary<string, DateTime> resolved)
    {
        foreach (string projectFile in ProjectLocator.EnumerateProjects(projectDirectory))
        {
            foreach ((string name, string referencedProject) in ProjectLocator.ReadProjectReferencePaths(projectFile))
            {
                if (!resolved.TryGetValue(name, out DateTime built) ||
                    Path.GetDirectoryName(referencedProject) is not { } directory)
                {
                    continue;
                }

                foreach (string source in EnumerateSources(directory))
                {
                    if (File.GetLastWriteTimeUtc(source) > built)
                    {
                        return $"'{Path.GetFileName(projectFile)}' references {name}, whose sources changed " +
                            $"after it was last built, so this compile would judge the edit against the old " +
                            $"{name}. Use the build tool for an authoritative answer.";
                    }
                }
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
