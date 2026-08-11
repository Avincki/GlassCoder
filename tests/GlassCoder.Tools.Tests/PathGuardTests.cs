using GlassCoder.TestSupport;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Options;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The guardrail is the only thing standing between an agent and the rest of the filesystem
/// (workplan task 8), so these tests are about what it <em>refuses</em>.
/// </summary>
public sealed class PathGuardTests : IDisposable
{
    private readonly TempWorkspace _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void Write_inside_the_writable_set_is_allowed()
    {
        _workspace.WriteFile("src/Program.cs", "// code");
        PathGuard guard = _workspace.Guard("src");

        PathGuardResult result = guard.Resolve("src/Program.cs", PathAccess.Write);

        result.Allowed.ShouldBeTrue(result.Reason);
        result.RelativePath.ShouldBe("src/Program.cs");
    }

    [Fact]
    public void Write_outside_the_writable_set_is_rejected()
    {
        _workspace.WriteFile("docs/README.md", "# docs");
        PathGuard guard = _workspace.Guard("src");

        PathGuardResult result = guard.Resolve("docs/README.md", PathAccess.Write);

        result.Allowed.ShouldBeFalse();
        result.Reason.ShouldContain("writable set");
    }

    [Fact]
    public void Write_is_rejected_outright_when_no_writable_paths_are_configured()
    {
        PathGuard guard = _workspace.Guard();

        PathGuardResult result = guard.Resolve("src/Program.cs", PathAccess.Write);

        result.Allowed.ShouldBeFalse();
        result.Reason.ShouldContain("No writable paths");
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("src/../../escape.txt")]
    [InlineData("src/./../../escape.txt")]
    public void Traversal_out_of_the_repository_is_rejected(string path)
    {
        PathGuard guard = _workspace.Guard("src");

        guard.Resolve(path, PathAccess.Write).Allowed.ShouldBeFalse();
        guard.Resolve(path, PathAccess.Read).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Absolute_paths_outside_the_repository_are_rejected()
    {
        PathGuard guard = _workspace.Guard("src");

        guard.Resolve(Path.Combine(Path.GetTempPath(), "elsewhere.txt"), PathAccess.Read).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void A_sibling_directory_sharing_the_root_prefix_is_not_inside_it()
    {
        // "…/repo-other" starts with "…/repo" as a string but is not under it as a path.
        string sibling = _workspace.Root + "-other";
        Directory.CreateDirectory(sibling);
        try
        {
            PathGuard guard = _workspace.Guard("src");

            guard.Resolve(Path.Combine(sibling, "file.txt"), PathAccess.Read).Allowed.ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void Denied_globs_are_excluded_even_inside_the_writable_set()
    {
        _workspace.WriteFile(".git/config", "[core]");
        _workspace.WriteFile("src/obj/generated.cs", "// generated");
        PathGuard guard = _workspace.Guard(".", "src");

        guard.Resolve(".git/config", PathAccess.Read).Allowed.ShouldBeFalse();
        guard.Resolve("src/obj/generated.cs", PathAccess.Write).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Reads_default_to_the_repository_root_when_no_readable_paths_are_configured()
    {
        _workspace.WriteFile("docs/README.md", "# docs");
        PathGuard guard = _workspace.Guard();

        guard.Resolve("docs/README.md", PathAccess.Read).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_path_is_rejected()
    {
        PathGuard guard = _workspace.Guard("src");

        guard.Resolve(null, PathAccess.Read).Allowed.ShouldBeFalse();
        guard.Resolve("   ", PathAccess.Read).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void Readable_paths_may_be_narrowed_below_the_repository_root()
    {
        _workspace.WriteFile("src/Program.cs", "// code");
        _workspace.WriteFile("secrets/keys.txt", "sk-live");
        WorkspaceOptions options = new() { RepoRoot = _workspace.Root };
        options.ReadablePaths.Add("src");
        PathGuard guard = new(Options.Create(options));

        guard.Resolve("src/Program.cs", PathAccess.Read).Allowed.ShouldBeTrue();
        guard.Resolve("secrets/keys.txt", PathAccess.Read).Allowed.ShouldBeFalse();
    }

    /// <summary>
    /// Run <c>46231701</c>: with src and tests writable and a solution correctly refused below the
    /// root, no run could produce a solution anywhere - so none was produced, and
    /// <c>dotnet test</c> from the root had nothing to run.
    /// </summary>
    [Fact]
    public void A_repository_artifact_is_writable_at_the_root_the_writable_set_does_not_reach()
    {
        PathGuard guard = _workspace.Guard("src");

        guard.Resolve("MultiplyApp.slnx", PathAccess.Write).Allowed.ShouldBeTrue();
        guard.Resolve("MultiplyApp.sln", PathAccess.Write).Allowed.ShouldBeTrue();
        guard.Resolve(".gitignore", PathAccess.Write).Allowed.ShouldBeTrue();
        guard.Resolve("Directory.Build.props", PathAccess.Write).Allowed.ShouldBeTrue();
    }

    [Fact]
    public void The_root_allow_list_admits_nothing_but_the_artifacts_it_names()
    {
        PathGuard guard = _workspace.Guard("src");

        // The whole reason this is a file-name list: the root stays closed to source.
        guard.Resolve("Program.cs", PathAccess.Write).Allowed.ShouldBeFalse();
        guard.Resolve("MainWindow.xaml", PathAccess.Write).Allowed.ShouldBeFalse();
        guard.Resolve(".editorconfig", PathAccess.Write).Allowed.ShouldBeFalse();
        guard.Resolve("NuGet.config", PathAccess.Write).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void An_artifact_name_one_level_down_is_still_refused()
    {
        // The refusal task 73 exists for. A solution is admitted because it is at the root, not
        // because of what it is called - otherwise this would hand back the orphan it prevents.
        PathGuard guard = _workspace.Guard("docs");

        guard.Resolve("docs/MultiplyApp.slnx", PathAccess.Write).Allowed.ShouldBeTrue("docs is writable");
        guard.Resolve("tests/MultiplyApp.slnx", PathAccess.Write).Allowed.ShouldBeFalse();
        guard.Resolve("src/App/App.slnx", PathAccess.Write).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void A_workspace_with_no_writable_paths_still_writes_nothing_at_all()
    {
        // The invariant the guard rests on outranks the allow-list: an unconfigured harness is a
        // harmless one, and a root artifact is not the exception that opens it.
        PathGuard guard = new(Options.Create(new WorkspaceOptions { RepoRoot = _workspace.Root }));

        guard.Resolve("MultiplyApp.slnx", PathAccess.Write).Allowed.ShouldBeFalse();
    }

    [Fact]
    public void The_root_allow_list_can_be_emptied_by_configuration()
    {
        WorkspaceOptions options = new() { RepoRoot = _workspace.Root };
        options.WritablePaths.Add("src");
        options.WritableRootFiles.Clear();
        PathGuard guard = new(Options.Create(options));

        guard.Resolve("MultiplyApp.slnx", PathAccess.Write).Allowed.ShouldBeFalse();
        guard.Resolve("src/Program.cs", PathAccess.Write).Allowed.ShouldBeTrue();
    }
}
