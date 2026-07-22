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
