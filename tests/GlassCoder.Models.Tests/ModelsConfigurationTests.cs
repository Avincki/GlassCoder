using GlassCoder.Models.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace GlassCoder.Models.Tests;

/// <summary>
/// Model configuration (workplan tasks 3-4): roles bind from configuration, and the one
/// mistake that silently pins an ablation to a machine - a checkpoint path where an alias
/// belongs - is rejected at startup.
/// </summary>
public sealed class ModelsConfigurationTests
{
    [Fact]
    public void Roles_bind_from_configuration()
    {
        ModelsOptions options = Bind(new Dictionary<string, string?>
        {
            ["GlassCoder:Models:DefaultRole"] = "worker",
            ["GlassCoder:Models:Roles:worker:Endpoint"] = "http://localhost:8001/v1",
            ["GlassCoder:Models:Roles:worker:ModelAlias"] = "worker",
            ["GlassCoder:Models:Roles:critic:Endpoint"] = "http://localhost:8003/v1",
            ["GlassCoder:Models:Roles:critic:ModelAlias"] = "critic",
            ["GlassCoder:Models:Roles:critic:ConstrainedDecoding:GuidedDecodingBackend"] = "xgrammar",
        });

        options.Roles.Count.ShouldBe(2);
        options.Roles["worker"].Endpoint.ShouldBe("http://localhost:8001/v1");
        options.Roles["critic"].ConstrainedDecoding.GuidedDecodingBackend.ShouldBe("xgrammar");
        Validate(options).Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("/models/qwen3-coder-30b/checkpoint-1200")]
    [InlineData(@"C:\models\qwen3\snapshot")]
    [InlineData("models/worker.gguf")]
    public void A_checkpoint_path_where_an_alias_belongs_is_rejected(string alias)
    {
        ModelsOptions options = new();
        options.Roles["worker"] = new ModelRoleOptions { Endpoint = "http://localhost:8001/v1", ModelAlias = alias };

        ValidateOptionsResult result = Validate(options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("checkpoint path");
    }

    [Fact]
    public void A_non_absolute_endpoint_is_rejected()
    {
        ModelsOptions options = new();
        options.Roles["worker"] = new ModelRoleOptions { Endpoint = "localhost:8001", ModelAlias = "worker" };

        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void A_default_role_that_is_not_configured_is_rejected()
    {
        ModelsOptions options = new() { DefaultRole = "drafter" };
        options.Roles["worker"] = new ModelRoleOptions { Endpoint = "http://localhost:8001/v1", ModelAlias = "worker" };

        Validate(options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void An_empty_role_set_is_rejected()
    {
        Validate(new ModelsOptions()).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_api_key_comes_from_the_environment_in_preference_to_the_file()
    {
        const string variable = "GLASSCODER_TEST_KEY";
        Environment.SetEnvironmentVariable(variable, "from-environment");
        try
        {
            ModelRoleOptions role = new()
            {
                Endpoint = "http://localhost:8001/v1",
                ModelAlias = "worker",
                ApiKey = "from-file",
                ApiKeyEnvironmentVariable = variable,
            };

            role.ResolveApiKey().ShouldBe("from-environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static ModelsOptions Bind(Dictionary<string, string?> values)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        ModelsOptions options = new();
        configuration.GetSection(ModelsOptions.SectionName).Bind(options);
        return options;
    }

    private static ValidateOptionsResult Validate(ModelsOptions options) =>
        new ModelsOptionsValidator().Validate(Options.DefaultName, options);
}
