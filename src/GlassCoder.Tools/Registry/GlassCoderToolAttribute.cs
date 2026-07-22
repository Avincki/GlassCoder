namespace GlassCoder.Tools.Registry;

/// <summary>
/// Marks a method as a tool the model may call. The JSON schema is generated from the
/// signature (CLAUDE.md §7) - this attribute only supplies what a signature cannot: the wire
/// name and the position in the tool list.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class GlassCoderToolAttribute : Attribute
{
    /// <summary>Declares a tool under its wire name.</summary>
    /// <param name="name">Snake-case name the model calls, for example <c>read_file</c>.</param>
    public GlassCoderToolAttribute(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <summary>Snake-case name the model calls.</summary>
    public string Name { get; }

    /// <summary>
    /// Sort key for the advertised tool list. Order matters: <c>build</c> must precede
    /// <c>run_tests</c> because it is the cheaper, higher-value oracle (CLAUDE.md §7, §8).
    /// </summary>
    public int Order { get; init; }
}
