using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OmniWin.Safety;

namespace OmniWin.Server.Learning;

/// <summary>
/// The built-in decision model adapter (provider "openai" or "openai_compatible"): any service that speaks the OpenAI chat
/// completions API, in the cloud or on this PC. It sends the request and the option list, asks for a JSON confidence per option id
/// and reads the answer leniently. It never sends anything else, and a slow or failing service only means "no answer" upstream.
/// The key comes from the environment variable named in decision.api_key_env when that is set, else from decision.api_key.
/// </summary>
internal sealed class OpenAiDecisionModel : IDecisionModel, IDisposable
{
    private const string SystemPrompt =
        "You match a user's request to one option of a Windows automation tool. Reply with JSON only, no other text, in exactly " +
        "this form: {\"confidences\": {\"<option id>\": <number from 0 to 1>, ...}}. Give every option id one number: how sure " +
        "you are that the option is what the user wants. Use the option \"none\" when nothing fits. Never invent ids.";

    private readonly HttpClient _http;
    private readonly DecisionConfig _settings;
    private readonly Func<string, string?> _environment;

    public OpenAiDecisionModel(DecisionConfig settings, HttpMessageHandler? handler = null, Func<string, string?>? environment = null)
    {
        _settings = settings;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        Url = CompletionsUrl(settings.Endpoint);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        // The decision stage cancels at timeout_ms; this is only a backstop.
        _http.Timeout = TimeSpan.FromMilliseconds(Math.Max(settings.TimeoutMs, 100) + 1000);
    }

    public Uri Url { get; }

    public string Model => _settings.Model?.Trim() ?? string.Empty;

    public string Name => "openai:" + Model;

    /// <summary>True unless the service is on this PC (localhost / 127.0.0.1 / ::1).</summary>
    public bool IsRemote => !IsLocal(Url);

    public static bool IsLocal(Uri url) =>
        url.IsLoopback || url.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    /// <summary>"{base}/chat/completions", unless the endpoint already names it.</summary>
    public static Uri CompletionsUrl(string? endpoint)
    {
        var text = (endpoint ?? string.Empty).Trim().TrimEnd('/');
        if (!text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) text += "/chat/completions";
        return new Uri(text, UriKind.Absolute);
    }

    /// <summary>The key to send: the named environment variable wins; else the pasted key; else none (local services).</summary>
    public string? Key()
    {
        if (_settings.ApiKeyEnv is { Length: > 0 } name && _environment(name.Trim()) is { Length: > 0 } fromEnv) return fromEnv.Trim();
        return string.IsNullOrWhiteSpace(_settings.ApiKey) ? null : _settings.ApiKey.Trim();
    }

    public static string UserPrompt(string request, IReadOnlyList<DecisionOption> options)
    {
        var text = new StringBuilder();
        text.Append("Request: ").Append(request).Append('\n').Append("Options:\n");
        foreach (var option in options)
        {
            text.Append("- ").Append(option.Id).Append(": ").Append(option.Label);
            if (option.Examples.Count > 0) text.Append(" (for example: ").AppendJoin("; ", option.Examples.Take(3)).Append(')');
            text.Append('\n');
        }

        return text.ToString();
    }

    public async Task<DecisionAnswer?> RankAsync(string request, IReadOnlyList<DecisionOption> options, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0,
            ["max_tokens"] = Math.Clamp(40 + (options.Count * 20), 64, 2000),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = UserPrompt(request, options) }),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, Url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (Key() is { } key) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // The status only: the body may echo the request or the key.
            throw new HttpRequestException("The decision model answered " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture), null, response.StatusCode);
        }

        var reply = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return Parse(reply, options);
    }

    /// <summary>Reads a chat completions reply: the first choice's text, then the confidences in it. Null when nothing usable.</summary>
    public static DecisionAnswer? Parse(string reply, IReadOnlyList<DecisionOption> options)
    {
        string? content;
        try
        {
            using var doc = JsonDocument.Parse(reply);
            content = doc.RootElement.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }

        return content is null ? null : ParseConfidences(content, options);
    }

    /// <summary>
    /// Finds the confidences in the model's text. Accepts {"confidences": {id: n}}, {id: n}, {"confidences": [{"id", "confidence"}]}
    /// or a bare array, with or without ``` fences or text around it; numbers may be strings or percentages. Unknown ids are dropped.
    /// </summary>
    public static DecisionAnswer? ParseConfidences(string content, IReadOnlyList<DecisionOption> options)
    {
        var known = options.Select(o => o.Id).ToHashSet(StringComparer.Ordinal);
        var json = ExtractJson(content);
        if (json is null) return null;
        var picks = new Dictionary<string, double>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "confidences", "confidence", "scores", "options", "ranked" })
                {
                    if (root.TryGetProperty(name, out var inner) && inner.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        root = inner;
                        break;
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (Number(property.Value) is { } n) Add(picks, known, property.Name, n);
                }
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var id = Text(item, "id") ?? Text(item, "option") ?? Text(item, "name");
                    var n = Number(item, "confidence") ?? Number(item, "score") ?? Number(item, "probability");
                    if (id is not null && n is { } value) Add(picks, known, id, value);
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return picks.Count == 0 ? null
            : new DecisionAnswer([.. picks.OrderByDescending(p => p.Value).Select(p => new DecisionPick(p.Key, p.Value))]);
    }

    private static void Add(Dictionary<string, double> picks, HashSet<string> known, string id, double value)
    {
        id = id.Trim();
        if (!known.Contains(id)) return;
        if (value > 1 && value <= 100) value /= 100;   // a percentage
        if (value is < 0 or > 1 || double.IsNaN(value)) return;
        picks[id] = value;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement item, string name) => item.TryGetProperty(name, out var v) ? Number(v) : null;

    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString()?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) =>
            value.GetString()!.Trim().EndsWith('%') ? n / 100 : n,
        _ => null,
    };

    /// <summary>The outermost JSON object or array in the text (``` fences and words around it are ignored).</summary>
    public static string? ExtractJson(string content)
    {
        var start = content.IndexOfAny(['{', '[']);
        if (start < 0) return null;
        var close = content[start] == '{' ? '}' : ']';
        var end = content.LastIndexOf(close);
        return end > start ? content[start..(end + 1)] : null;
    }

    public void Dispose() => _http.Dispose();
}