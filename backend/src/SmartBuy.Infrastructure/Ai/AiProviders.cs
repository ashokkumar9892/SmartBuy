using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SmartBuy.Domain.Entities;
using SmartBuy.Infrastructure.Persistence;

namespace SmartBuy.Infrastructure.Ai;

public sealed class AiOptions
{
    /// <summary>"mock", "claude" or "openai". Falls back to mock when the chosen provider has no API key.</summary>
    public string Provider { get; set; } = "mock";
    public ClaudeOptions Claude { get; set; } = new();
    public OpenAiOptions OpenAi { get; set; } = new();
}

public sealed class ClaudeOptions
{
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "claude-opus-5-5";
    // Used only to estimate spend in the admin panel. Update when your rates change.
    public decimal InputCostPerMillionTokens { get; set; } = 4.00m;
    public decimal OutputCostPerMillionTokens { get; set; } = 20.00m;
}

public sealed class OpenAiOptions
{
    public string? ApiKey { get; set; }
    /// <summary>Required when the OpenAI provider is selected; there is deliberately no default model.</summary>
    public string? Model { get; set; }
    public string BaseUrl { get; set; } = "https://api.openai.com/v1/";
    public decimal InputCostPerMillionTokens { get; set; }
    public decimal OutputCostPerMillionTokens { get; set; }
}

/// <param name="Feature">Logical feature name, recorded in the usage log.</param>
/// <param name="System">Trusted instructions written by this application.</param>
/// <param name="User">Untrusted content (user text, uploaded documents, supplier descriptions) wrapped as data.</param>
/// <param name="JsonSchema">When set, the provider must return JSON conforming to this schema.</param>
/// <param name="DeterministicFallback">Text the mock provider returns verbatim for non-extraction features.</param>
public sealed record AiRequest(string Feature, string System, string User, JsonElement? JsonSchema = null, int MaxTokens = 16000, string? DeterministicFallback = null);

public sealed record AiResponse(string Text, int InputTokens, int OutputTokens, string Provider, string Model);

public class AiProviderException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Provider-independent text-generation contract. Implementations never see credentials for business actions.</summary>
public interface IAiProvider
{
    string Name { get; }
    string Model { get; }
    bool IsConfigured { get; }
    decimal EstimateCostUsd(int inputTokens, int outputTokens);
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken);
}

public sealed class ClaudeAiProvider(IOptions<AiOptions> options) : IAiProvider
{
    private readonly ClaudeOptions _options = options.Value.Claude;

    public string Name => "claude";
    public string Model => _options.Model;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public decimal EstimateCostUsd(int inputTokens, int outputTokens) =>
        inputTokens / 1_000_000m * _options.InputCostPerMillionTokens + outputTokens / 1_000_000m * _options.OutputCostPerMillionTokens;

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new AiProviderException("The Claude provider is not configured.");
        AnthropicClient client = new() { ApiKey = _options.ApiKey };

        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = request.MaxTokens,
            System = request.System,
            Messages = [new() { Role = Role.User, Content = request.User }],
            OutputConfig = request.JsonSchema is { } schema
                ? new OutputConfig { Format = new JsonOutputFormat { Schema = schema.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) } }
                : null,
        };

        Message response;
        try
        {
            response = await client.Messages.Create(parameters, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AiProviderException($"Claude request failed: {ex.Message}", ex);
        }

        if (response.StopReason == "refusal")
            throw new AiProviderException("The AI provider declined this request.");
        if (response.StopReason == "max_tokens")
            throw new AiProviderException("The AI response was cut off before it finished. Try a shorter request.");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return new AiResponse(text, (int)response.Usage.InputTokens, (int)response.Usage.OutputTokens, Name, _options.Model);
    }
}

public sealed class OpenAiProvider(IOptions<AiOptions> options, IHttpClientFactory httpClientFactory) : IAiProvider
{
    private readonly OpenAiOptions _options = options.Value.OpenAi;

    public string Name => "openai";
    public string Model => _options.Model ?? "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey) && !string.IsNullOrWhiteSpace(_options.Model);

