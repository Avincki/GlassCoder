using System.ComponentModel;
using System.Text.Json.Serialization;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using GlassCoder.Tools.Registry;
using GlassCoder.Tools.Verification;
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

    /// <summary>
    /// Templates worth offering. Anything else is a build system this cannot vouch for.
    /// <para>
    /// The desktop pair earned its place from two runs: asked for a WPF app, the model requested
    /// 'wpf' unprompted both times, was refused both times, and both times spent ~7 steps
    /// converting a console project by hand - during which the leftover Program.cs failed three
    /// ladder climbs before being deleted (runs 5c071f37, e3993510). The SDK's own template does
    /// all of that in one call, with the SDK's own TargetFramework.
    /// </para>
    /// </summary>
    private static readonly string[] KnownTemplates =
        ["xunit", "nunit", "mstest", "classlib", "console", "wpf", "winforms", "web", "webapi", "worker", "blazor"];

    /// <summary>Templates whose scaffold is the app's starting skeleton, never a stub to delete.</summary>
    private static readonly string[] DesktopTemplates = ["wpf", "winforms"];

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
    [Description("Create and wire up .NET projects; never hand-edit a .csproj. A test project is new, "
        + "then add_reference, then build.")]
    public async Task<ToolObservation<DotnetProjectResult>> RunAsync(
        [Description("What to do.")]
        DotnetProjectOperation operation,
        [Description("Repo-relative target in a writable root (not '.'): for new, the directory to "
            + "create the project in, which names it; otherwise the project or solution to change.")]
        string path,
        [Description("Template for new (xunit, classlib, console, wpf, winforms...); the project for "
            + "add_reference and add_to_solution; the package id for add_package; solution name "
            + "for new_solution.")]
        string? argument = null,
        [Description("Package version for add_package; omit for latest.")]
        string? version = null,
        CancellationToken cancellationToken = default)
    {
        // Stray whitespace is never intent: run f4ed50e0 sent add_package ' FlaUI.UIA3' - one
        // leading space - and the SDK refused it twice before the model noticed. The CLI cannot
        // forgive it, so it is forgiven here.
        path = path?.Trim() ?? string.Empty;
        argument = argument?.Trim();
        version = version?.Trim();

        // Meet the model where it is (the edit_file lesson): run 4b562c91 sent add_to_solution
        // with the project in 'path' and the solution in 'argument' five times running, and the
        // CLI's "Solution argument is misplaced" taught it nothing all five times. When the
        // argument names the solution, the intent is unambiguous - put the pieces the right way
        // round instead of relaying the swap.
        if (operation == DotnetProjectOperation.AddToSolution && !string.IsNullOrWhiteSpace(argument))
        {
            (path, argument) = NormalizeSolutionAdd(path, argument);
        }

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

        if (operation == DotnetProjectOperation.New && RefuseHazardousScaffold(verdict) is { } hazard)
        {
            return hazard;
        }

        // Which files are already there, taken before the SDK runs, so everything it writes can
        // be told apart afterwards and recorded as this run's own creations.
        HashSet<string> preexisting = operation == DotnetProjectOperation.New
            ? new(ScaffoldFiles(verdict.FullPath!), StringComparer.OrdinalIgnoreCase)
            : [];

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
            if (operation == DotnetProjectOperation.AddReference &&
                result.CombinedOutput.Contains("incompatible targeted frameworks", StringComparison.OrdinalIgnoreCase))
            {
                return await RepairFrameworkMismatchAsync(
                    verdict, argument!, arguments, workingDirectory, payload, cancellationToken)
                    .ConfigureAwait(false);
            }

            // A refused operation is information, not a tool fault - same contract as a failed
            // build (CLAUDE.md §7). OutcomeOk carries the failure to the progress machinery,
            // which otherwise reads the relay as success (run 4b562c91: five identical soft
            // failures, invisible to every loop-breaker).
            return Observation.Ok(
                ToolName, payload, $"dotnet {Describe(operation)} failed with exit {result.ExitCode}.",
                outcomeOk: false);
        }

        // The project file has moved underneath any build already taken, and the SDK wrote it
        // without going through the change log, so both have to be told.
        _cache?.Invalidate();
        RecordChange(touchedFile, before);
        RecordScaffold(operation, verdict, argument, preexisting);

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
    /// Extensions that mean the caller named a file where <c>new</c> wants a directory. Dotted
    /// directory names are normal in .NET ('src/App.Tests'), so only extensions that
    /// unambiguously belong to files are listed.
    /// </summary>
    private static readonly string[] FileExtensions =
        [".cs", ".vb", ".fs", ".xaml", ".axaml", ".razor", ".cshtml", ".resx",
         ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".json", ".config", ".xml"];

    /// <summary>
    /// Runaway backstop on scaffold recording, far above what any offered template writes.
    /// </summary>
    private const int MaxScaffoldRecords = 200;

    /// <summary>
    /// Refuses a scaffold that would land where no project should go, while refusing is still
    /// one cheap step.
    /// <para>
    /// From run 008007e11a: asked to turn its window into a dialog, the model called new with
    /// path 'src/MultiplyApp/DialogWindow.xaml' - a file name, inside the very project it was
    /// editing - and got a complete second application nested in the first. It saw the mistake
    /// one step later and spent the rest of its token budget deleting the scaffold file by
    /// file. The SDK compiles nested projects' sources into each other; list_projects already
    /// warns about exactly that after the fact, and this is the same knowledge applied before
    /// the six files exist.
    /// </para>
    /// </summary>
    private ToolObservation<DotnetProjectResult>? RefuseHazardousScaffold(PathGuardResult verdict)
    {
        string full = Path.TrimEndingDirectorySeparator(verdict.FullPath!);
        string relative = verdict.RelativePath!;

        if (FileExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{relative}' names a file, and 'new' scaffolds a whole project into a directory.",
                "To add a window or class to an existing project, create the file with create_file - "
                + "every .cs and .xaml under a project's directory is compiled into it automatically. "
                + "Use 'new' only for a fresh project in its own directory, for example 'src/MyApp'.");
        }

        // Bounded to the workspace: a project file above the repository root is somebody
        // else's tree and no reason to refuse anything.
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_guard.RepoRoot));
        if (ProjectLocator.FindProjectFile(full) is { } owner &&
            Path.GetFullPath(owner).StartsWith(
                root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{relative}' is inside the '{_guard.ToRelativePath(owner)}' project, and the SDK "
                + "compiles a nested project's sources into its parent.",
                "Add files to that project with create_file, or scaffold the new project in a "
                + "sibling directory of its own.");
        }

        if (Directory.Exists(full) && ProjectLocator.FindAllProjects(full).FirstOrDefault() is { } nested)
        {
            return Observation.Fail<DotnetProjectResult>(
                ToolName,
                ToolErrorCodes.InvalidArgument,
                $"'{relative}' contains the '{_guard.ToRelativePath(nested)}' project, and a project "
                + "scaffolded above another compiles that project's sources into itself.",
                "Scaffold into an empty directory of its own, beside the projects already there.");
        }

        return null;
    }

    /// <summary>
    /// Every file under <paramref name="directory"/> the workspace can see. The guard hides
    /// bin/ and obj/, which matters because <c>new</c> restores and restore writes obj/.
    /// </summary>
    private IEnumerable<string> ScaffoldFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            if (_guard.Resolve(file, PathAccess.Read).Allowed)
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Puts the files the SDK just scaffolded into the change log as this run's creations.
    /// <para>
    /// <c>dotnet new</c> writes without going through the change log, so until now a scaffolded
    /// file had no baseline: the first later touch became "how this run found it". Run
    /// 008007e11a deleted a mis-scaffolded App.xaml, then reverted it - and the revert
    /// resurrected the scaffold, because the delete was the file's earliest record and the
    /// delete's before-text was the scaffold. Recorded as created (nothing to content), revert
    /// means what it says: the file goes.
    /// </para>
    /// </summary>
    private void RecordScaffold(
        DotnetProjectOperation operation,
        PathGuardResult verdict,
        string? argument,
        HashSet<string> preexisting)
    {
        if (operation == DotnetProjectOperation.NewSolution)
        {
            // The one file dotnet new sln writes. On a conflict the SDK refuses and this is
            // never reached, so a located solution is a created one.
            if (LocateCreatedSolution(verdict, argument) is { } solution &&
                ReadOrNull(solution) is { } text)
            {
                CodeChange change = _changes.Propose(
                    _guard.ToRelativePath(solution), ToolName, string.Empty, text);
                _changes.Update(change.Id, ChangeStatus.Applied);
            }

            return;
        }

        if (operation != DotnetProjectOperation.New)
        {
            return;
        }

        int recorded = 0;
        foreach (string file in ScaffoldFiles(verdict.FullPath!))
        {
            if (preexisting.Contains(file) || ReadOrNull(file) is not { } content)
            {
                continue;
            }

            CodeChange change = _changes.Propose(
                _guard.ToRelativePath(file), ToolName, string.Empty, content);
            _changes.Update(change.Id, ChangeStatus.Applied);

            if (++recorded >= MaxScaffoldRecords)
            {
                break;
            }
        }
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

    /// <summary>
    /// Repairs the one framework mismatch the tool's own templates manufacture, or names the fix.
    /// <para>
    /// The wpf template targets <c>net10.0-windows</c>; the xunit template targets
    /// <c>net10.0</c>. Wiring the pair - the very sequence the templates exist for - fails, and
    /// the CLI's message lists the <em>referencing</em> project's framework as the constraint,
    /// which reads as "change the app". Run a408b61b obeyed: it downgraded the WPF app first
    /// (which would break it), flipped it back, and only then widened the test project - seven
    /// steps and three hand-edits of csproj files under a tool whose description says never to
    /// hand-edit one. Run ca727be3 lost three steps to the same seam.
    /// </para>
    /// <para>
    /// When the shape is unambiguous - both projects single-targeted and the referenced
    /// framework is the referencing one plus an OS suffix - widening the referencing project is
    /// always the fix and always compile-compatible, so it is done here, through the change log,
    /// and the add retried once: the same meet-the-model-where-it-is contract as
    /// <see cref="NormalizeSolutionAdd"/>. Every other shape gets the diagnosis the CLI
    /// withholds: both frameworks, and which side to change.
    /// </para>
    /// </summary>
    private async Task<ToolObservation<DotnetProjectResult>> RepairFrameworkMismatchAsync(
        PathGuardResult verdict,
        string argument,
        List<string> arguments,
        string workingDirectory,
        DotnetProjectResult failed,
        CancellationToken cancellationToken)
    {
        // The CLI accepts a directory for either side, and so does the model: run c5eb67f6
        // sent 'tests/MultiplyApp.Tests' - the directory - and reading a TFM out of a directory
        // yields nothing, so the widen never fired and the gloss said "an unknown framework".
        // Whatever spelling arrived, the project file is what has a framework.
        string referencing = ProjectFileOf(verdict.FullPath!) ?? verdict.FullPath!;
        string referenced = ProjectFileOf(Resolve(argument)) ?? Resolve(argument);
        string referencingRel = _guard.ToRelativePath(referencing);
        string referencedRel = _guard.ToRelativePath(referenced);

        string? from = ProjectLocator.ReadTargetFrameworks(referencing);
        string? to = ProjectLocator.ReadTargetFrameworks(referenced);

        bool widened = from is not null && to is not null &&
            !from.Contains(';') && !to.Contains(';') &&
            to.StartsWith(from + "-", StringComparison.OrdinalIgnoreCase) &&
            TryWidenTargetFramework(referencing, from, to);

        if (!widened)
        {
            return Observation.Ok(
                ToolName,
                failed,
                $"dotnet add_reference failed: '{referencingRel}' targets {from ?? "an unknown framework"} and " +
                $"'{referencedRel}' targets {to ?? "an unknown framework"}, which the SDK will not wire together. " +
                $"Fix the REFERENCING project: set <TargetFramework> in {referencingRel} to one compatible with " +
                $"'{referencedRel}'. Do not change the referenced project's framework.",
                outcomeOk: false);
        }

        // The project file moved underneath any build already taken.
        _cache?.Invalidate();

        CommandResult retry = await _executor.ExecuteAsync(
            new CommandRequest("dotnet", arguments)
            {
                WorkingDirectory = workingDirectory,
                RequiresNetwork = false,
                Timeout = TimeSpan.FromSeconds(_sandbox.CommandTimeoutSeconds),
            },
            cancellationToken).ConfigureAwait(false);

        if (retry.FailureReason is not null || retry.TimedOut || retry.ExitCode != 0)
        {
            DotnetProjectResult still = new(
                "add_reference", referencingRel, false, retry.ExitCode,
                Trim(retry.CombinedOutput), retry.Duration.TotalMilliseconds);

            return Observation.Ok(
                ToolName,
                still,
                $"'{referencingRel}' was widened from {from} to {to} (see the change log), but the add " +
                $"still failed with exit {retry.ExitCode}.",
                outcomeOk: false);
        }

        DotnetProjectResult repaired = new(
            "add_reference", referencingRel, true, 0,
            Trim(retry.CombinedOutput), retry.Duration.TotalMilliseconds);

        return Observation.Ok(
            ToolName,
            repaired,
            $"'{referencingRel}' now references '{referencedRel}'. Its TargetFramework was widened from " +
            $"{from} to {to} first - a project referencing a Windows app must target Windows itself. " +
            "The csproj edit is in the change log.");
    }

    /// <summary>The project file a path means: itself when it names one, the directory's single
    /// project when it names a directory, null when there is no single answer.</summary>
    private static string? ProjectFileOf(string path) =>
        File.Exists(path) ? path
        : Directory.Exists(path) ? SingleProjectIn(path)
        : null;

    /// <summary>
    /// Rewrites the single <c>TargetFramework</c> element from one value to another, through the
    /// change log. False when the element is not there to rewrite, in which case nothing changed.
    /// </summary>
    private bool TryWidenTargetFramework(string projectFile, string from, string to)
    {
        try
        {
            string before = File.ReadAllText(projectFile);
            string needle = $"<TargetFramework>{from}</TargetFramework>";
            if (!before.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string after = before.Replace(
                needle, $"<TargetFramework>{to}</TargetFramework>", StringComparison.OrdinalIgnoreCase);
            File.WriteAllText(projectFile, after);

            CodeChange change = _changes.Propose(_guard.ToRelativePath(projectFile), ToolName, before, after);
            _changes.Update(change.Id, ChangeStatus.Applied);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Turns a repo-relative second operand into a full path, leaving it alone if it already is one.</summary>
    private string Resolve(string relative) =>
        Path.GetFullPath(Path.Combine(_guard.RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Project extensions an add_to_solution argument can name.</summary>
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>
    /// Puts a swapped <c>add_to_solution</c> the right way round: the solution as the target and
    /// the project as the argument, however the caller sent them.
    /// <para>
    /// Only when the argument names a solution - then the shapes cannot be what the contract
    /// says, and the caller's intent is unambiguous. The project is the path when it names one,
    /// else the single project in the directory the path points at; the solution is taken as
    /// named when it exists, else looked for beside the project, because a bare
    /// <c>sln.slnx</c> means "the solution I just made there", not one at the workspace root.
    /// A shape this cannot repair goes through unchanged and fails where it always failed.
    /// </para>
    /// </summary>
    private (string Path, string Argument) NormalizeSolutionAdd(string path, string argument)
    {
        if (!HasExtension(argument, SolutionExtensions))
        {
            return (path, argument);
        }

        try
        {
            string root = _guard.RepoRoot;
            string fullPath = Path.GetFullPath(
                Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

            string? project = HasExtension(path, ProjectExtensions)
                ? fullPath
                : Directory.Exists(fullPath)
                    ? SingleProjectIn(fullPath)
                    : null;

            if (project is null)
            {
                return (path, argument);
            }

            string solution = Path.GetFullPath(
                Path.Combine(root, argument.Replace('/', Path.DirectorySeparatorChar)));
            if (!File.Exists(solution))
            {
                string beside = Path.Combine(Path.GetDirectoryName(project)!, Path.GetFileName(argument));
                if (File.Exists(beside))
                {
                    solution = beside;
                }
            }

            return (_guard.ToRelativePath(solution), _guard.ToRelativePath(project));
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (path, argument);
        }
    }

    private static bool HasExtension(string value, string[] extensions) =>
        extensions.Contains(Path.GetExtension(value), StringComparer.OrdinalIgnoreCase);

    /// <summary>The directory's one project file, or null when there is none or no single answer.</summary>
    private static string? SingleProjectIn(string directory)
    {
        List<string> projects = [];
        foreach (string extension in ProjectExtensions)
        {
            projects.AddRange(Directory.EnumerateFiles(directory, "*" + extension));
        }

        return projects.Count == 1 ? projects[0] : null;
    }

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

        // The framework goes in the summary because it is the one fact the next call trips
        // over: wpf scaffolds net10.0-windows, xunit scaffolds net10.0, and both runs a408b61b
        // and ca727be3 discovered the mismatch only from add_reference's exit 1.
        string? framework = ProjectLocator.EnumerateProjects(directory).FirstOrDefault() is { } projectFile
            ? ProjectLocator.ReadTargetFrameworks(projectFile)
            : null;
        string created = framework is null
            ? $"Created a {template} project in '{verdict.RelativePath}'."
            : $"Created a {template} project in '{verdict.RelativePath}' targeting {framework}.";

        // A desktop template's scaffold is the starting skeleton, not a stub: the run's work IS
        // editing that window. Deleting it would hand every WPF task back the blank-workspace
        // problem the template exists to solve.
        if (DesktopTemplates.Contains(template, StringComparer.OrdinalIgnoreCase))
        {
            return $"{created} The scaffolded window and application files are the starting skeleton - " +
                "edit them in place rather than re-creating them. Build next.";
        }

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
        string name = SolutionName(
            argument ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(verdict.FullPath!)));

        if (LocateCreatedSolution(verdict, argument) is not { } created)
        {
            return $"Created a solution named '{name}'.";
        }

        // Said at creation, because afterwards nobody says it: run ca727be3's solution sat in a
        // subdirectory where build-target resolution never looks, empty, and no surface
        // mentioned it again for the rest of the run.
        bool atRoot = string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(created)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_guard.RepoRoot)),
            StringComparison.OrdinalIgnoreCase);

        return $"Created a solution at '{_guard.ToRelativePath(created)}'. Use this exact path with add_to_solution."
            + (atRoot
                ? string.Empty
                : " Note: it is not at the workspace root, so builds will not resolve it as their target - "
                  + "projects build individually. If you do not need a solution, skip it.");
    }

    /// <summary>The file <c>dotnet new sln</c> wrote, whichever format the SDK chose.</summary>
    private string? LocateCreatedSolution(PathGuardResult verdict, string? argument)
    {
        string full = verdict.FullPath!;
        bool namesAFile = Path.GetExtension(full) is ".sln" or ".slnx";
        string directory = namesAFile ? Path.GetDirectoryName(full) ?? _guard.RepoRoot : full;
        string name = SolutionName(
            argument ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(full)));

        return SolutionExtensions
            .Select(extension => Path.Combine(directory, name + extension))
            .FirstOrDefault(File.Exists);
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
