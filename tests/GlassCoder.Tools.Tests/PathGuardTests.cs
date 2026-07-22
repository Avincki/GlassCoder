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
}
