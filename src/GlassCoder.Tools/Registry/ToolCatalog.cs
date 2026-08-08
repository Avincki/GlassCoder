using System.ComponentModel;
using System.Reflection;
using GlassCoder.Tools.Build;
using GlassCoder.Tools.Execution;
using GlassCoder.Tools.Git;
using Microsoft.Extensions.AI;

namespace GlassCoder.Tools.Registry;

/// <summary>
/// Every tool the build knows about, and whether this session offers it (workplan task 64).
/// <para>
/// The registry holds what was registered, which is not the same question. A disabled tool set is
/// never registered <em>and never constructed</em> - with <c>Git:Enabled</c> false,
/// <c>AddGitTools</c> is not called, <c>GitTool</c> is not in the container, and no
/// <see cref="AIFunction"/> exists to read a description from. That is deliberate: gating
/// registration rather than execution is what keeps a switched-off tool out of the model's schema
/// entirely, and it is why "what could this do, and what is switching it off" cannot be answered
/// from <see cref="IToolRegistry"/> alone.
/// </para>
/// <para>
/// So the full set is discovered by reflecting over <em>types</em> rather than instances: no
/// construction, no dependencies to satisfy, and it keys on the same
/// <see cref="GlassCoderToolAttribute"/> that <see cref="ToolFunctionFactory"/> does, so a tool
/// added later cannot go missing here.
/// </para>
/// </summary>
public static class ToolCatalog
{
    /// <summary>
    /// The configuration key that registers each opt-in tool set, keyed by the type it registers.
    /// <para>
    /// Built from the same constants <c>ToolsServiceCollectionExtensions</c> reads, so the switch
    /// a row names and the switch that actually gates it cannot drift apart. A tool set absent
    /// from this map is one the default registration always adds.
    /// </para>
    /// </summary>
    private static readonly Dictionary<Type, string> Switches = new()
    {
        [typeof(GitTool)] = $"{GitOptions.SectionName}:{nameof(GitOptions.Enabled)}",
        [typeof(BashTool)] = $"{SandboxOptions.SectionName}:{nameof(SandboxOptions.EnableBashTool)}",
    };

    /// <summary>
    /// The catalogue, in advertised order, with the tools this session registered marked active.
    /// </summary>
    /// <param name="registry">The live registry - what the model is actually offered.</param>
    /// <param name="retrieval">
    /// Configured retrieval, so the MCP tools appear when they are switched off as well as when
    /// they are on. They are adapted from a server at run time rather than declared as
    /// <see cref="GlassCoderToolAttribute"/> methods, so the type sweep cannot see them and the
    /// registry only holds them once something registered them - which is exactly the case this
    /// list exists to explain. Null when the harness has no retrieval configuration at all.
    /// </param>
    public static IReadOnlyList<ToolCatalogEntry> Describe(
        IToolRegistry registry, Retrieval.RetrievalOptions? retrieval = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        List<ToolCatalogEntry> entries = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (Type type in ToolSetTypes())
        {
            Switches.TryGetValue(type, out string? gatedBy);

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                GlassCoderToolAttribute? tool = method.GetCustomAttribute<GlassCoderToolAttribute>();
                if (tool is null)
                {
                    continue;
                }

                seen.Add(tool.Name);
                bool active = registry.TryGetFunction(tool.Name, out AIFunction? function) && function is not null;

                entries.Add(new ToolCatalogEntry(
                    Name: tool.Name,
                    // The live description when there is one - literally what was sent. The
                    // attribute otherwise, which is the string that would be sent. Same text by
                    // construction; ToolCatalogTests asserts they agree wherever both exist.
                    Description: (active ? function!.Description : null)
                        ?? method.GetCustomAttribute<DescriptionAttribute>()?.Description
                        ?? string.Empty,
                    Order: tool.Order,
                    Active: active,
                    SchemaCharacters: active ? function!.JsonSchema.GetRawText().Length : null,
                    EnabledBy: active ? null : gatedBy));
            }
        }

        entries.Sort(static (left, right) =>
        {
            int byOrder = left.Order.CompareTo(right.Order);
            return byOrder != 0 ? byOrder : string.CompareOrdinal(left.Name, right.Name);
        });

