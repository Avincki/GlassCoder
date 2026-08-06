using GlassCoder.TestSupport;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Guardrails;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The harness marks its own build output as ignored by Dropbox (2026-08-06 run analysis).
/// <para>
/// The launcher sweeps the folder it launches, at launch - which is the wrong root and the
/// wrong time for a workspace the harness builds into mid-run. These tests pin the contract
/// that closes that gap: existing output is marked, project directories get bin and obj
/// pre-created and marked before the first build, a clean survives, and a workspace outside
/// Dropbox is never touched.
/// </para>
/// <para>
/// The tests run on a real NTFS temp directory, because the com.dropbox.ignored stream is an
/// alternate data stream and a fake filesystem would test nothing.
/// </para>
/// </summary>
public sealed class DropboxIgnoreMarkerTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Existing_build_output_is_marked()
    {
        _workspace.CreateDirectory("src/App/bin");
        _workspace.CreateDirectory("src/App/obj");

        Marker().EnsureWorkspaceMarked();

        StreamValue("src/App/bin").ShouldBe("1");
        StreamValue("src/App/obj").ShouldBe("1");
    }

    [Fact]
    public void A_project_directory_gets_bin_and_obj_pre_created_and_marked()
    {
        // The timing half of the contract: created and marked before the first build exists,
        // so the SDK's first write lands in a folder Dropbox is already ignoring.
        _workspace.WriteFile("src/App/App.csproj", "<Project />");

        Marker().EnsureWorkspaceMarked();

        StreamValue("src/App/bin").ShouldBe("1");
        StreamValue("src/App/obj").ShouldBe("1");
    }

    [Fact]
    public void A_clean_that_recreates_the_folder_is_re_marked_on_the_next_sweep()
    {
        // The stream dies with the folder; the sweep after the next command resurrects it.
        _workspace.WriteFile("src/App/App.csproj", "<Project />");
        DropboxIgnoreMarker marker = Marker();
        marker.EnsureWorkspaceMarked();
        Directory.Delete(Path.Combine(_workspace.Root, "src", "App", "obj"), recursive: true);

        marker.EnsureWorkspaceMarked();

        StreamValue("src/App/obj").ShouldBe("1");
    }

    [Fact]
    public void A_workspace_outside_dropbox_is_never_touched()
    {
        _workspace.WriteFile("src/App/App.csproj", "<Project />");
        _workspace.CreateDirectory("src/App/bin");

        Marker(dropboxRoot: Path.Combine(Path.GetTempPath(), "somewhere-else")).EnsureWorkspaceMarked();

        StreamValue("src/App/bin").ShouldBeNull();
        Directory.Exists(Path.Combine(_workspace.Root, "src", "App", "obj")).ShouldBeFalse();
    }

    [Fact]
    public void The_switch_turns_the_whole_pass_off()
    {
        _workspace.CreateDirectory("src/App/bin");
        WorkspaceOptions options = _workspace.Options();
        options.ExcludeBuildOutputFromDropbox = false;

        new DropboxIgnoreMarker(_workspace.Guard(), TempWorkspace.Wrap(options), dropboxRootsOverride: [_workspace.Root])
            .EnsureWorkspaceMarked();

        StreamValue("src/App/bin").ShouldBeNull();
    }

    [Fact]
    public void Dot_directories_are_not_descended_into_but_dot_vs_is_still_marked()
    {
        // .git is data, not output - nothing under it may be touched. .vs is the one dotted
        // name that IS output, so the target check has to run before the dot skip.
        _workspace.CreateDirectory(".hidden/bin");
        _workspace.CreateDirectory(".vs");

        Marker().EnsureWorkspaceMarked();

        StreamValue(".hidden/bin").ShouldBeNull();
        StreamValue(".vs").ShouldBe("1");
    }

    [Fact]
    public async Task Every_sandboxed_command_sweeps_the_workspace()
    {
        // The seam: no tool has to remember to mark anything - anything that executes a
        // command gets the sweep, and folders created mid-command are marked right after it.
        _workspace.WriteFile("src/App/App.csproj", "<Project />");
        SandboxOptions options = new() { Mode = SandboxMode.Local, AllowUnsandboxedExecution = true };
        FakeProcessRunner runner = new();
        runner.Enqueue(0, "ok");
        PathGuard guard = _workspace.Guard();

        SandboxedCommandExecutor executor = new(
            new DockerCommandExecutor(TempWorkspace.Wrap(options), guard),
            new LocalCommandExecutor(runner, TempWorkspace.Wrap(options)),
            TempWorkspace.Wrap(options),
            Marker());

        CommandResult result = await executor.ExecuteAsync(new CommandRequest("dotnet", ["build"]));

        result.Succeeded.ShouldBeTrue();
        StreamValue("src/App/bin").ShouldBe("1");
        StreamValue("src/App/obj").ShouldBe("1");
    }

    private DropboxIgnoreMarker Marker(string? dropboxRoot = null) => new(
        _workspace.Guard(),
        TempWorkspace.Wrap(_workspace.Options()),
        dropboxRootsOverride: [dropboxRoot ?? _workspace.Root]);

    /// <summary>The ignore stream's content, or null when the folder carries none.</summary>
    private string? StreamValue(string relativePath)
    {
        string directory = Path.Combine(_workspace.Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            return File.ReadAllText(directory + ":com.dropbox.ignored").Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}
