using System.Text;
using System.Text.RegularExpressions;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// Where a missing symbol actually lives, said in the refusal that reported it missing.
/// <para>
/// The pre-write check refuses a file over CS0246 and reports the compiler's error - which
/// names the symbol and withholds the diagnosis. Run 05e1bedb spent three ~12-second steps
/// guessing at one: a using directive for a type that had no namespace, then a qualified name
/// for the same type, each refused with a technically-accurate message that knew better. The
/// workspace holds the answer - which file declares the type, which project owns that file,
/// whether the referencing project can see it - and the refusal is the moment it is worth one
/// directory walk to say so.
/// </para>
/// </summary>
public static class SymbolHints
{
    /// <summary>The diagnostics that mean "a name was not found", whose quoted name is worth looking up.</summary>
    private static readonly string[] MissingSymbolIds = ["CS0103", "CS0138", "CS0234", "CS0246", "CS0426"];

    /// <summary>Directory segments that hold generated or foreign code, never declarations to point at.</summary>
    private static readonly string[] SkippedSegments = ["bin", "obj", ".git", ".vs", "node_modules", ".glasscoder"];

    /// <summary>One hint per name is plenty; a refusal quoting five names has a bigger problem than namespaces.</summary>
    private const int MaxIdentifiers = 3;

    /// <summary>Ceiling on the walk. A workspace this size gets its hints from a person.</summary>
    private const int MaxSourceFiles = 2000;

    /// <summary>Files larger than this are generated, embedded, or otherwise not where types live.</summary>
    private const int MaxFileBytes = 262_144;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Lines locating each missing symbol the diagnostics quote, prefixed with a newline so the
    /// caller can append the result verbatim. Empty when the diagnostics name no missing symbol,
    /// when nothing in the workspace declares one, or when looking would cost more than it says -
    /// a hint is a bonus, never a failure.
    /// </summary>
    public static string Describe(IEnumerable<CodeDiagnostic> diagnostics, string editedFullPath, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(editedFullPath);
        ArgumentNullException.ThrowIfNull(repoRoot);

        try
        {
            List<string> identifiers = [.. diagnostics
                .Where(d => d.IsError && MissingSymbolIds.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                .Select(QuotedName)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Take(MaxIdentifiers)];

            if (identifiers.Count == 0)
            {
                return string.Empty;
            }

            List<string> sources = [.. SourceFiles(repoRoot)];
            StringBuilder hints = new();
            foreach (string identifier in identifiers)
            {
                if (Locate(identifier, sources) is not { } declaration)
                {
                    continue;
                }

                hints.Append('\n').Append(DescribeDeclaration(identifier, declaration, editedFullPath, repoRoot));
            }

            return hints.ToString();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// The sentence that ends the guessing: the declaring file, its project or the lack of one,
    /// the namespace or the lack of one, and the reference that is missing when one is.
    /// </summary>
    private static string DescribeDeclaration(
        string identifier,
        (string File, string? Namespace) declaration,
        string editedFullPath,
        string repoRoot)
    {
        string declared = $"'{identifier}' is declared in {Relative(repoRoot, declaration.File)}";
        string @namespace = declaration.Namespace is null
            ? "the global namespace, so no using directive applies - use the name directly"
            : $"namespace '{declaration.Namespace}'";

        string? declarationProject = ProjectLocator.FindProjectFile(declaration.File);
        if (declarationProject is null)
        {
            return $"{declared}, which no project contains - nothing can reference it until a project owns " +
                   "that file. Scaffold one with dotnet_project (new) and move the file into its directory.";
        }

        string? editedProject = ProjectLocator.FindProjectFile(editedFullPath);
        bool sameProject = editedProject is not null &&
            string.Equals(Path.GetFullPath(editedProject), Path.GetFullPath(declarationProject), StringComparison.OrdinalIgnoreCase);

        if (sameProject)
        {
            return $"{declared}, in this same project, in {@namespace}.";
        }

        string project = Relative(repoRoot, declarationProject);
        bool referenced = editedProject is not null && ProjectLocator
            .ReadProjectReferencePaths(editedProject)
            .Any(r => string.Equals(r.FullPath, Path.GetFullPath(declarationProject), StringComparison.OrdinalIgnoreCase));

        if (editedProject is not null && !referenced)
        {
            return $"{declared} (project {project}), in {@namespace} - but " +
                   $"{Relative(repoRoot, editedProject)} does not reference that project. Add the reference " +
                   "with dotnet_project (add_reference) first.";
        }

        return $"{declared} (project {project}), in {@namespace}.";
    }

    /// <summary>
    /// The first quoted <em>name</em> in a compiler message. Not the first quoted token: CS0138
    /// quotes the phrase 'using namespace' before it quotes the type it is actually about, so
    /// anything that does not parse as a name is message furniture and is skipped.
    /// </summary>
    private static string? QuotedName(CodeDiagnostic diagnostic)
    {
        foreach (Match match in Regex.Matches(diagnostic.Message, "'([^']+)'", RegexOptions.None, RegexTimeout))
        {
            // "N.T" arrives when the model guessed a qualifier; the simple name is what
            // declarations use.
            string name = match.Groups[1].Value;
            int lastDot = name.LastIndexOf('.');
            string simple = lastDot >= 0 ? name[(lastDot + 1)..] : name;

            if (Regex.IsMatch(simple, @"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.None, RegexTimeout))
            {
                return simple;
            }
        }

        return null;
    }

    /// <summary>The file declaring a type of this name, and the namespace it sits in, if any does.</summary>
    private static (string File, string? Namespace)? Locate(string identifier, IReadOnlyList<string> sources)
    {
        Regex declaration = new(
            $@"\b(?:class|struct|interface|enum|delegate|record(?:\s+(?:class|struct))?)\s+{Regex.Escape(identifier)}\b",
            RegexOptions.None,
            RegexTimeout);

        foreach (string file in sources)
        {
            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                if (!declaration.IsMatch(content))
                {
                    continue;
                }

                Match @namespace = Regex.Match(
                    content, @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Multiline, RegexTimeout);
                return (file, @namespace.Success ? @namespace.Groups[1].Value : null);
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }
        }

        return null;
    }

    /// <summary>Source files worth searching for a declaration, smallest set of exclusions that stays honest.</summary>
    private static IEnumerable<string> SourceFiles(string repoRoot)
    {
        if (!Directory.Exists(repoRoot))
        {
            yield break;
        }

        int seen = 0;
        foreach (string file in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (Skipped(file))
            {
                continue;
            }

            if (++seen > MaxSourceFiles)
            {
                yield break;
            }

            long length;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (length <= MaxFileBytes)
            {
                yield return file;
            }
        }
    }

    private static bool Skipped(string path)
    {
        string normalised = path.Replace('\\', '/');
        return SkippedSegments.Any(segment =>
            normalised.Contains($"/{segment}/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');
}
