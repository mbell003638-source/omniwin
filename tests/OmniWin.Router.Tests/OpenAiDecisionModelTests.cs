using System.Net;
using System.Text;
using System.Text.Json;
using OmniWin.Safety;
using OmniWin.Server.Learning;

namespace OmniWin.Router.Tests;

/// <summary>A fake HTTP service: records each request and answers with a scripted reply. Nothing leaves this PC.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

    public TimeSpan Delay { get; init; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Seen.Add((request, body));
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
        return answer(request, body);
    }

    public static HttpResponseMessage Chat(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { role = "assistant", content } } } }), Encoding.UTF8, "application/json"),
    };
}

/// <summary>The built-in OpenAI-compatible decision adapter, against a fake service.</summary>
public sealed class OpenAiDecisionModelTests
{
    private static readonly IReadOnlyList<DecisionOption> Options =
    [
        new("recipe:open-report", "open the weekly report", ["open my report"]),
        new("intent:volume", "set the volume", []),
        new(DecisionService.NoneId, "nothing fits", []),
    ];

    private static DecisionConfig Settings(string endpoint = "https://api.example.com/v1", string? key = null, string? keyEnv = null) => new()
    {
        Provider = "openai", Mode = "shadow", Endpoint = endpoint, Model = "picker-1", ApiKey = key, ApiKeyEnv = keyEnv,
    };

    [Fact]
    public async Task It_posts_the_request_and_options_to_chat_completions_and_reads_the_confidences()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Chat("{\"confidences\": {\"recipe:open-report\": 0.97, \"intent:volume\": 0.02, \"none\": 0.01}}"));
        using var model = new OpenAiDecisionModel(Settings(key: "pasted-key"), handler, _ => null);

        var answer = await model.RankAsync("open my weekly report", Options, TestContext.Current.CancellationToken);

        Assert.NotNull(answer);
        Assert.Equal("recipe:open-report", answer.Ranked[0].Id);
        Assert.Equal(0.97, answer.Ranked[0].Confidence, 3);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.example.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("pasted-key", request.Headers.Authorization.Parameter);
        using var sent = JsonDocument.Parse(body);
        Assert.Equal("picker-1", sent.RootElement.GetProperty("model").GetString());
        var user = sent.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        Assert.Contains("open my weekly report", user, StringComparison.Ordinal);
        Assert.Contains("recipe:open-report", user, StringComparison.Ordinal);
        Assert.Contains("intent:volume", user, StringComparison.Ordinal);
    }

    [Fact]
    public void The_environment_variable_wins_over_the_pasted_key_and_no_key_sends_no_header()
    {
        using var both = new OpenAiDecisionModel(Settings(key: "pasted", keyEnv: "JEV_KEY"), new FakeHandler((_, _) => FakeHandler.Chat("{}")), n => n == "JEV_KEY" ? "from-env" : null);
        Assert.Equal("from-env", both.Key());
        using var emptyEnv = new OpenAiDecisionModel(Settings(key: "pasted", keyEnv: "JEV_KEY"), new FakeHandler((_, _) => FakeHandler.Chat("{}")), _ => "");
        Assert.Equal("pasted", emptyEnv.Key());
        using var none = new OpenAiDecisionModel(Settings(), new FakeHandler((_, _) => FakeHandler.Chat("{}")), _ => null);
        Assert.Null(none.Key());
    }

    [Fact]
    public async Task No_key_means_no_authorization_header()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Chat("{\"none\": 1}"));
        using var model = new OpenAiDecisionModel(Settings("http://localhost:11434/v1/"), handler, _ => null);
        await model.RankAsync("hello", Options, TestContext.Current.CancellationToken);
        Assert.Null(Assert.Single(handler.Seen).Request.Headers.Authorization);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Seen[0].Request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData("http://localhost:1234/v1", false)]
    [InlineData("http://127.0.0.1:8080/v1", false)]
    [InlineData("https://api.example.com/v1", true)]
    [InlineData("http://192.168.1.20:8000/v1", true)]
    public void Only_this_pc_counts_as_local(string endpoint, bool remote)
    {
        using var model = new OpenAiDecisionModel(Settings(endpoint), new FakeHandler((_, _) => FakeHandler.Chat("{}")), _ => null);
        Assert.Equal(remote, model.IsRemote);
    }

    [Fact]
    public void An_endpoint_that_already_names_chat_completions_is_used_as_is()
    {
        Assert.Equal("https://x.test/v1/chat/completions", OpenAiDecisionModel.CompletionsUrl("https://x.test/v1/chat/completions/").ToString());
    }

    [Theory]
    [InlineData("```json\n{\"confidences\": {\"intent:volume\": 0.9}}\n```")]
    [InlineData("Sure! Here you go: {\"intent:volume\": \"0.9\"} Hope that helps.")]
    [InlineData("{\"confidences\": [{\"id\": \"intent:volume\", \"confidence\": 0.9}]}")]
    [InlineData("[{\"option\": \"intent:volume\", \"score\": 90}]")]
    [InlineData("{\"scores\": {\"intent:volume\": \"90%\", \"made-up\": 0.99}}")]
    public void Answers_in_many_shapes_are_read_and_unknown_ids_dropped(string content)
    {
        var answer = OpenAiDecisionModel.ParseConfidences(content, Options);
        Assert.NotNull(answer);
        var pick = Assert.Single(answer.Ranked);
        Assert.Equal("intent:volume", pick.Id);
        Assert.Equal(0.9, pick.Confidence, 3);
    }

    [Theory]
    [InlineData("I am not sure.")]
    [InlineData("{\"made-up\": 0.9}")]
    [InlineData("{\"intent:volume\": 7.5e5}")]
    [InlineData("{not json")]
    public void Unusable_answers_give_no_answer(string content)
    {
        Assert.Null(OpenAiDecisionModel.ParseConfidences(content, Options));
    }

    [Fact]
    public void A_reply_without_choices_gives_no_answer()
    {
        Assert.Null(OpenAiDecisionModel.Parse("{\"error\": {\"message\": \"bad\"}}", Options));
        Assert.Null(OpenAiDecisionModel.Parse("<html>", Options));
    }

    [Fact]
    public async Task A_failing_service_throws_with_the_status_only()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("secret-echo pasted-key") });
        using var model = new OpenAiDecisionModel(Settings(key: "pasted-key"), handler, _ => null);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => model.RankAsync("hi", Options, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("pasted-key", error.Message, StringComparison.Ordinal);
        Assert.Contains("401", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_decision_stage_gives_up_on_a_slow_service_at_timeout_ms()
    {
        var handler = new FakeHandler((_, _) => FakeHandler.Chat("{\"none\": 1}")) { Delay = TimeSpan.FromSeconds(10) };
        using var model = new OpenAiDecisionModel(Settings() with { TimeoutMs = 200 }, handler, _ => null);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.RankAsync("hi", Options, limit.Token));
        Assert.True(watch.ElapsedMilliseconds < 5000);
    }
}