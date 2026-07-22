using System.ComponentModel;
using System.Text.Json;
using GlassCoder.Tools.Registry;
using Microsoft.Extensions.AI;

namespace GlassCoder.Tools.Tests;

/// <summary>
/// The registry's job (workplan task 7): a schema generated from every signature, conventions
/// enforced at registration, and no failure mode that escapes as an exception.
/// </summary>
public sealed class ToolRegistryTests
{
    [Fact]
    public void Registering_a_method_generates_a_valid_object_schema()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        registry.TryGetFunction("echo", out AIFunction? function).ShouldBeTrue();
        JsonElement schema = function!.JsonSchema;
        schema.GetProperty("type").GetString().ShouldBe("object");
        schema.GetProperty("properties").GetProperty("text").GetProperty("description").GetString()
            .ShouldBe("Text to echo back.");
        function.Description.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Tools_are_advertised_in_declared_order()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        registry.Functions.Select(f => f.Name).ShouldBe(["echo", "fails", "throws"]);
    }

    [Fact]
    public void A_method_without_a_description_is_rejected_at_registration()
    {
        ToolContractException exception = Should.Throw<ToolContractException>(() => new ToolRegistry([new UndescribedTool()]));

        exception.Message.ShouldContain("[Description]");
    }

    [Fact]
    public void A_parameter_without_a_description_is_rejected_at_registration()
    {
        ToolContractException exception =
            Should.Throw<ToolContractException>(() => new ToolRegistry([new UndescribedParameterTool()]));

        exception.Message.ShouldContain("parameter");
    }

    [Fact]
    public void A_duplicate_tool_name_is_rejected_at_registration()
    {
        Should.Throw<ToolContractException>(() => new ToolRegistry([new WellFormedTools(), new WellFormedTools()]));
    }

    [Fact]
    public async Task A_successful_call_is_reported_as_succeeded()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(
            new FunctionCallContent("c1", "echo", new Dictionary<string, object?> { ["text"] = "hello" }));

        invocation.Status.ShouldBe(ToolCallStatus.Succeeded);
        invocation.IsValid.ShouldBeTrue();
        Json(invocation.Result).GetProperty("data").GetProperty("value").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task A_handled_tool_failure_still_counts_as_a_valid_call()
    {
        // "Valid" means the call parsed and executed - that is what the tool-call validity rate
        // measures. A tool reporting ok:false did its job.
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent("c2", "fails", null));

        invocation.Status.ShouldBe(ToolCallStatus.Failed);
        invocation.IsValid.ShouldBeTrue();
        Json(invocation.Result).GetProperty("ok").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_tool_comes_back_as_an_observation_not_an_exception()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent("c3", "no_such_tool", null));

        invocation.Status.ShouldBe(ToolCallStatus.UnknownTool);
        invocation.IsValid.ShouldBeFalse();
        invocation.Result.ShouldBeOfType<ToolObservation<object>>()
            .Error!.Code.ShouldBe(ToolErrorCodes.UnknownTool);
    }

    [Fact]
    public async Task A_tool_that_throws_is_contained_and_reported_as_faulted()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent("c4", "throws", null));

        invocation.Status.ShouldBe(ToolCallStatus.Faulted);
        invocation.IsValid.ShouldBeFalse();
        invocation.ErrorMessage.ShouldContain("boom");
    }

    private static JsonElement Json(object? result) =>
        result is JsonElement element
            ? element
            : JsonDocument.Parse(JsonSerializer.Serialize(result, ToolFunctionFactory.SerializerOptions)).RootElement;

    private sealed class WellFormedTools : IToolSet
    {
        [GlassCoderTool("echo", Order = 1)]
        [Description("Echoes text back, for tests.")]
        public ToolObservation<EchoData> Echo([Description("Text to echo back.")] string text) =>
            Observation.Ok("echo", new EchoData(text), "echoed");

        [GlassCoderTool("fails", Order = 2)]
        [Description("Always reports a handled failure, for tests.")]
        public ToolObservation<EchoData> Fails() =>
            Observation.Fail<EchoData>("fails", ToolErrorCodes.NotFound, "nothing here");

        [GlassCoderTool("throws", Order = 3)]
        [Description("Throws, which a real tool must never do.")]
        public ToolObservation<EchoData> Throws() => throw new InvalidOperationException("boom");
    }

    private sealed class UndescribedTool : IToolSet
    {
        [GlassCoderTool("undescribed")]
        public ToolObservation<EchoData> Undescribed() => Observation.Ok("undescribed", new EchoData("x"));
    }

    private sealed class UndescribedParameterTool : IToolSet
    {
        [GlassCoderTool("undescribed_parameter")]
        [Description("Has a described method but an undescribed parameter.")]
        public ToolObservation<EchoData> Run(string text) => Observation.Ok("undescribed_parameter", new EchoData(text));
    }

    public sealed record EchoData([property: Description("The echoed text.")] string Value);
}
