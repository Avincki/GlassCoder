using GlassCoder.Core.Context;
using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Options;

namespace GlassCoder.TestSupport;

/// <summary>Builds a real <see cref="ContextAssembler"/> over in-memory options, for tests.</summary>
public static class TestContextAssembler
{
    /// <summary>Creates an assembler with the given options and an optional path guard.</summary>
    public static ContextAssembler Create(ContextOptions? options = null, IPathGuard? guard = null)
    {
        ContextOptions effective = options ?? new ContextOptions();
        IOptions<ContextOptions> wrapped = Options.Create(effective);
        HeuristicTokenEstimator estimator = new(wrapped);

        return new ContextAssembler(
            wrapped,
            estimator,
            new DigestCompactor(estimator),
            guard ?? new PathGuard(Options.Create(new WorkspaceOptions { RepoRoot = "." })));
    }
}