        // Registered tools that no [GlassCoderTool] method declares - which is what an MCP tool
        // adapted from a server will be (workplan task 57). They are real and they are active, so
        // they belong on the list; they have no declared Order, so they follow in registry order.
        foreach (AIFunction function in registry.Functions)
        {
            if (seen.Add(function.Name))
            {
                entries.Add(new ToolCatalogEntry(
                    function.Name,
                    function.Description ?? string.Empty,
                    Order: int.MaxValue,
                    Active: true,
                    SchemaCharacters: function.JsonSchema.GetRawText().Length,
                    EnabledBy: null));
            }
        }

        // And the retrieval tools that are configured but not registered - the case the first two
        // sources structurally cannot cover. Reflection cannot see them because they are not
        // methods, and the registry cannot list them because being switched off is precisely what
        // keeps them out of it. Without this, the default install shows no MCP tool at all and the
        // list quietly stops being an inventory of what the build can do.
        if (retrieval is not null)
        {
            foreach (Retrieval.RetrievalServer server in
                (Retrieval.RetrievalServer[])Enum.GetValues(typeof(Retrieval.RetrievalServer)))
            {
                Retrieval.RetrievalServerOptions settings = retrieval.For(server);

                foreach (Retrieval.RetrievalToolOptions tool in settings.Tools)
                {
                    if (string.IsNullOrWhiteSpace(tool.Name) || !seen.Add(tool.Name))
                    {
                        continue;
                    }

                    entries.Add(new ToolCatalogEntry(
                        tool.Name,
                        tool.Description,
                        Order: int.MaxValue,
                        Active: false,
                        SchemaCharacters: null,
                        EnabledBy: Switch(retrieval, settings, server),
                        Unavailable: Unavailable(retrieval, settings)));
                }
            }
        }

        return entries;
    }

    /// <summary>
    /// The setting to change to get a configured retrieval tool registered - the master switch
    /// when that is what is off, the server's own when it is not. Null when both are already on
    /// and the reason is something a setting cannot fix.
    /// </summary>
    private static string? Switch(
        Retrieval.RetrievalOptions retrieval,
        Retrieval.RetrievalServerOptions settings,
        Retrieval.RetrievalServer server)
    {
        if (!retrieval.Enabled)
        {
            return $"{Retrieval.RetrievalOptions.SectionName}:{nameof(Retrieval.RetrievalOptions.Enabled)}";
        }

        return settings.Enabled
            ? null
            : $"{Retrieval.RetrievalOptions.SectionName}:{server}:{nameof(Retrieval.RetrievalServerOptions.Enabled)}";
    }

    /// <summary>
    /// Why a switched-on retrieval tool is still absent. Always the corpus: registration reads
    /// the recorded tool list so that a Replay run opens no socket, and a cold corpus therefore
    /// has nothing to register.
    /// </summary>
    private static string? Unavailable(
        Retrieval.RetrievalOptions retrieval, Retrieval.RetrievalServerOptions settings) =>
        retrieval.Enabled && settings.Enabled
            ? $"configured, but no recorded tool list - run once with Retrieval:Mode=Record"
            : null;

    /// <summary>
    /// The tool set types in this assembly. Concrete classes only - the interface is a marker and
    /// nothing abstract can carry an executable tool.
    /// </summary>
    private static IEnumerable<Type> ToolSetTypes() => typeof(IToolSet).Assembly
        .GetTypes()
        .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IToolSet)))
        .OrderBy(type => type.Name, StringComparer.Ordinal);
}

/// <summary>One tool the build knows about, and what this session does with it.</summary>
/// <param name="Name">The wire name the model calls.</param>
/// <param name="Description">What it is for - the same text the model is given.</param>
/// <param name="Order">Sort key for the advertised list; order is part of the contract.</param>
/// <param name="Active">Whether this session registered it, and so whether the model is offered it.</param>
/// <param name="SchemaCharacters">
/// Size of the generated schema, when there is one. This is the figure as the harness generates
/// it; what reaches the wire is larger, because the client re-serialises it indented.
/// </param>
/// <param name="EnabledBy">
/// The configuration key that would switch an inactive tool on. Null when the tool is active, and
/// also null when it is inactive with no known switch - which is either a tool registered by no
/// path at all, worth noticing, or one whose absence <paramref name="Unavailable"/> explains.
/// </param>
/// <param name="Unavailable">
/// Why an inactive tool is absent for a reason no setting fixes. Null in every other case, so
/// "inactive, no switch, no explanation" stays the shape that means a defect.
/// </param>
public sealed record ToolCatalogEntry(
    string Name,
    string Description,
    int Order,
    bool Active,
    int? SchemaCharacters,
    string? EnabledBy,
    string? Unavailable = null);
