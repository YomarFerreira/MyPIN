using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MyPIN.Services.Ai;

namespace MyPIN;

/// <summary>Painel de IA dentro da nota (botão ✨): ações rápidas + conversa livre.</summary>
public partial class NoteWindow
{
    const double AiPanelHeight = 260;

    const string SystemPrompt =
        "Você é um assistente integrado ao MyPIN, um aplicativo de notas adesivas para Windows. " +
        "O usuário está trabalhando na nota mostrada abaixo e conversa com você sobre ela. " +
        "Responda em português do Brasil, a menos que o usuário peça outro idioma. Seja direto e conciso. " +
        "Escreva em texto simples, sem Markdown (nada de **, #, tabelas ou blocos de código), porque a resposta pode ser colada na nota. " +
        "Para listas, use linhas começando com \"- \"; para listas de tarefas, linhas começando com \"☐ \". " +
        "Quando o pedido for reescrever, corrigir, traduzir ou continuar um texto, responda somente com o texto final, sem introdução nem comentários.";

    static readonly Brush AiBubbleBg = Frz(new SolidColorBrush(Color.FromArgb(0xB4, 0xFF, 0xFF, 0xFF)));
    static readonly Brush AiErrorFg = Frz(new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)));
    static Brush Frz(Brush b) { b.Freeze(); return b; }

    readonly List<AiMessage> _aiHistory = new();
    CancellationTokenSource? _aiCts;
    double _aiGrownBy;

    // ---------- inicialização (chamada no construtor) ----------

    void InitAi()
    {
        App.Current.AiChanged += OnAiChanged;
        Closed += (_, _) =>
        {
            App.Current.AiChanged -= OnAiChanged;
            _aiCts?.Cancel();
        };
        BuildQuickActions();
        OnAiChanged();
    }

    void OnAiChanged()
    {
        var cfg = App.Current.Ai;
        AiButton.Visibility = cfg.Enabled ? Visibility.Visible : Visibility.Collapsed;
        AiTitle.Text = $"✨ IA · {cfg.Model}";
        AiTitle.ToolTip = $"{cfg.Preset.Name} — {cfg.Model}";
        if (!cfg.Enabled && AiPanel.IsVisible) CloseAi();
    }

    void BuildQuickActions()
    {
        AiQuick.Children.Clear();
        AiQuick.Children.Add(Chip("Resumir", "Resumir", t => $"Resuma {t.Alvo} em poucas linhas."));
        AiQuick.Children.Add(Chip("Corrigir", "Corrigir o texto", t =>
            $"Corrija ortografia, gramática e pontuação {t.DoAlvo}, mantendo o sentido e o estilo. Responda somente com o texto corrigido."));
        AiQuick.Children.Add(Chip("Tarefas", "Virar lista de tarefas", t =>
            $"Transforme {t.Alvo} em uma lista de tarefas objetivas, uma por linha, cada linha começando com \"☐ \". Responda somente com a lista."));
        AiQuick.Children.Add(Chip("Continuar", "Continuar o texto", _ =>
            "Continue escrevendo o texto da nota a partir de onde ele termina, no mesmo estilo e tom. Responda somente com a continuação."));

        var translate = new Button { Content = "Traduzir ▾", Style = ChipStyle, ToolTip = "Traduzir a nota (ou o trecho selecionado)" };
        translate.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = translate, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var lang in new[] { "Inglês", "Espanhol", "Português", "Francês", "Italiano", "Alemão" })
            {
                var l = lang;
                var it = new MenuItem { Header = l };
                it.Click += (_, _) =>
                {
                    var t = Target();
                    _ = AiSendAsync($"Traduza {t.Alvo} para {l.ToLowerInvariant()}. Responda somente com a tradução.",
                        $"Traduzir para {l.ToLowerInvariant()}{t.Sufixo}");
                };
                menu.Items.Add(it);
            }
            menu.IsOpen = true;
        };
        AiQuick.Children.Add(translate);
    }

    static Style ChipStyle
    {
        get
        {
            var s = new Style(typeof(Button), (Style)Application.Current.Resources["FlatButton"]);
            s.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 2, 8, 2)));
            s.Setters.Add(new Setter(MarginProperty, new Thickness(0, 0, 4, 4)));
            s.Setters.Add(new Setter(FontSizeProperty, 12.0));
            s.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF))));
            s.Setters.Add(new Setter(BorderBrushProperty, new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0))));
            return s;
        }
    }

    /// <summary>Sobre o que a ação age: o trecho selecionado ou a nota toda.</summary>
    sealed record AiTarget(string Alvo, string DoAlvo, string Sufixo);

    AiTarget Target() => Editor.Selection.IsEmpty
        ? new("a nota", "da nota", "")
        : new("o trecho selecionado", "do trecho selecionado", " (seleção)");

    Button Chip(string label, string displayName, Func<AiTarget, string> prompt)
    {
        var b = new Button { Content = label, Style = ChipStyle, ToolTip = displayName + " (a nota toda, ou só o trecho selecionado)" };
        b.Click += (_, _) =>
        {
            var t = Target();
            _ = AiSendAsync(prompt(t), displayName + t.Sufixo);
        };
        return b;
    }

    // ---------- abrir / fechar ----------

    void AiButton_Click(object sender, RoutedEventArgs e) => ToggleAi();
    void AiClose_Click(object sender, RoutedEventArgs e) => CloseAi();
    void AiSettings_Click(object sender, RoutedEventArgs e) => App.Current.ShowAiSettings();

    void ToggleAi()
    {
        if (AiPanel.IsVisible) CloseAi();
        else OpenAi();
    }

    void OpenAi()
    {
        if (!App.Current.Ai.Enabled) return;
        if (!App.Current.Ai.IsReady)
        {
            App.Current.ShowAiSettings();
            if (!App.Current.Ai.IsReady) return;
        }
        if (_note.RolledUp) ApplyRolled(false);

        // Aumenta a nota para o painel não "espremer" o texto (se couber na tela)
        double baseHeight = ActualHeight;
        if (!_note.Locked)
        {
            var wa = SystemParameters.WorkArea;
            double room = Math.Max(0, wa.Bottom - (Top + baseHeight));
            _aiGrownBy = Math.Min(AiPanelHeight, room);
            if (_aiGrownBy > 0) Height = baseHeight + _aiGrownBy;
        }
        // Se a nota não pôde crescer o suficiente, o painel divide o espaço com o texto
        double content = baseHeight + _aiGrownBy - 16 - 2 - 32 - 30; // margem, borda, cabeçalho, barra
        AiPanel.Height = Math.Clamp(_aiGrownBy >= AiPanelHeight ? AiPanelHeight : content * 0.62, 150, AiPanelHeight);
        AiPanel.Visibility = Visibility.Visible;
        if (_aiHistory.Count == 0 && AiConversation.Children.Count == 0)
            AddInfo("Use os botões acima ou escreva um pedido. Se houver texto selecionado na nota, as ações valem só para ele.");
        AiInput.Focus();
    }

    void CloseAi()
    {
        _aiCts?.Cancel();
        AiPanel.Visibility = Visibility.Collapsed;
        if (_aiGrownBy > 0 && !_note.RolledUp)
            Height = Math.Max(120, ActualHeight - _aiGrownBy);
        _aiGrownBy = 0;
        Editor.Focus();
    }

    void AiReset_Click(object sender, RoutedEventArgs e)
    {
        _aiCts?.Cancel();
        _aiHistory.Clear();
        AiConversation.Children.Clear();
        AddInfo("Nova conversa.");
        AiInput.Focus();
    }

    // ---------- envio ----------

    void AiInput_TextChanged(object sender, TextChangedEventArgs e) =>
        AiHint.Visibility = AiInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    void AiInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            SendTyped();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_aiCts != null) _aiCts.Cancel();
            else CloseAi();
            e.Handled = true;
        }
    }

    void AiSend_Click(object sender, RoutedEventArgs e)
    {
        if (_aiCts != null) _aiCts.Cancel(); // durante a resposta o botão vira "parar"
        else SendTyped();
    }

    void SendTyped()
    {
        var text = AiInput.Text.Trim();
        if (text.Length == 0 || _aiCts != null) return;
        AiInput.Clear();
        _ = AiSendAsync(text, text);
    }

    string BuildSystemPrompt()
    {
        FlushContent();
        var sb = new StringBuilder(SystemPrompt);
        sb.Append("\n\n--- NOTA ATUAL ---\n");
        sb.Append("Título: ").Append(string.IsNullOrWhiteSpace(_note.Title) ? "(sem título)" : _note.Title.Trim()).Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(_note.PlainText) ? "(a nota está vazia)" : _note.PlainText);
        sb.Append("\n--- FIM DA NOTA ---");
        if (!Editor.Selection.IsEmpty)
        {
            sb.Append("\n\n--- TRECHO SELECIONADO PELO USUÁRIO ---\n");
            sb.Append(Editor.Selection.Text.TrimEnd());
            sb.Append("\n--- FIM DO TRECHO ---");
        }
        return sb.ToString();
    }

    async Task AiSendAsync(string prompt, string display)
    {
        if (_aiCts != null) return;
        if (!App.Current.Ai.IsReady)
        {
            App.Current.ShowAiSettings();
            if (!App.Current.Ai.IsReady) return;
        }
        if (!AiPanel.IsVisible) OpenAi();

        IAiChat chat;
        try { chat = AiChatFactory.Create(App.Current.Ai); }
        catch (Exception ex) { AddError(ex.Message); return; }

        var system = BuildSystemPrompt();
        _aiHistory.Add(new AiMessage(true, prompt));
        AddUserBubble(display);
        var (answerBox, actions) = AddAnswerBubble();

        _aiCts = new CancellationTokenSource();
        SetBusy(true);
        var answer = new StringBuilder();
        try
        {
            await foreach (var piece in chat.StreamAsync(system, _aiHistory, _aiCts.Token))
            {
                answer.Append(piece);
                answerBox.Text = answer.ToString();
                AiScroll.ScrollToEnd();
            }
            if (answer.Length == 0) answerBox.Text = "(sem resposta)";
        }
        catch (OperationCanceledException)
        {
            answer.Append(answer.Length > 0 ? " …(interrompido)" : "(interrompido)");
            answerBox.Text = answer.ToString();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
            answerBox.Text = ex is AiException ? ex.Message : "Erro: " + ex.Message;
            answerBox.Foreground = AiErrorFg;
            _aiHistory.RemoveAt(_aiHistory.Count - 1); // o pedido que falhou não entra na conversa
            answer.Clear();
        }
        finally
        {
            _aiCts.Dispose();
            _aiCts = null;
            SetBusy(false);
        }

        var final = answer.ToString().Trim();
        if (final.Length > 0)
        {
            _aiHistory.Add(new AiMessage(false, final));
            actions.Visibility = Visibility.Visible;
            actions.Tag = final;
        }
        AiScroll.ScrollToEnd();
    }

    void SetBusy(bool busy)
    {
        AiSendButton.Content = busy ? "" : "";
        AiSendButton.ToolTip = busy ? "Parar (Esc)" : "Enviar (Enter)";
        foreach (UIElement c in AiQuick.Children) c.IsEnabled = !busy;
    }

    // ---------- bolhas da conversa ----------

    void AddInfo(string text) => AiConversation.Children.Add(new TextBlock
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 11.5,
        Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)),
        Margin = new Thickness(2, 2, 2, 6),
    });

    void AddError(string text)
    {
        AiConversation.Children.Add(new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = AiErrorFg, Margin = new Thickness(2, 2, 2, 6),
        });
        AiScroll.ScrollToEnd();
    }

    void AddUserBubble(string text)
    {
        AiConversation.Children.Add(new TextBlock
        {
            Text = "Você: " + text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0, 0, 0)),
            Margin = new Thickness(2, 6, 2, 3),
        });
        AiScroll.ScrollToEnd();
    }

    (TextBox answer, Panel actions) AddAnswerBubble()
    {
        var answer = new TextBox
        {
            Text = "…",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            FontSize = 12.5,
            Padding = new Thickness(0),
        };

        var actions = new WrapPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(ActionButton("Inserir na nota", "Insere a resposta onde está o cursor (depois da seleção, se houver)", t => InsertAiText(t, AiInsertMode.AtCursor), needsEdit: true));
        actions.Children.Add(ActionButton("Substituir seleção", "Troca o texto selecionado na nota pela resposta", t => InsertAiText(t, AiInsertMode.ReplaceSelection), needsEdit: true));
        actions.Children.Add(ActionButton("Substituir tudo", "Troca o texto inteiro da nota pela resposta (a formatação se perde). Ctrl+Z desfaz.", t => InsertAiText(t, AiInsertMode.ReplaceAll), needsEdit: true));
        actions.Children.Add(ActionButton("Copiar", "Copia a resposta", t => { try { Clipboard.SetText(t); } catch { } }, needsEdit: false));

        var stack = new StackPanel();
        stack.Children.Add(answer);
        stack.Children.Add(actions);
        AiConversation.Children.Add(new Border
        {
            Background = AiBubbleBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 4),
            Child = stack,
        });
        AiScroll.ScrollToEnd();
        return (answer, actions);
    }

    Button ActionButton(string label, string tip, Action<string> act, bool needsEdit)
    {
        var b = new Button { Content = label, Style = ChipStyle, ToolTip = tip, FontSize = 11.5 };
        b.Click += (_, _) =>
        {
            if (b.Parent is FrameworkElement { Tag: string text }) act(text);
        };
        if (needsEdit) b.IsEnabled = CanEdit;
        return b;
    }

    enum AiInsertMode { AtCursor, ReplaceSelection, ReplaceAll }

    void InsertAiText(string text, AiInsertMode mode)
    {
        if (!CanEdit) return;
        if (mode == AiInsertMode.ReplaceSelection && Editor.Selection.IsEmpty)
        {
            AddInfo("Selecione na nota o trecho que deve ser substituído e clique de novo.");
            AiScroll.ScrollToEnd();
            return;
        }

        Editor.BeginChange(); // uma única etapa de desfazer (Ctrl+Z)
        try
        {
            switch (mode)
            {
                case AiInsertMode.AtCursor:
                {
                    var pos = Editor.Selection.End;
                    // começa numa linha nova se o cursor não estiver no início de uma linha
                    bool atLineStart = pos.GetLineStartPosition(0)?.CompareTo(pos) == 0;
                    Editor.Selection.Select(pos, pos);
                    Editor.Selection.Text = (atLineStart ? "" : "\n") + text;
                    break;
                }
                case AiInsertMode.ReplaceSelection:
                    Editor.Selection.Text = text;
                    break;
                case AiInsertMode.ReplaceAll:
                    new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text = text;
                    break;
            }
            var end = Editor.Selection.End;
            Editor.Selection.Select(end, end);
            NormalizeCheckboxes(); // a IA costuma devolver listas com ☐
        }
        finally
        {
            Editor.EndChange();
        }
        Editor.Focus();
    }
}
