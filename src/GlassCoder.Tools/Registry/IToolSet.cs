namespace GlassCoder.Tools.Registry;

/// <summary>
/// Marker for a class that contributes tools. Every public method carrying
/// <see cref="GlassCoderToolAttribute"/> is registered, schema and all.
/// </summary>
/// <remarks>
/// Tool sets are resolved from DI, so a tool can depend on the path guard, the process runner
/// or anything else that is worth faking in a test (CLAUDE.md §14).
/// </remarks>
public interface IToolSet;

/// <summary>
/// Contributes tools that are not <see cref="GlassCoderToolAttribute"/> methods, because they
/// are not methods at all - an MCP server declares them and the harness adapts them (workplan
/// task 57).
/// <para>
/// §7's invariant is that a schema is generated from the signature that executes it, so the two
/// cannot drift. An adapted tool weakens that to "trust the server", and there is no way to keep
/// it fully. What is kept: the same schema validation at registration, our own name and
/// description, and the same observation contract on the way back.
/// </para>
/// </summary>
public interface IToolFunctionSource
{
    /// <summary>The functions to advertise, in the order they should appear.</summary>
    IReadOnlyList<Microsoft.Extensions.AI.AIFunction> Functions { get; }
}
