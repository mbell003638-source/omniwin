namespace OmniWin.Safety.Tests;

/// <summary>--validate-config rules for the OpenAI-compatible decision model fields.</summary>
public sealed class DecisionConfigValidationTests
{
    private static IReadOnlyList<string> Errors(DecisionConfig decision) => new OmniWinConfig { Decision = decision }.Validate();

    [Fact]
    public void A_complete_openai_setup_is_accepted_with_either_key_source()
    {
        Assert.Empty(Errors(new DecisionConfig { Provider = "openai", Mode = "shadow", Endpoint = "https://api.example.com/v1", Model = "m", ApiKey = "k" }));
        Assert.Empty(Errors(new DecisionConfig { Provider = "openai_compatible", Mode = "act", Endpoint = "http://localhost:11434/v1", Model = "m", ApiKeyEnv = "JEV_KEY" }));
        Assert.Empty(Errors(new DecisionConfig { Provider = "OpenAI", Endpoint = "http://127.0.0.1:8080", Model = "m" }));
    }

    [Theory]
    [InlineData(null, "m", "decision.endpoint")]
    [InlineData("api.example.com/v1", "m", "decision.endpoint")]
    [InlineData("ftp://api.example.com", "m", "decision.endpoint")]
    [InlineData("https://api.example.com/v1", " ", "decision.model")]
    [InlineData("https://api.example.com/v1", null, "decision.model")]
    public void Openai_needs_a_web_base_url_and_a_model(string? endpoint, string? model, string expected)
    {
        var errors = Errors(new DecisionConfig { Provider = "openai", Mode = "shadow", Endpoint = endpoint, Model = model });
        Assert.Contains(errors, e => e.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Key_fields_must_be_sane_and_none_ignores_the_new_fields()
    {
        Assert.Contains(Errors(new DecisionConfig { ApiKeyEnv = "MY KEY" }), e => e.StartsWith("decision.api_key_env", StringComparison.Ordinal));
        Assert.Contains(Errors(new DecisionConfig { ApiKey = "a\nb" }), e => e.StartsWith("decision.api_key ", StringComparison.Ordinal));
        Assert.Empty(Errors(new DecisionConfig { Provider = "none", Model = "left over", Endpoint = "not a url" }));
    }

    [Fact]
    public void The_new_keys_load_from_snake_case_json()
    {
        var dir = Path.Combine(Path.GetTempPath(), "omniwin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "config.json");
            File.WriteAllText(file, "{\"decision\":{\"provider\":\"openai\",\"mode\":\"shadow\",\"endpoint\":\"https://api.example.com/v1\",\"model\":\"jev-1\",\"api_key\":\"k\",\"api_key_env\":\"JEV_KEY\"}}");
            Assert.Empty(ConfigStore.Check(file));   // strict loader: an unknown key would be an error here
            var config = System.Text.Json.JsonSerializer.Deserialize<OmniWinConfig>(File.ReadAllText(file), OmniWin.Core.CoreJson.Indented)!;
            Assert.Equal("jev-1", config.Decision.Model);
            Assert.Equal("k", config.Decision.ApiKey);
            Assert.Equal("JEV_KEY", config.Decision.ApiKeyEnv);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}