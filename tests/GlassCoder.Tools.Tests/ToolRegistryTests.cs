using System.ComponentModel;
using System.Text.Json;
using GlassCoder.TestSupport;
using GlassCoder.Tools.FileSystem;
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

    /// <summary>
    /// A wrong name is nearly always a near-miss on a right one - run d18c0e57 called `run`
    /// meaning `run_tests` and spent a step learning only that it does not exist. The refusal
    /// should hand back the intended call.
    /// </summary>
    [Fact]
    public async Task An_unknown_tool_that_nearly_names_a_real_one_gets_that_name_suggested()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent("c5", "ech", null));

        invocation.Status.ShouldBe(ToolCallStatus.UnknownTool);
        invocation.ErrorMessage.ShouldContain("Did you mean 'echo'?");
    }

    /// <summary>
    /// Shell-shaped names are the exception: run 008007e1 sent `run` meaning `rm -rf`, run
    /// 216360bf sent it meaning `copy`, and "did you mean run_tests?" answers neither. What
    /// the model wants is a shell, and the honest answer names the real paths instead.
    /// </summary>
    [Fact]
    public async Task A_shell_shaped_name_gets_the_no_shell_answer()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent(
            "c10", "run", new Dictionary<string, object?> { ["command"] = "copy a.xaml b.xaml" }));

        invocation.Status.ShouldBe(ToolCallStatus.UnknownTool);
        invocation.ErrorMessage.ShouldContain("There is no shell");
        invocation.ErrorMessage.ShouldContain("create_file");

        // It used to end by telling the model the application was the operator's to start, which
        // stopped being true when task 71 shipped launch_app. The answer to "there is no shell"
        // is the list of tools that do the jobs a shell would - all of them.
        invocation.ErrorMessage.ShouldContain("launch_app");
        invocation.ErrorMessage.ShouldNotContain("Did you mean");
    }

    /// <summary>
    /// Run f4ed50e0 called <c>todo_write</c> twice - with byte-identical <c>update_todos</c>
    /// arguments - and the "did you mean" hint converted the first miss but not the second. A
    /// suggestion the model ignores twice is not a mechanism: a name whose intent is
    /// unambiguous is rewritten and invoked, not bounced.
    /// </summary>
    [Fact]
    public async Task A_known_alias_is_rewritten_and_invoked()
    {
        ToolRegistry registry = new([new TodoTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(
            new FunctionCallContent("c6", "todo_write", new Dictionary<string, object?> { ["items"] = "[]" }));

        invocation.Status.ShouldBe(ToolCallStatus.Succeeded);
        invocation.IsValid.ShouldBeTrue();
        invocation.ToolName.ShouldBe("update_todos", "failure and repeat keys must align on the canonical name");
    }

    [Fact]
    public async Task An_alias_whose_canonical_tool_is_absent_still_fails_as_unknown()
    {
        // WellFormedTools registers no update_todos, so the alias has nothing to point at and
        // the ordinary unknown-tool answer stands.
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent("c7", "todo_write", null));

        invocation.Status.ShouldBe(ToolCallStatus.UnknownTool);
    }

    // ── Arguments checked before they bind (run c5eb67f6) ──
    //
    // The binder silently drops unknown keys: read_file(offset: 70) - another harness's name
    // for startLine - returned the head of the file thirteen times, every answer Succeeded,
    // while the model paged a file whose pager ignored the page number.

    [Fact]
    public async Task An_unknown_argument_is_refused_with_the_real_parameter_list()
    {
        ToolRegistry registry = new([new WellFormedTools()]);

        ToolInvocation invocation = await registry.InvokeAsync(
            new FunctionCallContent("c8", "echo", new Dictionary<string, object?> { ["txt"] = "hello" }));

        invocation.Status.ShouldBe(ToolCallStatus.InvalidArguments);
        invocation.ErrorMessage.ShouldContain("no parameter named 'txt'");
        invocation.Result.ShouldBeOfType<ToolObservation<object>>()
            .Error!.Hint.ShouldContain("text");
    }

    [Fact]
    public async Task A_known_argument_alias_is_rewritten_and_honoured()
    {
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "src/Big.cs", string.Join('\n', Enumerable.Range(1, 100).Select(i => $"// line {i}")));
        ToolRegistry registry = new([new ReadFileTool(workspace.Guard(), TempWorkspace.Wrap(new ToolsOptions()))]);

        ToolInvocation invocation = await registry.InvokeAsync(new FunctionCallContent(
            "c9", "read_file",
            new Dictionary<string, object?> { ["path"] = "src/Big.cs", ["offset"] = 70, ["maxLines"] = 5 }));

        invocation.Status.ShouldBe(ToolCallStatus.Succeeded);
        invocation.Summary.ShouldContain("lines 70-74", customMessage: "the alias must page, not return the head");
    }

    [Fact]
    public async Task Integer_arguments_accept_the_shapes_models_send()
    {
        // Step 18 of the run hard-failed on "70.0" as a string. Whole numbers bind however
        // they arrive; fractions are a confusion and are refused with the reason.
        using TempWorkspace workspace = new();
        workspace.WriteFile(
            "src/Big.cs", string.Join('\n', Enumerable.Range(1, 100).Select(i => $"// line {i}")));
        ToolRegistry registry = new([new ReadFileTool(workspace.Guard(), TempWorkspace.Wrap(new ToolsOptions()))]);

        ToolInvocation whole = await registry.InvokeAsync(new FunctionCallContent(
            "ca", "read_file",
            new Dictionary<string, object?> { ["path"] = "src/Big.cs", ["startLine"] = "70.0", ["maxLines"] = "5" }));
        ToolInvocation fractional = await registry.InvokeAsync(new FunctionCallContent(
            "cb", "read_file",
            new Dictionary<string, object?> { ["path"] = "src/Big.cs", ["startLine"] = "70.5" }));

        whole.Status.ShouldBe(ToolCallStatus.Succeeded);
        whole.Summary.ShouldContain("lines 70-74");
        fractional.Status.ShouldBe(ToolCallStatus.InvalidArguments);
        fractional.ErrorMessage.ShouldContain("whole number");
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

    private sealed class TodoTools : IToolSet
    {
        [GlassCoderTool("update_todos")]
        [Description("Replaces the plan, for tests.")]
        public ToolObservation<EchoData> UpdateTodos([Description("The plan items.")] string items) =>
            Observation.Ok("update_todos", new EchoData(items), "Plan updated.");
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
