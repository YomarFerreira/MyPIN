using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;

namespace MyPIN.Services.Ai;

public sealed record AiMessage(bool FromUser, string Text);

/// <summary>Erro com mensagem amigável para mostrar ao usuário.</summary>
public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

public interface IAiChat
{
    /// <summary>Envia a conversa e devolve a resposta aos pedaços (streaming).</summary>
    IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<AiMessage> messages, CancellationToken ct);

    Task<List<string>> ListModelsAsync(CancellationToken ct);
}

public static class AiChatFactory
{
    public static IAiChat Create(AiConfig cfg) => cfg.Preset.Protocol == AiProtocol.Anthropic
        ? new AnthropicChat(cfg.GetApiKey(), cfg.BaseUrl, cfg.Model)
        : new OpenAiCompatChat(cfg.GetApiKey(), cfg.BaseUrl, cfg.Model);
}

// =========================================================================
// Anthropic (Claude) — SDK oficial
// =========================================================================

public sealed class AnthropicChat : IAiChat
{
    const string DefaultUrl = "https://api.anthropic.com";
    readonly AnthropicClient _client;
    readonly string _model;

    public AnthropicChat(string? apiKey, string baseUrl, string model)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new AiException("Informe a chave da API da Anthropic.");
        _client = new AnthropicClient { ApiKey = apiKey };
        var url = baseUrl.Trim().TrimEnd('/');
        if (url.Length > 0 && !url.Equals(DefaultUrl, StringComparison.OrdinalIgnoreCase))
            _client = new AnthropicClient { ApiKey = apiKey, BaseUrl = url };
        _model = model.Trim();
    }

    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<AiMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var history = new List<MessageParam>();
        foreach (var m in messages)
            history.Add(m.FromUser ? new UserMessageParam { Content = m.Text } : new AssistantMessageParam { Content = m.Text });

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = 16000,
            System = system,
            Messages = history,
        };

        var stream = _client.Messages.CreateStreaming(parameters, ct);
        await using var e = stream.GetAsyncEnumerator(ct);
        while (true)
        {
            RawMessageStreamEvent ev;
            try
            {
                if (!await e.MoveNextAsync()) yield break;
                ev = e.Current;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw Translate(ex); }

            if (ev.TryPickContentBlockDelta(out var delta) && delta.Delta.TryPickText(out var text))
                yield return text.Text;
            else if (ev.TryPickDelta(out var msgDelta) && msgDelta.Delta.StopReason == "refusal")
                yield return "\n\n(O modelo recusou este pedido.)";
        }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        try
        {
            var page = await _client.Models.List(cancellationToken: ct);
            return page.Items.Select(m => m.ID).ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { throw Translate(ex); }
    }

    static AiException Translate(Exception ex) => ex switch
    {
        AiException a => a,
        AnthropicUnauthorizedException => new AiException("Chave da API inválida ou revogada (erro 401). Confira a chave nas configurações da IA.", ex),
        AnthropicForbiddenException => new AiException("Sua conta não tem permissão para isso (erro 403). Veja as configurações da sua organização no console da Anthropic.", ex),
        AnthropicNotFoundException => new AiException("Modelo não encontrado (erro 404). Use \"Buscar modelos\" nas configurações da IA.", ex),
        AnthropicRateLimitException => new AiException("Muitos pedidos em pouco tempo (erro 429). Espere um pouco e tente de novo.", ex),
        Anthropic5xxException => new AiException("O serviço da Anthropic está com problemas ou sobrecarregado. Tente de novo em instantes.", ex),
        AnthropicIOException => new AiException("Sem conexão com a Anthropic. Verifique a internet.", ex),
        AnthropicApiException api => new AiException("A API respondeu com erro: " + api.Message, ex),
        _ => new AiException("Erro ao falar com a IA: " + ex.Message, ex),
    };
}

// =========================================================================
// Compatível com OpenAI (OpenAI, Gemini, Ollama, OpenRouter, Groq, LM Studio...)
// =========================================================================

public sealed class OpenAiCompatChat : IAiChat
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    readonly string? _key;
    readonly string _base;
    readonly string _model;

    public OpenAiCompatChat(string? apiKey, string baseUrl, string model)
    {
        _key = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        _base = baseUrl.Trim().TrimEnd('/');
        _model = model.Trim();
        if (_base.Length == 0) throw new AiException("Informe o endereço (URL) da API.");
    }

    HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, _base + path);
        if (_key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return req;
    }

    public async IAsyncEnumerable<string> StreamAsync(string system, IReadOnlyList<AiMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var msgs = new List<object> { new { role = "system", content = system } };
        msgs.AddRange(messages.Select(m => (object)new { role = m.FromUser ? "user" : "assistant", content = m.Text }));
        var body = JsonSerializer.Serialize(new { model = _model, messages = msgs, stream = true });

        using var req = NewRequest(HttpMethod.Post, "/chat/completions");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new AiException($"Não consegui conectar em {_base}. Verifique o endereço e a internet.", ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await HttpError(resp);

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;

                string? piece = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                        piece = c.GetString();
                }
                catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // linha que não é um pedaço de texto (ex.: metadados) — ignora
                }
                if (!string.IsNullOrEmpty(piece)) yield return piece;
            }
        }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        using var req = NewRequest(HttpMethod.Get, "/models");
        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { throw new AiException($"Não consegui conectar em {_base}. Verifique o endereço e a internet.", ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) throw await HttpError(resp);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("data").EnumerateArray()
                .Select(m => m.GetProperty("id").GetString() ?? "")
                .Where(id => id.Length > 0)
                .Select(id => id.StartsWith("models/") ? id[7..] : id) // Gemini devolve "models/xxx"
                .OrderBy(id => id)
                .ToList();
        }
    }

    static async Task<AiException> HttpError(HttpResponseMessage resp)
    {
        string detail = "";
        try
        {
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement[0] : doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                detail = err.ValueKind == JsonValueKind.String ? err.GetString() ?? ""
                    : err.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch { /* corpo não é JSON */ }

        var friendly = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Chave da API inválida (erro 401).",
            HttpStatusCode.Forbidden => "Acesso negado (erro 403).",
            HttpStatusCode.NotFound => "Endereço ou modelo não encontrado (erro 404).",
            HttpStatusCode.TooManyRequests => "Limite de uso atingido (erro 429). Espere um pouco ou verifique seus créditos.",
            HttpStatusCode.PaymentRequired => "Problema de pagamento/créditos na conta (erro 402).",
            _ when (int)resp.StatusCode >= 500 => $"O serviço está com problemas (erro {(int)resp.StatusCode}). Tente de novo em instantes.",
            _ => $"A API respondeu com erro {(int)resp.StatusCode}.",
        };
        return new AiException(detail.Length > 0 ? $"{friendly}\n{detail}" : friendly);
    }
}
