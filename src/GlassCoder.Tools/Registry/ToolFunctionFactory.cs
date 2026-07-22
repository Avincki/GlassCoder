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
    /// <summary>JSON options used for tool arguments and observations.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = AIJsonUtilities.DefaultOptions;

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
        JsonElement schema = function.JsonSchema;
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out JsonElement schemaType) ||
            schemaType.ValueKind != JsonValueKind.String ||
            !string.Equals(schemaType.GetString(), "object", StringComparison.Ordinal))
        {
            throw new ToolContractException(
                $"{type.Name}.{method.Name} (tool '{attribute.Name}'): generated schema is not a JSON object schema: {schema}");
        }
    }
}
