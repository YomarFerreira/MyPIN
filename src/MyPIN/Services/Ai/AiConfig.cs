using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyPIN.Services.Ai;

/// <summary>Tipo de conexão: a API do Claude (Anthropic) ou qualquer API "compatível com OpenAI".</summary>
public enum AiProtocol { Anthropic, OpenAiCompatible }

/// <summary>Provedores prontos para escolher na tela de configuração.</summary>
public sealed record AiProviderPreset(
    string Key, string Name, AiProtocol Protocol, string DefaultUrl, bool NeedsKey,
    string[] SuggestedModels, string Help);

public static class AiProviders
{
    public static readonly AiProviderPreset[] All =
    {
        new("anthropic", "Anthropic (Claude)", AiProtocol.Anthropic, "https://api.anthropic.com", true,
            new[] { "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5" },
            "Crie a chave em console.anthropic.com → API Keys. A API é cobrada por uso e é separada da assinatura do Claude (claude.ai)."),
        new("openai", "OpenAI (ChatGPT)", AiProtocol.OpenAiCompatible, "https://api.openai.com/v1", true,
            Array.Empty<string>(),
            "Crie a chave em platform.openai.com/api-keys. A API é cobrada por uso e é separada da assinatura do ChatGPT. Use \"Buscar modelos\" para ver os disponíveis."),
        new("gemini", "Google Gemini", AiProtocol.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", true,
            Array.Empty<string>(),
            "Crie a chave em aistudio.google.com/apikey (tem plano gratuito). Use \"Buscar modelos\" para ver os disponíveis."),
        new("ollama", "Ollama (no seu computador, grátis)", AiProtocol.OpenAiCompatible, "http://localhost:11434/v1", false,
            Array.Empty<string>(),
            "Instale o Ollama (ollama.com) e baixe um modelo, ex.: \"ollama pull <modelo>\". Roda offline, sem chave. Use \"Buscar modelos\" para listar os instalados."),
        new("custom", "Outro (compatível com OpenAI)", AiProtocol.OpenAiCompatible, "", false,
            Array.Empty<string>(),
            "Qualquer serviço com API compatível com OpenAI (OpenRouter, Groq, LM Studio, Azure...). Informe o endereço base (geralmente termina em /v1), a chave e o modelo."),
    };

    public static AiProviderPreset Get(string? key) => All.FirstOrDefault(p => p.Key == key) ?? All[0];
}

public sealed class AiConfig
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "anthropic";
    public string BaseUrl { get; set; } = "https://api.anthropic.com";
    public string Model { get; set; } = "claude-haiku-5-5";

    /// <summary>Chave criptografada com DPAPI (só o seu usuário do Windows consegue ler).</summary>
    public string? ApiKeyProtected { get; set; }

    public AiProviderPreset Preset => AiProviders.Get(Provider);

    public bool IsReady =>
        Enabled && !string.IsNullOrWhiteSpace(Model) && !string.IsNullOrWhiteSpace(BaseUrl) &&
        (!Preset.NeedsKey || !string.IsNullOrEmpty(ApiKeyProtected));

    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(ApiKeyProtected)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ApiKeyProtected), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
            return null;
        }
    }

    public void SetApiKey(string? key)
    {
        ApiKeyProtected = string.IsNullOrWhiteSpace(key)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser));
    }

    // ATENÇÃO: não mudar este texto (nem com a troca de nome do app). Ele faz parte da
    // criptografia da chave já salva; se mudar, a chave guardada deixa de abrir.
    static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MyPostIt.AiKey.v1");

    public AiConfig Clone() => (AiConfig)MemberwiseClone();

    // ---------- arquivo ai.json (separado das notas, fora dos backups) ----------

    static string FilePath => Path.Combine(NoteStore.DataDir, "ai.json");

    public static AiConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(FilePath)) ?? new AiConfig();
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        return new AiConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(NoteStore.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
