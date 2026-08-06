using GlassCoder.Core.Context;
using GlassCoder.Tools.FileSystem;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Options;

namespace GlassCoder.TestSupport;

/// <summary>Builds a real <see cref="ContextAssembler"/> over in-memory options, for tests.</summary>
public static class TestContextAssembler
{
    /// <summary>
    /// Creates an assembler with the given options, an optional path guard, and an optional
    /// workspace-map builder. Without the builder the opening window has no map, which keeps
    /// the many tests that count messages independent of the temp directory's contents.
    /// </summary>
    public static ContextAssembler Create(
        ContextOptions? options = null,
        IPathGuard? guard = null,
        WorkspaceMapBuilder? workspaceMap = null)
    {
        ContextOptions effective = options ?? new ContextOptions();
        IOptions<ContextOptions> wrapped = Options.Create(effective);
        HeuristicTokenEstimator estimator = new(wrapped);

        return new ContextAssembler(
            wrapped,
            estimator,
            new DigestCompactor(estimator),
            guard ?? new PathGuard(Options.Create(new WorkspaceOptions { RepoRoot = "." })),
            workspaceMap);
    }
}
