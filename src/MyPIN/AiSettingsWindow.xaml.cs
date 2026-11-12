using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MyPIN.Services;
using MyPIN.Services.Ai;

namespace MyPIN;

public partial class AiSettingsWindow : Window
{
    readonly AiConfig _cfg;
    bool _loading = true;

    public AiSettingsWindow()
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        _cfg = App.Current.Ai.Clone();

        ProviderBox.ItemsSource = AiProviders.All;
        ProviderBox.SelectedItem = _cfg.Preset;
        EnabledBox.IsChecked = _cfg.Enabled || App.Current.Ai.ApiKeyProtected == null; // 1ª vez: já vem marcado
        UrlBox.Text = _cfg.BaseUrl;
        ModelBox.ItemsSource = _cfg.Preset.SuggestedModels;
        ModelBox.Text = _cfg.Model;
        KeyBox.PasswordChanged += (_, _) => UpdateKeyHint();
        UpdatePresetUi();
        UpdateKeyHint();
        _loading = false;
    }

    AiProviderPreset Preset => (AiProviderPreset)ProviderBox.SelectedItem;

    void ProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // Trocou de serviço: preenche endereço e modelo sugeridos e pede a chave de novo
        UrlBox.Text = Preset.DefaultUrl;
        ModelBox.ItemsSource = Preset.SuggestedModels;
        ModelBox.Text = Preset.SuggestedModels.FirstOrDefault() ?? "";
        KeyBox.Clear();
        UpdatePresetUi();
        UpdateKeyHint();
        HideStatus();
    }

    void UpdatePresetUi()
    {
        HelpText.Text = Preset.Help;
        KeyLabel.Text = Preset.NeedsKey ? "Chave da API" : "Chave da API (opcional)";
    }

    /// <summary>A chave salva continua valendo se o campo ficar vazio e o serviço não mudou.</summary>
    bool KeepsSavedKey => KeyBox.Password.Length == 0 && Preset.Key == App.Current.Ai.Provider && App.Current.Ai.ApiKeyProtected != null;

    void UpdateKeyHint() => KeyHint.Visibility = KeepsSavedKey ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Monta a configuração a partir do que está na tela.</summary>
    AiConfig Collect()
    {
        var cfg = App.Current.Ai.Clone();
        cfg.Enabled = EnabledBox.IsChecked == true;
        cfg.Provider = Preset.Key;
        cfg.BaseUrl = UrlBox.Text.Trim();
        cfg.Model = ModelBox.Text.Trim();
        if (!KeepsSavedKey) cfg.SetApiKey(KeyBox.Password);
        return cfg;
    }

    async void ListModels_Click(object sender, RoutedEventArgs e)
    {
        ShowStatus("Buscando modelos...", busy: true);
        try
        {
            var cfg = Collect();
            var models = await AiChatFactory.Create(cfg).ListModelsAsync(CancellationToken.None);
            var current = ModelBox.Text;
            ModelBox.ItemsSource = models;
            ModelBox.Text = models.Contains(current) || models.Count == 0 ? current : models[0];
            ModelBox.IsDropDownOpen = models.Count > 0;
            ShowStatus(models.Count == 0 ? "Nenhum modelo encontrado." : $"{models.Count} modelo(s) disponíveis. Escolha na lista.", ok: models.Count > 0);
        }
        catch (Exception ex)
        {
            ShowStatus(ex.Message, ok: false);
        }
    }

    async void Test_Click(object sender, RoutedEventArgs e)
    {
        var cfg = Collect();
        if (string.IsNullOrWhiteSpace(cfg.Model)) { ShowStatus("Escolha um modelo.", ok: false); return; }
        ShowStatus("Testando...", busy: true);
        try
        {
            var reply = new System.Text.StringBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await foreach (var piece in AiChatFactory.Create(cfg).StreamAsync(
                               "Responda em português, em uma frase curta.",
                               new[] { new AiMessage(true, "Diga olá e o nome do modelo que você é.") }, cts.Token))
                reply.Append(piece);
            ShowStatus("Funcionou! Resposta: " + reply.ToString().Trim(), ok: true);
        }
        catch (Exception ex)
        {
            ShowStatus(ex is OperationCanceledException ? "Demorou demais para responder." : ex.Message, ok: false);
        }
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = Collect();
        if (cfg.Enabled)
        {
            if (cfg.BaseUrl.Length == 0) { ShowStatus("Informe o endereço da API.", ok: false); return; }
            if (cfg.Model.Length == 0) { ShowStatus("Escolha um modelo.", ok: false); return; }
            if (Preset.NeedsKey && cfg.ApiKeyProtected == null) { ShowStatus("Informe a chave da API.", ok: false); return; }
        }
        try
        {
            App.Current.SaveAi(cfg);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            ShowStatus("Não foi possível salvar: " + ex.Message, ok: false);
        }
    }

    void ShowStatus(string text, bool ok = true, bool busy = false)
    {
        StatusText.Text = text;
        StatusBorder.Visibility = Visibility.Visible;
        var (bg, fg) = busy ? ("#F3F4F6", "#374151") : ok ? ("#ECFDF5", "#065F46") : ("#FEF2F2", "#991B1B");
        StatusBorder.Background = (Brush)new BrushConverter().ConvertFromString(bg)!;
        StatusText.Foreground = (Brush)new BrushConverter().ConvertFromString(fg)!;
    }

    void HideStatus() => StatusBorder.Visibility = Visibility.Collapsed;
}