    public decimal EstimateCostUsd(int inputTokens, int outputTokens) =>
        inputTokens / 1_000_000m * _options.InputCostPerMillionTokens + outputTokens / 1_000_000m * _options.OutputCostPerMillionTokens;

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured) throw new AiProviderException("The OpenAI provider is not configured (API key and model are required).");
        var http = httpClientFactory.CreateClient("openai");
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), "chat/completions"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["max_completion_tokens"] = request.MaxTokens,
            ["messages"] = new object[]
            {
                new { role = "system", content = request.System },
                new { role = "user", content = request.User },
            },
        };
        if (request.JsonSchema is { } schema)
            body["response_format"] = new { type = "json_schema", json_schema = new { name = "result", strict = true, schema } };
        message.Content = JsonContent.Create(body);

        try
        {
            using var response = await http.SendAsync(message, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new AiProviderException($"OpenAI request failed with HTTP {(int)response.StatusCode}.");
            using var doc = JsonDocument.Parse(payload);
            var choice = doc.RootElement.GetProperty("choices")[0];
            var text = choice.GetProperty("message").GetProperty("content").GetString() ?? "";
            var usage = doc.RootElement.TryGetProperty("usage", out var u) ? u : default;
            var input = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            var output = usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty("completion_tokens", out var ctok) ? ctok.GetInt32() : 0;
            return new AiResponse(text, input, output, Name, _options.Model!);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AiProviderException($"OpenAI request failed: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Rule-based stand-in used for local development and demos. It is not a language model: it parses quantities with
/// regular expressions and returns pre-computed explanations, and the UI labels its output accordingly.
/// </summary>
public sealed class MockAiProvider : IAiProvider
{
    public string Name => "mock";
    public string Model => "rule-based-demo";
    public bool IsConfigured => true;
    public decimal EstimateCostUsd(int inputTokens, int outputTokens) => 0m;

    public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var text = request.Feature == AiFeatures.MaterialList
            ? JsonSerializer.Serialize(HeuristicListParser.ParseRequest(MaterialListPrompts.ExtractRequestText(request.User)), AiJson.Options)
            : request.DeterministicFallback ?? "";
        // Rough token estimate so the usage screens show plausible volumes.
        return Task.FromResult(new AiResponse(text, (request.System.Length + request.User.Length) / 4, text.Length / 4, Name, Model));
    }
}

public static class AiFeatures
{
    public const string MaterialList = "material-list";
    public const string PlanExplanation = "plan-explanation";
    public const string AlternativeExplanation = "alternative-explanation";
}

public static class AiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Selects the configured provider, records usage and cost, and normalizes failures.</summary>
public sealed class AiGateway(IEnumerable<IAiProvider> providers, IOptions<AiOptions> options, AppDbContext db, ITenantContext tenant, ILogger<AiGateway> logger)
{
    public IAiProvider ActiveProvider
    {
        get
        {
            var wanted = providers.FirstOrDefault(p => string.Equals(p.Name, options.Value.Provider, StringComparison.OrdinalIgnoreCase));
            return wanted is { IsConfigured: true } ? wanted : providers.First(p => p.Name == "mock");
        }
    }

    public bool IsMock => ActiveProvider.Name == "mock";

    public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken)
    {
        var provider = ActiveProvider;
        var stopwatch = Stopwatch.StartNew();
        var log = new AiUsageLog
        {
            OrganizationId = tenant.OrganizationId,
            UserId = tenant.UserId,
            Provider = provider.Name,
            Model = provider.Model,
            Feature = request.Feature,
        };
        try
        {
            var response = await provider.CompleteAsync(request, cancellationToken);
            log.Success = true;
            log.InputTokens = response.InputTokens;
            log.OutputTokens = response.OutputTokens;
            log.EstimatedCostUsd = provider.EstimateCostUsd(response.InputTokens, response.OutputTokens);
            return response;
        }
        catch (AiProviderException ex)
        {
            log.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
            logger.LogWarning(ex, "AI provider {Provider} failed for feature {Feature}", provider.Name, request.Feature);
            throw;
        }
        finally
        {
            log.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            db.AiUsageLogs.Add(log);
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
