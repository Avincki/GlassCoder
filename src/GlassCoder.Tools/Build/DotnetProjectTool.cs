using System.ComponentModel;
using System.Text.Json.Serialization;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Build;

/// <summary>What <c>dotnet_project</c> is being asked to do.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DotnetProjectOperation>))]
public enum DotnetProjectOperation
{
    /// <summary>Scaffold a new project from a template into an empty directory.</summary>
    New,

    /// <summary>Scaffold a new solution file.</summary>
    NewSolution,

    /// <summary>Add a project to a solution.</summary>
    AddToSolution,

    /// <summary>Add a project-to-project reference.</summary>
    AddReference,

    /// <summary>Add a NuGet package reference.</summary>
    AddPackage,

    /// <summary>Restore NuGet packages.</summary>
    Restore,

    /// <summary>Run <c>dotnet format</c> over a project or solution.</summary>
    Format,
}

/// <summary>Result payload of <c>dotnet_project</c>.</summary>
/// <param name="Operation">What was done.</param>
/// <param name="Path">What it was done to.</param>
/// <param name="Succeeded">Whether the command exited cleanly.</param>
/// <param name="ExitCode">Exit code from <c>dotnet</c>.</param>
/// <param name="Output">Trimmed command output - the useful lines, not the whole log.</param>
/// <param name="DurationMs">Wall-clock for the command.</param>
public sealed record DotnetProjectResult(
    [property: Description("The operation that ran.")] string Operation,
    [property: Description("The project, solution or directory it applied to.")] string Path,
    [property: Description("True when the command exited cleanly.")] bool Succeeded,
    [property: Description("Exit code from dotnet.")] int ExitCode,
    [property: Description("Command output, trimmed.")] string Output,
    [property: Description("Wall-clock milliseconds.")] double DurationMs);

/// <summary>
/// <c>dotnet_project</c> - create and wire up projects (workplan task 44).
/// <para>
/// Added because scaffolding was the one common task the harness could only do by hand. Asked to
/// add unit tests, the agent had to write a <c>.csproj</c> as raw XML through <c>edit_file</c>,
/// guess package versions, and hand-author a <c>ProjectReference</c> - and a single mistyped
/// element is a build failure several steps later, a long way from its cause. The SDK already
/// knows how to do all of this correctly; this is the seam that lets the agent ask it.
/// </para>
/// <para>
/// Every operation is bounded by the path guard's writable set, exactly as the file tools are,
/// and each one that changes a file records it in the change log so the edit is as visible on
/// the Changes surface as one the agent typed (CLAUDE.md §10).
/// </para>
/// </summary>
public sealed class DotnetProjectTool : IToolSet
{
    private const string ToolName = "dotnet_project";

    /// <summary>How many sources a formatting pass will watch for rewrites.</summary>
    private const int MaxFormattedFiles = 500;

    /// <summary>Templates worth offering. Anything else is a build system this cannot vouch for.</summary>
    private static readonly string[] KnownTemplates =
        ["xunit", "nunit", "mstest", "classlib", "console", "web", "webapi", "worker", "blazor"];

    /// <summary>Solution formats the SDK writes, newest first - the order they are looked for.</summary>
    private static readonly string[] SolutionExtensions = [".slnx", ".sln"];

    private readonly ICommandExecutor _executor;
    private readonly IPathGuard _guard;
    private readonly IChangeLog _changes;
    private readonly SandboxOptions _sandbox;
    private readonly BuildCache? _cache;

    /// <summary>Creates the tool.</summary>
    public DotnetProjectTool(
        ICommandExecutor executor,
        IPathGuard guard,
        IChangeLog changes,
        IOptions<SandboxOptions> sandbox,
        BuildCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        _executor = executor;
        _guard = guard;
        _changes = changes;
        _sandbox = sandbox.Value;
        _cache = cache;
    }

