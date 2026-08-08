using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace GlassCoder.Tools.Registry;

/// <summary>
/// Turns <see cref="IToolSet"/> methods into <see cref="AIFunction"/>s with schemas generated
/// from their signatures (CLAUDE.md §7, workplan task 7).
/// <para>
/// Schemas are never hand-written. That is the whole trick: the model's contract is derived
/// from the executor, so the two cannot drift apart. What this class adds on top of
/// <see cref="AIFunctionFactory"/> is enforcement of the conventions a signature cannot carry -
/// every method and every parameter must be described, names must be unique, and the generated
/// schema must be a usable object schema.
/// </para>
/// </summary>
public static class ToolFunctionFactory
{
    /// <summary>
    /// JSON options used for tool schemas, arguments and observations.
    /// <para>
    /// <see cref="AIJsonUtilities.DefaultOptions"/> writes indented, which is right for a library
    /// whose output a human reads and wrong for everything here: this JSON goes on the wire. The
    /// indentation was 22% of the advertised schemas - re-sent on every request for the whole run
    /// - and it is in every tool observation fed back into the conversation as well.
    /// </para>
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } =
        new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>
    /// Builds the ordered function list for the given tool sets.
    /// </summary>
    /// <exception cref="ToolContractException">A tool method breaks the CLAUDE.md §7 contract.</exception>
    public static IReadOnlyList<AIFunction> Create(IEnumerable<IToolSet> toolSets)
    {
        ArgumentNullException.ThrowIfNull(toolSets);

        List<(int Order, string Name, AIFunction Function)> created = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (IToolSet toolSet in toolSets)
        {
            Type type = toolSet.GetType();
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                GlassCoderToolAttribute? attribute = method.GetCustomAttribute<GlassCoderToolAttribute>();
                if (attribute is null)
                {
                    continue;
                }

                Validate(type, method, attribute, seen);
                AIFunction function = AIFunctionFactory.Create(
                    method,
                    toolSet,
                    new AIFunctionFactoryOptions
                    {
                        Name = attribute.Name,
                        SerializerOptions = SerializerOptions,
                    });

                ValidateSchema(type, method, attribute, function);
                created.Add((attribute.Order, attribute.Name, function));
            }
        }

        return [.. created.OrderBy(c => c.Order).ThenBy(c => c.Name, StringComparer.Ordinal).Select(c => c.Function)];
    }

    private static void Validate(Type type, MethodInfo method, GlassCoderToolAttribute attribute, HashSet<string> seen)
    {
        string origin = $"{type.Name}.{method.Name} (tool '{attribute.Name}')";

        if (!seen.Add(attribute.Name))
        {
            throw new ToolContractException($"{origin}: tool name '{attribute.Name}' is registered more than once.");
        }

        if (string.IsNullOrWhiteSpace(method.GetCustomAttribute<DescriptionAttribute>()?.Description))
        {
            throw new ToolContractException(
                $"{origin}: tool methods must carry [Description] - it becomes the model's only guidance on when to call it.");
        }

        foreach (ParameterInfo parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
            {
                throw new ToolContractException(
                    $"{origin}: parameter '{parameter.Name}' must carry [Description] - it lands in the generated JSON schema.");
            }
        }

        if (method.ReturnType == typeof(void))
        {
            throw new ToolContractException(
                $"{origin}: tools must return an observation object, never void - the loop feeds the result back to the model.");
        }
    }

    private static void ValidateSchema(Type type, MethodInfo method, GlassCoderToolAttribute attribute, AIFunction function)
    {
        if (!IsObjectSchema(function.JsonSchema))
        {
            throw new ToolContractException(
                $"{type.Name}.{method.Name} (tool '{attribute.Name}'): generated schema is not a JSON object schema: {function.JsonSchema}");
        }
    }

    /// <summary>
    /// Checks a function the harness did not generate - an MCP server declared it, so the
    /// schema arrives as a claim rather than as a consequence of a signature (workplan task 57).
    /// <para>
    /// Refused loudly at startup rather than on first use, mid-run. A server advertising an
    /// unusable schema is a configuration problem, and a run that discovers it at step twelve
    /// has already spent the budget that would have found it at step zero.
    /// </para>
    /// </summary>
    /// <exception cref="ToolContractException">The schema is not a usable object schema.</exception>
    public static void ValidateSchema(AIFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (string.IsNullOrWhiteSpace(function.Description))
        {
            throw new ToolContractException(
                $"Adapted tool '{function.Name}' carries no description - it would reach the model as a " +
                "name with no guidance on when to call it.");
        }

        if (!IsObjectSchema(function.JsonSchema))
        {
            throw new ToolContractException(
                $"Adapted tool '{function.Name}': the server's schema is not a JSON object schema: {function.JsonSchema}");
        }
    }

    private static bool IsObjectSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object &&
        schema.TryGetProperty("type", out JsonElement schemaType) &&
        schemaType.ValueKind == JsonValueKind.String &&
        string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal);
}
