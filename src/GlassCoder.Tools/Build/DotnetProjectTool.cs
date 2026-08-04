using System.ComponentModel;
using System.Text.Json.Serialization;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Execution;
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

    /// <summary>Templates worth offering. Anything else is a build system this cannot vouch for.</summary>
    private static readonly string[] KnownTemplates =
        ["xunit", "nunit", "mstest", "classlib", "console", "web", "webapi", "worker", "blazor"];

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
    [Description("Create and wire up .NET projects with the dotnet CLI. Use this rather than hand-editing a "
        + ".csproj. A test project is normally new, then add_reference, then build.")]
    public async Task<ToolObservation<DotnetProjectResult>> RunAsync(
        [Description("What to do.")]
        DotnetProjectOperation operation,
        [Description("Repo-relative target. For new, the directory to create the project in; its name comes "
            + "from that directory. Otherwise the project or solution being changed.")]
        string path,
        [Description("Template for new (xunit, classlib, console, ...); referenced project for add_reference; "
            + "package id for add_package; project to add for add_to_solution. Unused by restore.")]
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

        return Observation.Ok(ToolName, payload, Success(operation, verdict.RelativePath!, argument));
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
                string name = argument
                    ?? Path.GetFileNameWithoutExtension(Path.TrimEndingDirectorySeparator(full));

                return (["new", "sln", "-o", directory, "-n", name], root, null);
            }

            case DotnetProjectOperation.AddToSolution:
                return (["sln", full, "add", Resolve(argument!)], root, full);

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

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    /// <summary>Turns a repo-relative second operand into a full path, leaving it alone if it already is one.</summary>
    private string Resolve(string relative) =>
        Path.GetFullPath(Path.Combine(_guard.RepoRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

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

    private static bool RequiresArgument(DotnetProjectOperation operation) => operation is
        DotnetProjectOperation.New or
        DotnetProjectOperation.AddToSolution or
        DotnetProjectOperation.AddReference or
        DotnetProjectOperation.AddPackage;

    private static bool NeedsNetwork(DotnetProjectOperation operation) => operation is
        DotnetProjectOperation.AddPackage or
        DotnetProjectOperation.Restore or
        DotnetProjectOperation.New;

    private static string Describe(DotnetProjectOperation operation) => operation switch
    {
        DotnetProjectOperation.New => "new",
        DotnetProjectOperation.NewSolution => "new_solution",
        DotnetProjectOperation.AddToSolution => "add_to_solution",
        DotnetProjectOperation.AddReference => "add_reference",
        DotnetProjectOperation.AddPackage => "add_package",
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
        DotnetProjectOperation.New =>
            $"Created a {argument} project in '{path}'. Add a reference to the code under test, then build.",
        DotnetProjectOperation.NewSolution => $"Created a solution in '{path}'.",
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