    /// <summary>Creates or wires up a project.</summary>
    [GlassCoderTool(ToolName, Order = 55)]
    [Description("Create and wire up .NET projects with the dotnet CLI rather than hand-editing a .csproj. "
        + "A test project is normally new, then add_reference, then build.")]
    public async Task<ToolObservation<DotnetProjectResult>> RunAsync(
        [Description("What to do.")]
        DotnetProjectOperation operation,
        [Description("Repo-relative target: for new, the directory to create the project in, which names it; "
            + "otherwise the project or solution to change.")]
        string path,
        [Description("Template for new (xunit, classlib, console...); the project for add_reference and "
            + "add_to_solution; the package id for add_package; optional solution name for new_solution. "
            + "Unused otherwise.")]
        string? argument = null,
        [Description("Package version for add_package. Omit for the latest.")]
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        // Everything here writes, so the writable set is the gate - the same one the file tools
        // answer to. 'restore' is the exception in spirit but not in practice: it writes obj/.
        PathGuardResult verdict = _guard.Resolve(path, PathAccess.Write);
        if (!verdict.Allowed || verdict.FullPath is null)
        {
            return Observation.Fail<DotnetProjectResult>(ToolName, ToolErrorCodes.PathNotAllowed, verdict.Reason!);
        }

        if (RequiresArgument(operation) && string.IsNullOrWhiteSpace(argument))
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{Describe(operation)}' needs the 'argument' parameter.",
                Hint(operation));
        }

        (List<string> arguments, string workingDirectory, string? touchedFile) =
            Compose(operation, verdict, argument, version);

        if (operation == DotnetProjectOperation.New &&
            !KnownTemplates.Contains(argument!, StringComparer.OrdinalIgnoreCase))
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{argument}' is not a template this tool offers.",
                $"Use one of: {string.Join(", ", KnownTemplates)}.");
        }

        // Read before, so the change log can show what the SDK did to the file as a diff.
        string? before = touchedFile is not null && File.Exists(touchedFile)
            ? await ReadOrNullAsync(touchedFile).ConfigureAwait(false)
            : null;

        // Formatting rewrites whatever it likes. Snapshotting first is the only way the change
        // log can show it: a pass that silently reformats forty files is exactly the invisible
        // change the log exists to prevent.
        Dictionary<string, string> sources = operation == DotnetProjectOperation.Format
            ? Snapshot(verdict.FullPath, cancellationToken)
            : [];

        CommandResult result = await _executor.ExecuteAsync(
            new CommandRequest("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                RequiresNetwork = NeedsNetwork(operation),
                Timeout = TimeSpan.FromSeconds(_sandbox.CommandTimeoutSeconds),
            },
            cancellationToken).ConfigureAwait(false);

        if (result.FailureReason is not null)
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName, ToolErrorCodes.SandboxUnavailable, result.FailureReason);
        }

        if (result.TimedOut)
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName,
                ToolErrorCodes.Timeout,
                $"dotnet {arguments[0]} exceeded {_sandbox.CommandTimeoutSeconds} seconds and was stopped.");
        }

        DotnetProjectResult payload = new(
            Describe(operation),
            verdict.RelativePath!,
            result.ExitCode == 0,
            result.ExitCode,
            Trim(result.CombinedOutput),
            result.Duration.TotalMilliseconds);

        if (!payload.Succeeded)
        {
            // A refused operation is information, not a tool fault - same contract as a failed
            // build (CLAUDE.md §7).
            return Observation.Ok(
                ToolName, payload, $"dotnet {Describe(operation)} failed with exit {result.ExitCode}.");
        }

        // The project file has moved underneath any build already taken, and the SDK wrote it
        // without going through the change log, so both have to be told.
        _cache?.Invalidate();
        RecordChange(touchedFile, before);

        int reformatted = RecordRewrites(sources);
        string summary = operation switch
        {
            DotnetProjectOperation.Format =>
                $"Formatted '{verdict.RelativePath}': {reformatted} file(s) rewritten.",
            DotnetProjectOperation.New => DescribeCreatedProject(verdict, argument!),
            DotnetProjectOperation.NewSolution => DescribeCreatedSolution(verdict, argument),

            // Named by the file the add really landed in, which ResolveSolution may have chosen:
            // echoing the caller's spelling here would plant the wrong path for the next call.
            DotnetProjectOperation.AddToSolution when touchedFile is not null =>
                $"Added '{argument}' to '{_guard.ToRelativePath(touchedFile)}'.",
            _ => Success(operation, verdict.RelativePath!, argument),
        };

        return Observation.Ok(ToolName, payload, summary);
    }

    /// <summary>
    /// Reads every C# source under a target, so a later sweep can tell which ones changed.
    /// Capped: past a few hundred files this is a whole-project read on the way to a formatting
    /// pass, and the cap is reported rather than silently applied.
    /// </summary>
    private Dictionary<string, string> Snapshot(string fullPath, CancellationToken cancellationToken)
    {
        string directory = Directory.Exists(fullPath)
            ? fullPath
            : Path.GetDirectoryName(fullPath) ?? fullPath;

        Dictionary<string, string> sources = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in WorkspaceFiles.Enumerate(
            _guard, directory, "**/*.cs", MaxFormattedFiles, cancellationToken))
        {
            if (ReadOrNull(file) is { } text)
            {
                sources[file] = text;
            }
        }

        return sources;
    }

    /// <summary>Puts every file the command rewrote into the change log, and says how many.</summary>
    private int RecordRewrites(Dictionary<string, string> sources)
    {
        int changed = 0;
        foreach ((string file, string before) in sources)
        {
            if (ReadOrNull(file) is not { } after || string.Equals(before, after, StringComparison.Ordinal))
            {
                continue;
            }

            CodeChange change = _changes.Propose(_guard.ToRelativePath(file), ToolName, before, after);
            _changes.Update(change.Id, ChangeStatus.Applied);
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Builds the command line, and says which file the operation is expected to rewrite so the
    /// change log can pick up the diff.
    /// </summary>
    private (List<string> Arguments, string WorkingDirectory, string? TouchedFile) Compose(
        DotnetProjectOperation operation,
        PathGuardResult verdict,
        string? argument,
        string? version)
    {
        string full = verdict.FullPath!;
        string root = _guard.RepoRoot;

        switch (operation)
        {
            case DotnetProjectOperation.New:
            {
                // The directory names the project, which is the convention every .NET repository
                // already follows and saves the agent inventing a second name.
                string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(full));
                return (["new", argument!, "-o", full, "-n", name], root, null);
            }

            case DotnetProjectOperation.NewSolution:
            {
                // A caller naming a solution means a file, and says so by ending the path in
                // .sln. Treating that as a directory produced src/X.sln/X.sln.slnx - a folder
                // named like a solution, holding a solution named like a folder. It built, by
                // accident, which is the kind of nearly-right that survives a run and confuses
                // the next person.
                string extension = Path.GetExtension(full);
                bool namesAFile = extension is ".sln" or ".slnx";

                string directory = namesAFile
                    ? Path.GetDirectoryName(full) ?? root
                    : full;
                string name = SolutionName(
                    argument ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(full)));

                return (["new", "sln", "-o", directory, "-n", name], root, null);
            }

            case DotnetProjectOperation.AddToSolution:
            {
                string solution = ResolveSolution(full);
                return (["sln", solution, "add", Resolve(argument!)], root, solution);
            }

            case DotnetProjectOperation.AddReference:
                return (["add", full, "reference", Resolve(argument!)], root, full);

            case DotnetProjectOperation.AddPackage:
            {
                List<string> arguments = ["add", full, "package", argument!];
                if (!string.IsNullOrWhiteSpace(version))
                {
                    arguments.Add("--version");
                    arguments.Add(version);
                }

                return (arguments, root, full);
            }

            case DotnetProjectOperation.Restore:
                return (["restore", full], root, null);

            // The only operation that rewrites files it was not pointed at, which is why the
            // change log is fed by a before/after sweep rather than by a single named file.
            case DotnetProjectOperation.Format:
                return (["format", full], root, null);

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    /// <summary>Turns a repo-relative second operand into a full path, leaving it alone if it already is one.</summary>
    private string Resolve(string relative) =>
        Path.GetFullPath(Path.Combine(_guard.RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>
    /// The solution file that actually exists at a caller's path, whichever format the SDK chose.
    /// <para>
    /// <c>dotnet new sln</c> writes <c>.slnx</c> on .NET 10, and a caller who scaffolded a
    /// solution one step ago reasonably asks for it as <c>.sln</c> - which cost run d18c0e57 a
    /// failed add and a dead-end glob. The name is the intent; the extension is trivia this can
    /// forgive.
    /// </para>
    /// </summary>
    private static string ResolveSolution(string full)
    {
        if (File.Exists(full))
        {
            return full;
        }

        string extension = Path.GetExtension(full);
        if (extension is not (".sln" or ".slnx"))
        {
            return full;
        }

        string sibling = Path.ChangeExtension(full, extension == ".sln" ? ".slnx" : ".sln");
        return File.Exists(sibling) ? sibling : full;
    }

    /// <summary>
    /// The bare name <c>-n</c> wants: any .sln/.slnx suffix goes, because the SDK appends its
    /// format extension to the name verbatim - 'ArrayProcessor.sln' came back as
    /// 'ArrayProcessor.sln.slnx' in run 56f01cc5. The name is the caller's; the extension is
    /// the SDK's.
    /// </summary>
    private static string SolutionName(string name) =>
        Path.GetExtension(name) is ".sln" or ".slnx"
            ? Path.GetFileNameWithoutExtension(name)
            : name;

    /// <summary>Stub files test templates scaffold, in the order they are looked for.</summary>
    private static readonly string[] TestStubNames = ["UnitTest1.cs", "Test1.cs"];

    /// <summary>Templates whose stub is a placeholder test rather than a placeholder class.</summary>
    private static readonly string[] TestTemplates = ["xunit", "nunit", "mstest"];

    /// <summary>
    /// Describes a scaffolded project, deleting the template's stub file on the way - because
    /// left alone, the stubs outlive every run. <c>Class1.cs</c> once collided with the very
    /// class a run was writing (CS0101, run d21eb210). <c>UnitTest1.cs</c> got a gentler
    /// treatment first - named in the summary with "replace or delete it" - and two runs in a
    /// row read the warning, wrote their tests in a fresh file, and left the stub padding the
    /// pass count ("All 5 tests passed" was four real tests plus an assertion of nothing). A
    /// suggestion the model reliably ignores is not a mechanism, the stub is one tool call
    /// old, and the change log accounts for the delete - so now both kinds go.
    /// </summary>
    private string DescribeCreatedProject(PathGuardResult verdict, string template)
    {
        string directory = verdict.FullPath!;
        string created = $"Created a {template} project in '{verdict.RelativePath}'.";
        const string next = "Add a reference to the code under test, then build.";

        string? stub = template.Equals("classlib", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, "Class1.cs")
            : TestTemplates.Contains(template, StringComparer.OrdinalIgnoreCase)
                ? TestStubNames.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists)
                : null;

        return stub is not null && TryDeleteStub(stub)
            ? $"{created} The template's empty {Path.GetFileName(stub)} was removed - create your files fresh. {next}"
            : $"{created} {next}";
    }

    /// <summary>
    /// Removes a stub the SDK just wrote, recording the removal so the Changes surface accounts
    /// for a file that existed for one tool call. False when there was nothing to delete or the
    /// delete could not be done - either way the stub is then merely mentioned, not hidden.
    /// </summary>
    private bool TryDeleteStub(string fullPath)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            string before = File.ReadAllText(fullPath);
            File.Delete(fullPath);

            CodeChange change = _changes.Propose(_guard.ToRelativePath(fullPath), ToolName, before, string.Empty);
            _changes.Update(change.Id, ChangeStatus.Applied);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Names the file <c>dotnet new sln</c> actually wrote, because the SDK picks the format:
    /// .NET 10 writes <c>.slnx</c> where the caller may well have said <c>.sln</c>. The exact
    /// path goes into the summary so the next add_to_solution and glob are aimed at a file that
    /// exists.
    /// </summary>
    private string DescribeCreatedSolution(PathGuardResult verdict, string? argument)
    {
        string full = verdict.FullPath!;
        bool namesAFile = Path.GetExtension(full) is ".sln" or ".slnx";
        string directory = namesAFile ? Path.GetDirectoryName(full) ?? _guard.RepoRoot : full;
        string name = SolutionName(
            argument ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(full)));

        string? created = SolutionExtensions
            .Select(extension => Path.Combine(directory, name + extension))
            .FirstOrDefault(File.Exists);

        return created is null
            ? $"Created a solution named '{name}'."
            : $"Created a solution at '{_guard.ToRelativePath(created)}'. Use this exact path with add_to_solution.";
    }

    /// <summary>
    /// Puts the SDK's edit into the change log, so a project file the agent never typed into is
    /// still visible as a diff on the Changes surface.
    /// </summary>
    private void RecordChange(string? touchedFile, string? before)
    {
        if (touchedFile is null || before is null || !File.Exists(touchedFile))
        {
            return;
        }

        string after;
        try
        {
            after = File.ReadAllText(touchedFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        CodeChange change = _changes.Propose(_guard.ToRelativePath(touchedFile), ToolName, before, after);
        _changes.Update(change.Id, ChangeStatus.Applied);
    }

    private static async Task<string?> ReadOrNullAsync(string path)
    {
        try
        {
            return await File.ReadAllTextAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool RequiresArgument(DotnetProjectOperation operation) => operation is
        DotnetProjectOperation.New or
        DotnetProjectOperation.AddToSolution or
        DotnetProjectOperation.AddReference or
        DotnetProjectOperation.AddPackage;

    // format restores before it formats, unless told not to.
    private static bool NeedsNetwork(DotnetProjectOperation operation) => operation is
        DotnetProjectOperation.AddPackage or
        DotnetProjectOperation.Restore or
        DotnetProjectOperation.Format or
        DotnetProjectOperation.New;

    private static string Describe(DotnetProjectOperation operation) => operation switch
    {
        DotnetProjectOperation.New => "new",
        DotnetProjectOperation.NewSolution => "new_solution",
        DotnetProjectOperation.AddToSolution => "add_to_solution",
        DotnetProjectOperation.AddReference => "add_reference",
        DotnetProjectOperation.AddPackage => "add_package",
        DotnetProjectOperation.Format => "format",
        _ => "restore",
    };

    private static string Hint(DotnetProjectOperation operation) => operation switch
    {
        DotnetProjectOperation.New => "Pass the template, for example 'xunit'.",
        DotnetProjectOperation.AddToSolution => "Pass the project to add.",
        DotnetProjectOperation.AddReference => "Pass the project to reference.",
        DotnetProjectOperation.AddPackage => "Pass the package id, for example 'xunit'.",
        _ => "This operation takes no argument.",
    };

    private static string Success(DotnetProjectOperation operation, string path, string? argument) => operation switch
    {
        DotnetProjectOperation.AddToSolution => $"Added '{argument}' to '{path}'.",
        DotnetProjectOperation.AddReference => $"'{path}' now references '{argument}'.",
        DotnetProjectOperation.AddPackage => $"'{path}' now references the {argument} package.",
        _ => $"Restored '{path}'.",
    };

    /// <summary>Keeps the useful lines. The SDK is chatty and the model pays for every one.</summary>
    private static string Trim(string output)
    {
        string[] lines = [.. output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.Length > 0)];

        return lines.Length <= 12
            ? string.Join(Environment.NewLine, lines)
            : string.Join(Environment.NewLine, lines[^12..]);
    }
}
