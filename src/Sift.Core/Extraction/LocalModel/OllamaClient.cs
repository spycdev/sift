using System.Text;
using System.Text.Json.Nodes;

namespace Sift.Core.Extraction.LocalModel;

/// <summary>The local model couldn't be used: not running, not pulled, timed out, or returned unusable output.</summary>
public sealed class LocalModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A language model running on this machine that can answer with JSON matching a schema.</summary>
public interface ILocalModelClient
{
    string ModelName { get; }

    /// <summary>True when the model service is running and the model is available.</summary>
    Task<bool> IsReadyAsync(CancellationToken ct = default);

    /// <returns>JSON text that conforms to <paramref name="schema"/>.</returns>
    /// <exception cref="LocalModelException"/>
    Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, JsonObject schema, CancellationToken ct = default);
}

public sealed record OllamaOptions
{
    public const string DefaultModel = "qwen3:4b-instruct-2507-q4_K_M";

    public Uri Endpoint { get; init; } = new("http://127.0.0.1:11434");
    public string Model { get; init; } = DefaultModel;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);
    public int ContextLength { get; init; } = 8192;
}

/// <summary>
/// Talks to a local Ollama server. Output is constrained to a JSON schema by Ollama's structured
/// outputs, so malformed JSON is impossible. Only loopback endpoints are accepted, so receipt text
/// can't be sent off the machine by misconfiguration.
/// </summary>
public sealed class OllamaClient : ILocalModelClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly OllamaOptions _options;

    public OllamaClient(OllamaOptions options, HttpMessageHandler? handler = null)
    {
        if (!options.Endpoint.IsLoopback)
            throw new ArgumentException("The model endpoint must be on this machine (a loopback address); receipt text never leaves the device.", nameof(options));

        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = options.Endpoint;
        _http.Timeout = options.Timeout;
    }

    public string ModelName => _options.Model;

    public async Task<bool> IsReadyAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;
            var models = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["models"]?.AsArray();
            return models?.Any(m => m?["name"]?.GetValue<string>() is { } name
                && (name == _options.Model || name == _options.Model + ":latest")) ?? false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    public async Task<string> CompleteJsonAsync(string systemPrompt, string userPrompt, JsonObject schema, CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["model"] = _options.Model,
            ["stream"] = false,
            ["format"] = schema.DeepClone(),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userPrompt }),
            // Deterministic: the same email always gives the same answer.
            ["options"] = new JsonObject { ["temperature"] = 0, ["seed"] = 7, ["num_ctx"] = _options.ContextLength },
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync("/api/chat", new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LocalModelException($"The local model service isn't running at {_options.Endpoint}. Start Ollama and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LocalModelException($"The local model didn't respond within {_options.Timeout.TotalSeconds:0} seconds.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new LocalModelException(
                    $"The local model service returned an error ({(int)response.StatusCode}). Is the model pulled? Run: ollama pull {_options.Model}");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(body)?["message"]?["content"]?.GetValue<string>() is { Length: > 0 } content
                ? content
                : throw new LocalModelException("The local model returned an empty response.");
        }
    }

    public void Dispose() => _http.Dispose();
}
