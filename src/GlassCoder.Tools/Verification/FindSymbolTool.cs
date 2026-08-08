using System.ComponentModel;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Verification;

/// <summary>Result payload of <c>find_symbol</c>.</summary>
/// <param name="Symbols">Where the name is declared.</param>
/// <param name="Total">How many declarations were found in all.</param>
/// <param name="FilesSearched">How many C# files were read.</param>
/// <param name="Truncated">Whether the cap cut the list short.</param>
public sealed record FindSymbolResult(
    [property: Description("Declarations of the name, exact matches first.")] IReadOnlyList<SourceSymbol> Symbols,
    [property: Description("How many declarations were found in all.")] int Total,
    [property: Description("How many C# files were searched.")] int FilesSearched,
    [property: Description("True when more were found than are listed.")] bool Truncated);

/// <summary>
/// <c>find_symbol</c> - where a type or member is declared (workplan task 47).
/// <para>
/// The gap this fills is not "search": <c>grep</c> already searches. It is that grep cannot tell
/// a declaration from a call, so <c>grep Widget</c> returns every mention and the agent reads
/// files to work out which one defines it. This answers the declaration question directly, and
/// returns the line range so the follow-up is one ranged <c>read_file</c>.
/// </para>
/// <para>
/// Syntax tree only, which is what makes it trustworthy. A declaration is in the file whether or
/// not the project was ever built - the failure mode that keeps <c>find_references</c> unbuilt
/// (task 48) does not exist here.
/// </para>
/// </summary>
public sealed class FindSymbolTool : IToolSet
{
    private const string ToolName = "find_symbol";

    /// <summary>How many declarations to return. The count is always the true one.</summary>
    private const int MaxSymbols = 50;

    private readonly IPathGuard _guard;
    private readonly RoslynCodeAnalyzer _analyzer;
    private readonly ToolsOptions _options;

    /// <summary>Creates the tool.</summary>
    public FindSymbolTool(IPathGuard guard, RoslynCodeAnalyzer analyzer, IOptions<ToolsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _guard = guard;
        _analyzer = analyzer;
        _options = options.Value;
    }

    /// <summary>
    /// Whether some source in this workspace declares <paramref name="name"/> exactly.
    /// <para>
    /// Not a tool - the retrieval signals ask it (workplan task 59) to tell a missing package
    /// type from the model's own typo, which is the whole difference between a question
    /// documentation answers and one it makes worse. The same sweep the tool runs, through the
    /// same cached trees, and a false on any trouble: a signal that guesses "yes" would suppress
    /// a legitimate lookup, and one that guesses "no" would invite a pointless one, so the
    /// cheaper mistake is the one that costs a refusal.
    /// </para>
    /// </summary>
    public bool Declares(string name, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            foreach (string file in WorkspaceFiles.Enumerate(
                _guard, _guard.RepoRoot, "**/*.cs", _options.MaxFilesSearched, cancellationToken))
            {
                if (_analyzer.ParseFile(file, cancellationToken) is not { } tree)
                {
                    continue;
                }

                foreach (SourceSymbol symbol in CodeStructure.Outline(_guard.ToRelativePath(file), tree))
                {
                    if (string.Equals(symbol.Name, name, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }

        return false;
    }

    /// <summary>Finds declarations of a name across the workspace.</summary>
    [GlassCoderTool(ToolName, Order = 25)]
    [Description("Find where a C# type or member is declared, with its line range. Use this rather than "
        + "grepping for a name: grep cannot tell a declaration from a use.")]
    public ToolObservation<FindSymbolResult> FindSymbol(
        [Description("Type or member name, for example 'AgentLoop' or 'RunAsync'. Part of a name also matches.")]
        string name,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Observation.Fail<FindSymbolResult>(ToolName, ToolErrorCodes.InvalidArgument, "name is required.");
        }

        List<SourceSymbol> exact = [];
        List<SourceSymbol> partial = [];
        int filesSearched = 0;

        try
        {
            foreach (string file in WorkspaceFiles.Enumerate(
                _guard, _guard.RepoRoot, "**/*.cs", _options.MaxFilesSearched, cancellationToken))
            {
                // Through the analyzer's cache: a symbol sweep and a pre-write compile want the
                // same trees, and this is usually the second visit to most of them.
                if (_analyzer.ParseFile(file, cancellationToken) is not { } tree)
                {
                    continue;
                }

                filesSearched++;
                string relative = _guard.ToRelativePath(file);

                foreach (SourceSymbol symbol in CodeStructure.Outline(relative, tree))
                {
                    if (string.Equals(symbol.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        exact.Add(symbol);
                    }
                    else if (symbol.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    {
                        partial.Add(symbol);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return Observation.Fail<FindSymbolResult>(ToolName, ToolErrorCodes.Timeout, "The search was cancelled.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Observation.Fail<FindSymbolResult>(ToolName, ToolErrorCodes.Unreadable, ex.Message);
        }

        // Exact first, because a search for 'Run' should lead with Run and not with RunAsync,
        // RunContext and RunMetricsCollector.
        List<SourceSymbol> found = [.. exact, .. partial];
        if (found.Count == 0)
        {
            return Observation.Fail<FindSymbolResult>(
                ToolName,
                ToolErrorCodes.NotFound,
                $"Nothing named '{name}' is declared in {filesSearched} C# file(s).",
                "It may come from a package rather than this workspace. Use grep to find its uses.");
        }

        bool truncated = found.Count > MaxSymbols;
        FindSymbolResult result = new(
            truncated ? [.. found.Take(MaxSymbols)] : found,
            found.Count,
            filesSearched,
            truncated);

        string summary = truncated
            ? $"{found.Count} declarations match '{name}'; showing {MaxSymbols}."
            : $"{found.Count} declaration(s) of '{name}'.";

        return Observation.Ok(ToolName, result, summary);
    }
}
