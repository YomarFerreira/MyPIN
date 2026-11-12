using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;
using MyPIN.Models;
using MyPIN.Services;

namespace MyPIN;

public partial class NoteWindow : Window
{
    const double RolledHeight = 32 + 2 + 16; // cabeçalho + borda + margem da sombra
    const double ZoomMin = 0.6, ZoomMax = 2.5, ZoomStep = 0.1;
    const char BoxEmpty = '☐';
    const char BoxChecked = '☑';

    static readonly Brush FindNotFoundBg = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6))); // cinza clarinho: termo não encontrado
    static readonly CompareInfo Compare = new CultureInfo("pt-BR").CompareInfo;

    readonly Note _note;
    readonly DispatcherTimer _flushTimer;
    bool _loading;
    bool _contentDirty;
    bool _ringing;
    List<TextRange> _matches = new();
    int _matchIndex;
    TextBlock? _zoomLabel;

    public Note Note => _note;

    public NoteWindow(Note note)
    {
        _note = note;
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _flushTimer.Tick += (_, _) => { _flushTimer.Stop(); FlushContent(); };

        _loading = true; // o RichTextBox dispara TextChanged durante a inicialização
        InitializeComponent();
        _loading = false;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = note.Left ?? 100;
        Top = note.Top ?? 100;
        Width = Math.Max(MinWidth, note.Width);
        Topmost = note.Topmost;

        // Menu do botão direito no texto: preenchido na hora de abrir (Editor_ContextMenuOpening)
        Editor.ContextMenu = new ContextMenu();

        LoadContent();
        ApplyColor();
        UpdatePin(animate: false);
        UpdateReminderIcon();
        UpdateInfo();
        ApplyZoom();
        ApplyRolled(note.RolledUp);
        ApplyLock();
        UpdateOpacity();

        LocationChanged += (_, _) =>
        {
            _note.Left = Left;
            _note.Top = Top;
            App.Store.MarkDirty();
        };
        SizeChanged += (_, _) =>
        {
            _note.Width = ActualWidth;
            if (!_note.RolledUp) _note.Height = ActualHeight;
            App.Store.MarkDirty();
        };
        Activated += (_, _) => { UpdateFooter(); UpdateOpacity(); };
        Deactivated += (_, _) => { UpdateFooter(); UpdateOpacity(); FlushContent(); };
        MouseEnter += (_, _) => UpdateOpacity();
        MouseLeave += (_, _) => UpdateOpacity();
        Closing += (_, _) => FlushContent();
        Closed += (_, _) => _ringSound?.Stop();

        Editor.PreviewMouseLeftButtonUp += Editor_PreviewMouseLeftButtonUp;
        InitAi();
        InitRichEdit();
        InitFooter();
    }

    static Brush Freeze(Brush b) { b.Freeze(); return b; }

    // =====================================================================
    // Conteúdo
    // =====================================================================

    void LoadContent()
    {
        if (string.IsNullOrEmpty(_note.ContentRtf)) return;
        _loading = true;
        try
        {
            var range = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
            using var ms = new MemoryStream(Encoding.Latin1.GetBytes(_note.ContentRtf));
            range.Load(ms, DataFormats.Rtf);
            NormalizeLists(Editor.Document.Blocks);
            RichEdit.Normalize(Editor.Document); // refaz estilo de divisórias e tabelas
            RichEdit.FitTables(Editor.Document, Math.Max(MinWidth, _note.Width) - 16 - 2 - 12 - 22); // tabela não maior que a nota
            RestoreCollapsedState(); // tabelas que estavam minimizadas voltam minimizadas
            NormalizeCheckboxes();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
            Editor.Document.Blocks.Clear();
            Editor.Document.Blocks.Add(new Paragraph(new Run(_note.PlainText)));
        }
        finally
        {
            _loading = false;
        }

        // O cursor começa no fim do texto (onde normalmente se continua escrevendo),
        // e não no começo da nota.
        Editor.CaretPosition = Editor.Document.ContentEnd;

        // começa sem histórico de desfazer (não dá para "desfazer" a abertura da nota)
        Editor.IsUndoEnabled = false;
        Editor.IsUndoEnabled = true;
    }

    /// <summary>
    /// O RTF grava as listas com recuo fixo (48px no item). Ao recarregar, removemos esses
    /// valores para valer o estilo da nota (recuo de 20px), igual a uma lista recém-criada.
    /// </summary>
    static void NormalizeLists(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is List list)
            {
                list.ClearValue(Block.MarginProperty);
                list.ClearValue(Block.PaddingProperty);
                list.ClearValue(List.MarkerOffsetProperty);
                foreach (var item in list.ListItems)
                {
                    item.ClearValue(ListItem.MarginProperty);
                    item.ClearValue(ListItem.PaddingProperty);
                    foreach (var p in item.Blocks.OfType<Paragraph>())
                        p.ClearValue(Paragraph.TextIndentProperty);
                    NormalizeLists(item.Blocks); // listas dentro de listas
                }
            }
            else if (block is Section section)
            {
                NormalizeLists(section.Blocks);
            }
        }
    }

    // tabelas minimizadas são gravadas completas (as linhas escondidas não se perdem)
    string GetRtf() => RichEdit.ToRtf(Editor.Document, CollapsedOf);

    public void FlushContent()
    {
        _flushTimer.Stop();
        if (!_contentDirty) return;
        _contentDirty = false;

        _note.ContentRtf = GetRtf();
        _note.CollapsedTables = CollapsedOrdinals();
        _note.PlainText = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd).Text.TrimEnd();
        _note.Modified = DateTime.Now;
        App.Store.MarkDirty();
        UpdateInfo();
        if (_note.RolledUp) UpdateTitle();
    }

    void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _contentDirty = true;
        _flushTimer.Stop();
        _flushTimer.Start();
    }

    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(() =>
        {
            Editor.Focus();
            Keyboard.Focus(Editor);
        }, DispatcherPriority.Input);
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;      // truque para garantir que venha para frente
        Topmost = _note.Topmost;
    }

    // =====================================================================
    // Aparência
    // =====================================================================

    void ApplyColor()
    {
        var c = NoteColors.Get(_note.Color);
        Root.Background = c.BodyBrush;
        Header.Background = c.HeaderBrush;
    }

    void UpdateTitle()
    {
        if (!string.IsNullOrWhiteSpace(_note.Title))
        {
            TitleText.Text = _note.Title;
            TitleText.Opacity = 1;
        }
        else if (_note.RolledUp)
        {
            TitleText.Text = _note.DisplayTitle; // recolhida: mostra a 1ª linha do texto
            TitleText.Opacity = 0.6;
        }
        else
        {
            TitleText.Text = "";
        }
    }

    /// <summary>
    /// Pino: solto = inclinado; fixado = em pé (um pouco mais baixo, "espetado").
    /// Só a posição muda. Ao clicar, ele gira, afunda além do ponto e assenta.
    /// </summary>
    void UpdatePin(bool animate)
    {
        bool on = _note.Topmost;
        PinButton.ToolTip = on
            ? "Fixada sobre as outras janelas (Ctrl+Shift+T)\nClique para soltar"
            : "Manter sobre as outras janelas (Ctrl+Shift+T)";

        double angle = on ? 0 : 40;
        double shift = on ? 1.2 : 0;

        if (!animate)
        {
            PinRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            PinShift.BeginAnimation(TranslateTransform.YProperty, null);
            PinRotate.Angle = angle;
            PinShift.Y = shift;
            return;
        }

        var dur = TimeSpan.FromMilliseconds(320);
        PinRotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(angle, dur)
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        });

        // espetar: desce além do ponto e volta um pouco / soltar: sobe um pouco e volta
        var push = new DoubleAnimationUsingKeyFrames { Duration = dur };
        push.KeyFrames.Add(new EasingDoubleKeyFrame(on ? 3.2 : -2.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        push.KeyFrames.Add(new EasingDoubleKeyFrame(shift, KeyTime.FromTimeSpan(dur), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        PinShift.BeginAnimation(TranslateTransform.YProperty, push);
    }

    public void UpdateReminderIcon()
    {
        var lines = new List<string>();
        if (_note.ReminderAt is DateTime r)
        {
            lines.Add($"Lembrete: {r:dddd, dd/MM 'às' HH:mm}");
            if (_note.ReminderRepeat != ReminderRepeat.None) lines.Add("Repete: " + Reminders.Describe(_note.ReminderRepeat).ToLowerInvariant());
        }
        if (_note.SnoozeAt is DateTime s) lines.Add($"Adiado para {s:dd/MM HH:mm}");

        ReminderButton.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReminderButton.ToolTip = string.Join("\n", lines);
    }

    void UpdateInfo()
    {
        var m = _note.Modified;
        InfoText.Text = m.Date == DateTime.Today ? $"hoje {m:HH:mm}" : m.ToString("dd/MM/yy HH:mm");
        InfoText.ToolTip = $"Criada em {_note.Created:dd/MM/yyyy HH:mm}\nEditada em {m:dd/MM/yyyy HH:mm}";
    }

    void UpdateFooter() =>
        Footer.Visibility = IsActive && !_note.RolledUp && !_note.Locked ? Visibility.Visible : Visibility.Collapsed;

    void UpdateOpacity() =>
        Opacity = IsActive || IsMouseOver ? 1.0 : Math.Clamp(_note.Opacity, 0.2, 1.0);

    void ApplyRolled(bool rolled)
    {
        _note.RolledUp = rolled;
        Editor.Visibility = rolled ? Visibility.Collapsed : Visibility.Visible;
        if (rolled)
        {
            FindBar.Visibility = Visibility.Collapsed;
            _aiCts?.Cancel();
            AiPanel.Visibility = Visibility.Collapsed;
            _aiGrownBy = 0;
        }
        RollButton.Content = rolled ? "" : "";
        RollButton.ToolTip = rolled ? "Expandir (Ctrl+M)" : "Recolher — mostra só o título (Ctrl+M)";
        Height = rolled ? RolledHeight : Math.Max(120, _note.Height);
        UpdateFooter();
        UpdateTitle();
        App.Store.MarkDirty();
    }

    // =====================================================================
    // Bloquear (texto, posição e tamanho)
    // =====================================================================

    void SetLocked(bool locked)
    {
        _note.Locked = locked;
        ApplyLock();
        App.Store.MarkDirty();
        App.Store.NotifyChanged();
    }

    void ApplyLock()
    {
        bool locked = _note.Locked;
        if (locked) EndTitleEdit(true);
        Editor.IsReadOnly = locked;
        LockButton.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        // Sem borda de redimensionar = não dá para mudar o tamanho
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(locked ? 0 : 8),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        UpdateFooter();
    }

    void Lock_Click(object sender, RoutedEventArgs e) => SetLocked(false);

    bool CanEdit => !_note.Locked;

    // =====================================================================
    // Tamanho (zoom) e ajustar ao conteúdo
    // =====================================================================

    void SetZoom(double zoom)
    {
        _note.Zoom = Math.Round(Math.Clamp(zoom, ZoomMin, ZoomMax), 2);
        ApplyZoom();
        App.Store.MarkDirty();
    }

    void ApplyZoom()
    {
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = _note.Zoom;
        if (_zoomLabel != null) _zoomLabel.Text = $"{_note.Zoom * 100:0}%";
    }

    void Editor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(_note.Zoom + (e.Delta > 0 ? ZoomStep : -ZoomStep));
        e.Handled = true;
    }

    /// <summary>Aumenta/diminui a altura da nota para caber exatamente o texto.</summary>
    void FitToContent()
    {
        if (!CanEdit) return;
        if (_note.RolledUp) ApplyRolled(false);
        UpdateLayout();
        double extra = (Editor.ExtentHeight - Editor.ViewportHeight) * _note.Zoom;
        var wa = SystemParameters.WorkArea;
        Height = Math.Clamp(ActualHeight + extra + 2, 120, wa.Height);
        if (Top + Height > wa.Bottom) Top = Math.Max(wa.Top, wa.Bottom - Height);
    }

    // =====================================================================
    // Lembrete tocando
    // =====================================================================

    LoopingSound? _ringSound;

    /// <summary>
    /// Lembrete tocando: a nota vem para a frente (e fica por cima), pisca suavemente e o som
    /// repete até você clicar nela (o som para sozinho depois de 1 minuto).
    /// </summary>
    public void Ring(string? soundId)
    {
        StopRinging();
        _ringing = true;
        if (_note.RolledUp) ApplyRolled(false);
        BringToFront();
        Topmost = true; // enquanto pisca, fica na frente de tudo

        // só pisca: a nota mantém a aparência normal (sem borda colorida)
        BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, 0.45, TimeSpan.FromMilliseconds(750))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });

        var next = _note.ReminderAt is DateTime n && _note.ReminderRepeat != ReminderRepeat.None
            ? $" · próximo: {n:dd/MM HH:mm}" : "";
        ReminderBarText.Text = "⏰ Lembrete" + next;
        ReminderBarText.ToolTip = ReminderBarText.Text;
        ReminderBar.Visibility = Visibility.Visible;

        _ringSound = Sounds.PlayLoop(soundId, App.Store.Settings.ReminderVolume, TimeSpan.FromMinutes(1));
    }

    /// <summary>Para de piscar e de tocar (a barra com "Adiar" continua até você escolher).</summary>
    void StopRinging()
    {
        if (!_ringing) return;
        _ringing = false;
        _ringSound?.Stop();
        _ringSound = null;
        BeginAnimation(OpacityProperty, null);
        UpdateOpacity();
        Topmost = _note.Topmost;
    }

    void Snooze_Click(object sender, RoutedEventArgs e)
    {
        int minutes = int.Parse((string)((Button)sender).Tag);
        _note.SnoozeAt = Reminders.RoundToMinute(DateTime.Now.AddMinutes(minutes)); // minuto cheio: toca sem atraso
        StopRinging();
        ReminderBar.Visibility = Visibility.Collapsed;
        UpdateReminderIcon();
        App.Store.MarkDirty();
        App.Store.NotifyChanged();
    }

    void SnoozeOk_Click(object sender, RoutedEventArgs e)
    {
        StopRinging();
        ReminderBar.Visibility = Visibility.Collapsed;
    }

    // =====================================================================
    // Cabeçalho
    // =====================================================================

    void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            BeginTitleEdit();
            e.Handled = true;
            return;
        }
        if (TitleBox.IsVisible || _note.Locked) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    void Header_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        ShowMenu(BuildMenu(), Header, e.GetPosition(Header));
        e.Handled = true;
    }

    /// <summary>
    /// Abre um menu sem cobrir a nota: ao lado dela (à direita; se não couber, à esquerda), na altura
    /// do botão (ou do clique <paramref name="at"/>), deslizando na vertical para caber inteiro na tela.
    /// Só se não houver espaço dos lados ele abre embaixo/em cima do botão.
    /// </summary>
    void ShowMenu(ContextMenu menu, FrameworkElement target, Point? at = null)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        {
            // Aqui tudo está em pixels da tela (não em unidades do WPF)
            double w = popupSize.Width, h = popupSize.Height;
            var origin = target.PointToScreen(new Point(0, 0));
            var noteTopLeft = Root.PointToScreen(new Point(0, 0));
            var noteBottomRight = Root.PointToScreen(new Point(Root.ActualWidth, Root.ActualHeight));

            // Área útil do monitor onde a nota está (funciona com vários monitores)
            var screen = System.Windows.Forms.Screen.FromPoint(
                new System.Drawing.Point((int)origin.X, (int)origin.Y)).WorkingArea;

            // Altura de referência: o botão ou o ponto clicado; desliza para caber na tela
            double refY = at is Point p ? origin.Y + p.Y * VisualTreeHelper.GetDpi(target).DpiScaleY : origin.Y;
            double y = Math.Max(screen.Top, Math.Min(refY, screen.Bottom - h));

            // O menu tem 10px de margem transparente (sombra): encosta a parte visível na nota
            const double shadow = 10, gap = 2;
            double xRight = noteBottomRight.X + gap - shadow;
            double xLeft = noteTopLeft.X - gap - w + shadow;

            var candidates = new List<CustomPopupPlacement>();
            bool rightFits = xRight + w <= screen.Right, leftFits = xLeft >= screen.Left;
            if (rightFits) candidates.Add(Place(xRight, y));
            if (leftFits) candidates.Add(Place(xLeft, y));
            // avisa o estilo dos submenus para qual lado abrir
            menu.Tag = !rightFits && leftFits ? "left" : null;
            // Sem espaço dos lados: embaixo ou em cima do botão
            double xBtn = origin.X + targetSize.Width - w;
            candidates.Add(Place(xBtn, origin.Y + targetSize.Height));
            candidates.Add(Place(xBtn, origin.Y - h));
            candidates.Add(Place(xBtn, y));
            return candidates.ToArray();

            // posições são relativas ao alvo
            CustomPopupPlacement Place(double sx, double sy) =>
                new(new Point(sx - origin.X, sy - origin.Y), PopupPrimaryAxis.Horizontal);
        };
        menu.IsOpen = true;
    }

    void BeginTitleEdit()
    {
        if (!CanEdit) return;
        TitleBox.Text = _note.Title;
        TitleText.Visibility = Visibility.Collapsed;
        TitleBox.Visibility = Visibility.Visible;
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    void EndTitleEdit(bool commit)
    {
        if (TitleBox.Visibility != Visibility.Visible) return;
        if (commit)
        {
            _note.Title = TitleBox.Text.Trim();
            App.Store.MarkDirty();
        }
        TitleBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        UpdateTitle();
    }

    void TitleBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { EndTitleEdit(true); FocusEditor(); e.Handled = true; }
        else if (e.Key == Key.Escape) { EndTitleEdit(false); FocusEditor(); e.Handled = true; }
    }

    void TitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => EndTitleEdit(true);

    void New_Click(object sender, RoutedEventArgs e) => App.Current.CreateNote(_note);

    void Close_Click(object sender, RoutedEventArgs e) => App.Current.HideNote(_note);

    void Pin_Click(object sender, RoutedEventArgs e) => SetTopmost(!_note.Topmost);

    void Roll_Click(object sender, RoutedEventArgs e) => ApplyRolled(!_note.RolledUp);

    void Reminder_Click(object sender, RoutedEventArgs e) => EditReminder();

    // abre exatamente onde o menu do ⋯ abriria
    void FooterMenu_Click(object sender, RoutedEventArgs e) => ShowMenu(BuildMenu(), MenuButton);

    void Menu_Click(object sender, RoutedEventArgs e)
    {
        ShowMenu(BuildMenu(), MenuButton);
    }

    void SetTopmost(bool value)
    {
        _note.Topmost = value;
        Topmost = value;
        UpdatePin(animate: true);
        App.Store.MarkDirty();
    }

    // =====================================================================
    // Menu principal da nota
    // =====================================================================

    ContextMenu BuildMenu()
    {
        FlushContent();
        var menu = new ContextMenu();
        var items = menu.Items;

        items.Add(Item("Nova nota", "", () => App.Current.CreateNote(_note), "Ctrl+N", bold: true));
        if (App.Current.Ai.Enabled)
            items.Add(Item("Perguntar à IA", "", ToggleAi, "Ctrl+Shift+I"));

        // Cor
        var current = NoteColors.Get(_note.Color);
        var colors = new MenuItem { Header = "Cor", Icon = ColorDot(current) };
        foreach (var c in NoteColors.All)
        {
            var key = c.Key;
            var it = Item(c.Name, null, () => { _note.Color = key; ApplyColor(); App.Store.MarkDirty(); App.Store.NotifyChanged(); });
            it.Icon = ColorDot(c);
            it.IsChecked = c.Key == _note.Color;
            colors.Items.Add(it);
        }
        items.Add(colors);

        // Transparência
        var opacity = new MenuItem { Header = "Transparência", Icon = Glyph("") };
        foreach (var pct in new[] { 100, 90, 80, 70, 60, 50, 40 })
        {
            var value = pct / 100.0;
            var it = Item(pct == 100 ? "Nenhuma (100%)" : $"{pct}%", null, () => { _note.Opacity = value; UpdateOpacity(); App.Store.MarkDirty(); });
            it.IsChecked = Math.Abs(_note.Opacity - value) < 0.01;
            opacity.Items.Add(it);
        }
        opacity.Items.Add(new Separator());
        opacity.Items.Add(new MenuItem { Header = "Vale quando a nota está fora de foco", IsEnabled = false });
        items.Add(opacity);

        items.Add(BuildSizeRow());
        items.Add(BuildFormatMenu());
        items.Add(BuildFooterMenu());
        items.Add(new Separator());

        items.Add(Item(_note.ReminderAt == null ? "Lembrete..." : $"Lembrete: {_note.ReminderAt:dd/MM HH:mm}...", "", EditReminder, "Ctrl+Shift+A"));
        items.Add(Item("Renomear", "", BeginTitleEdit, "F2", enabled: CanEdit));
        items.Add(new Separator());

        items.Add(Item("Localizar...", "", OpenFind, "Ctrl+F"));
        items.Add(Item("Imprimir...", "", Print, "Ctrl+P"));
        items.Add(Item("Exportar...", "", Export, "Ctrl+S"));
        items.Add(Item("Duplicar", "", () => App.Current.DuplicateNote(_note), "Ctrl+D"));
        items.Add(BuildShareMenu());
        items.Add(new Separator());

        var top = Item("Sempre visível", "", () => SetTopmost(!_note.Topmost), "Ctrl+Shift+T");
        top.IsChecked = _note.Topmost;
        items.Add(top);
        var lockItem = Item("Bloquear nota", "", () => SetLocked(!_note.Locked), "Ctrl+Shift+B");
        lockItem.IsChecked = _note.Locked;
        items.Add(lockItem);
        items.Add(Item(_note.RolledUp ? "Expandir" : "Recolher", _note.RolledUp ? "" : "", () => ApplyRolled(!_note.RolledUp), "Ctrl+M"));
        items.Add(Item("Ocultar", "", () => App.Current.HideNote(_note), "Ctrl+W"));
        items.Add(Item("Ocultar outras", null, () => App.Current.HideOthers(_note), "Ctrl+Shift+W"));
        items.Add(new Separator());

        items.Add(BuildMoveMenu());
        items.Add(Item("Excluir nota", "", () => App.Current.DeleteNote(_note), "Ctrl+Shift+Del"));
        items.Add(new Separator());
        items.Add(Item("Gerenciar notas...", "", () => App.Current.ShowManager()));
        items.Add(Item("Sobre o MyPIN...", "", () => App.Current.ShowAbout()));

        return menu;
    }

    MenuItem BuildSizeRow()
    {
        var minus = new Button { Style = MiniStyle, Content = "−", ToolTip = "Diminuir texto (Ctrl+−)" };
        var plus = new Button { Style = MiniStyle, Content = "+", ToolTip = "Aumentar texto (Ctrl++)" };
        var reset = new Button { Style = MiniStyle, MinWidth = 46, ToolTip = "Voltar para 100% (Ctrl+0)" };
        _zoomLabel = new TextBlock { Text = $"{_note.Zoom * 100:0}%" };
        reset.Content = _zoomLabel;
        var fit = new Button
        {
            Style = MiniStyle,
            Content = Glyph(""),
            ToolTip = "Ajustar a altura da nota ao texto",
            IsEnabled = CanEdit,
            Margin = new Thickness(8, 0, 0, 0),
        };
        minus.Click += (_, _) => SetZoom(_note.Zoom - ZoomStep);
        plus.Click += (_, _) => SetZoom(_note.Zoom + ZoomStep);
        reset.Click += (_, _) => SetZoom(1.0);
        fit.Click += (_, _) => FitToContent();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24, 0, 0, 0) };
        buttons.Children.Add(minus);
        buttons.Children.Add(reset);
        buttons.Children.Add(plus);
        buttons.Children.Add(fit);

        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(new TextBlock { Text = "Tamanho", VerticalAlignment = VerticalAlignment.Center });

        return new MenuItem
        {
            Header = row,
            Icon = Glyph(""),
            StaysOpenOnClick = true,
            Template = (ControlTemplate)Application.Current.Resources["MenuRowTemplate"],
        };
    }

    static Style MiniStyle => (Style)Application.Current.Resources["MenuMiniButton"];

    MenuItem BuildShareMenu()
    {
        var share = new MenuItem { Header = "Compartilhar", Icon = Glyph("") };
        share.Items.Add(Item("Copiar texto", "", () => SetClipboardText(_note.ShareText)));
        share.Items.Add(Item("Copiar com formatação", "", CopyFormatted));
        share.Items.Add(new Separator());
        share.Items.Add(Item("Enviar por e-mail", "", SendEmail));
        share.Items.Add(Item("Enviar pelo WhatsApp", "", SendWhatsApp));
        return share;
    }

    MenuItem BuildMoveMenu()
    {
        var move = new MenuItem { Header = "Mover para caderno", Icon = Glyph("") };
        var currentNb = App.Store.GetNotebook(_note);
        foreach (var nb in App.Store.OrderedNotebooks)
        {
            var target = nb;
            var it = Item(nb.Name, null, () => App.Current.MoveNote(_note, target));
            it.Icon = ColorDot(NoteColors.Get(nb.Color));
            it.IsChecked = nb == currentNb;
            move.Items.Add(it);
        }
        move.Items.Add(new Separator());
        move.Items.Add(Item("Novo caderno...", "", () =>
        {
            var dlg = new NotebookDialog(null) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            var nb = new Notebook { Name = dlg.NotebookName, Color = dlg.NotebookColor };
            App.Store.Notebooks.Add(nb);
            App.Current.MoveNote(_note, nb);
        }));
        return move;
    }

    static MenuItem Item(string header, string? glyph, Action action, string? gesture = null, bool enabled = true, bool bold = false)
    {
        var item = new MenuItem
        {
            Header = header,
            InputGestureText = gesture ?? "",
            IsEnabled = enabled,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        };
        if (glyph != null) item.Icon = Glyph(glyph);
        item.Click += (_, e) =>
        {
            if (e.OriginalSource != item) return; // clique veio de um subitem
            action();
        };
        return item;
    }

    static TextBlock Glyph(string glyph) => new()
    {
        Text = glyph,
        FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
        FontSize = 14,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Ellipse ColorDot(NoteColor c) => new()
    {
        Width = 14,
        Height = 14,
        Fill = c.HeaderBrush,
        Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
        StrokeThickness = 1,
        VerticalAlignment = VerticalAlignment.Center,
    };

    void EditReminder()
    {
        var dlg = new ReminderDialog(_note) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _note.ReminderAt = dlg.Result;
            _note.ReminderBase = dlg.Result;
            _note.ReminderRepeat = dlg.Result == null ? ReminderRepeat.None : dlg.Repeat;
            _note.ReminderSound = dlg.Sound;
            _note.SnoozeAt = null;
            UpdateReminderIcon();
            App.Store.MarkDirty();
            App.Store.NotifyChanged();
        }
    }

    // =====================================================================
    // Menu do botão direito dentro do texto
    // =====================================================================

    void Editor_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = Editor.ContextMenu!;
        menu.Items.Clear();

        var mouse = Mouse.GetPosition(Editor);
        var pos = Editor.GetPositionFromPoint(mouse, snapToText: true);
        // Clique numa célula de tabela: opções de linhas/colunas/mesclar primeiro
        AddTableMenuItems(menu, pos, mouse);

        // Correção ortográfica (palavra sublinhada em vermelho)
        var error = pos != null && CanEdit ? Editor.GetSpellingError(pos) : null;
        if (error != null)
        {
            var suggestions = error.Suggestions.Take(5).ToList();
            foreach (var s in suggestions)
            {
                var word = s;
                menu.Items.Add(Item(word, null, () => error.Correct(word), bold: true));
            }
            if (suggestions.Count == 0)
                menu.Items.Add(new MenuItem { Header = "(sem sugestões)", IsEnabled = false });
            menu.Items.Add(Item("Ignorar esta palavra", null, () => error.IgnoreAll()));
            menu.Items.Add(new Separator());
        }

        menu.Items.Add(Cmd("Desfazer", "", ApplicationCommands.Undo, "Ctrl+Z"));
        menu.Items.Add(Cmd("Refazer", "", ApplicationCommands.Redo, "Ctrl+Y"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Recortar", "", ApplicationCommands.Cut, "Ctrl+X"));
        menu.Items.Add(Cmd("Copiar", "", ApplicationCommands.Copy, "Ctrl+C"));
        menu.Items.Add(Cmd("Colar", "", ApplicationCommands.Paste, "Ctrl+V"));
        menu.Items.Add(Item("Colar como texto simples", null, PastePlain, "Ctrl+Shift+V", enabled: CanEdit && SafeContainsText()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Cmd("Selecionar tudo", "", ApplicationCommands.SelectAll, "Ctrl+A"));
        menu.Items.Add(Item("Inserir data e hora", "", InsertDateTime, "F5", enabled: CanEdit));
    }

    MenuItem Cmd(string header, string glyph, RoutedUICommand cmd, string gesture)
    {
        var it = Item(header, glyph, () => cmd.Execute(null, Editor), gesture);
        it.IsEnabled = cmd.CanExecute(null, Editor);
        return it;
    }

    static bool SafeContainsText()
    {
        try { return Clipboard.ContainsText(); } catch { return false; }
    }

    // =====================================================================
    // Localizar
    // =====================================================================

    void OpenFind()
    {
        if (_note.RolledUp) ApplyRolled(false);
        FindBar.Visibility = Visibility.Visible;
        var sel = Editor.Selection.Text;
        if (!string.IsNullOrWhiteSpace(sel) && !sel.Contains('\n') && sel.Length < 60) FindBox.Text = sel;
        FindBox.Focus();
        FindBox.SelectAll();
        RunFind(keepIndex: false);
    }

    void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _matches.Clear();
        DrawFindMarks();
        Editor.Focus();
    }

    void FindBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindHint.Visibility = FindBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RunFind(keepIndex: false);
    }

    void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { StepFind(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
    }

    void FindNext_Click(object sender, RoutedEventArgs e) => StepFind(1);
    void FindPrev_Click(object sender, RoutedEventArgs e) => StepFind(-1);
    void FindClose_Click(object sender, RoutedEventArgs e) => CloseFind();

    void StepFind(int delta)
    {
        RunFind(keepIndex: true); // o texto pode ter mudado
        if (_matches.Count == 0) return;
        _matchIndex = (_matchIndex + delta + _matches.Count) % _matches.Count;
        ShowMatch();
    }

    void RunFind(bool keepIndex)
    {
        _matches = FindAll(FindBox.Text);
        if (!keepIndex) _matchIndex = 0;
        if (_matchIndex >= _matches.Count) _matchIndex = 0;
        ShowMatch();
    }

    List<TextRange> FindAll(string query)
    {
        var result = new List<TextRange>();
        if (string.IsNullOrEmpty(query)) return result;
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

        var nav = Editor.Document.ContentStart;
        while (nav != null && nav.CompareTo(Editor.Document.ContentEnd) < 0)
        {
            if (nav.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = nav.GetTextInRun(LogicalDirection.Forward);
                int idx = 0;
                while (idx < text.Length && (idx = Compare.IndexOf(text, query, idx, opts)) >= 0)
                {
                    var start = nav.GetPositionAtOffset(idx);
                    var end = start?.GetPositionAtOffset(query.Length);
                    if (start != null && end != null) result.Add(new TextRange(start, end));
                    idx += query.Length;
                }
            }
            nav = nav.GetNextContextPosition(LogicalDirection.Forward);
        }
        return result;
    }

    void ShowMatch()
    {
        bool hasQuery = FindBox.Text.Length > 0;
        FindBox.Background = hasQuery && _matches.Count == 0 ? FindNotFoundBg : (Brush)new BrushConverter().ConvertFromString("#AAFFFFFF")!;
        if (_matches.Count == 0)
        {
            FindCount.Text = hasQuery ? "0 de 0" : "";
            DrawFindMarks();
            return;
        }
        var r = _matches[_matchIndex];
        // a seleção acompanha a ocorrência atual: fechando o Localizar, o cursor fica nela
        Editor.Selection.Select(r.Start, r.End);
        ScrollToMatch(r);
        FindCount.Text = $"{_matchIndex + 1} de {_matches.Count}";
        DrawFindMarks();
        // depois da rolagem o desenho precisa ser refeito no lugar novo
        Dispatcher.BeginInvoke(DrawFindMarks, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Rola o texto só o necessário para a ocorrência ficar à vista (com uma folga).</summary>
    void ScrollToMatch(TextRange r)
    {
        var rect = r.Start.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty) { r.Start.Paragraph?.BringIntoView(); return; }
        double margin = 24, view = Editor.ViewportHeight;
        if (view <= 0) return;
        // GetCharacterRect vem nas coordenadas da caixa de texto (já descontada a rolagem)
        if (rect.Top < margin) Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + rect.Top - margin));
        else if (rect.Bottom > view - margin) Editor.ScrollToVerticalOffset(Editor.VerticalOffset + rect.Bottom - view + margin);
    }

    static readonly Brush FindMarkLine = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)));
    static readonly Brush FindMarkCurrentLine = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x00)));
    static readonly Brush FindMarkText = Freeze(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));

    /// <summary>
    /// Desenha por cima do texto um realce em todas as ocorrências e um mais forte (com contorno) na
    /// atual. É só desenho: o texto da nota não muda, nada vai para o desfazer nem para o arquivo.
    /// </summary>
    void DrawFindMarks()
    {
        FindOverlay.Children.Clear();
        if (FindBar.Visibility != Visibility.Visible || _matches.Count == 0) return;
        for (int i = 0; i < _matches.Count; i++)
        {
            var m = _matches[i];
            Rect a, b;
            try
            {
                a = m.Start.GetCharacterRect(LogicalDirection.Forward);
                b = m.End.GetCharacterRect(LogicalDirection.Backward);
            }
            catch (InvalidOperationException) { continue; } // texto mudou e a medida ainda não está pronta
            if (a.IsEmpty || b.IsEmpty) continue;
            if (a.Bottom < 0 || a.Top > Editor.ActualHeight) continue; // fora da área visível
            // ocorrência quebrada em duas linhas: realça só o pedaço da primeira
            double right = Math.Abs(a.Top - b.Top) < 1 ? b.Right : a.Right + 8;
            var p1 = Editor.TranslatePoint(new Point(a.Left - 1, a.Top), FindOverlay);
            var p2 = Editor.TranslatePoint(new Point(right + 1, a.Bottom), FindOverlay);
            bool current = i == _matchIndex;
            // etiqueta branca por cima, com o termo achado escrito em negrito (na mesma fonte e tamanho)
            double scale = ZoomTransform.ScaleX > 0 ? ZoomTransform.ScaleX : 1;
            var family = m.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily ?? Editor.FontFamily;
            double size = m.GetPropertyValue(TextElement.FontSizeProperty) is double fs ? fs : Editor.FontSize;
            var mark = new Border
            {
                MinWidth = Math.Max(2, p2.X - p1.X), Height = Math.Max(2, p2.Y - p1.Y),
                Background = Brushes.White, CornerRadius = new CornerRadius(3),
                BorderBrush = current ? FindMarkCurrentLine : FindMarkLine,
                BorderThickness = new Thickness(current ? 1.6 : 1),
                Padding = new Thickness(1, 0, 1, 0),
                Child = new TextBlock
                {
                    Text = m.Text, FontFamily = family, FontSize = size * scale, FontWeight = FontWeights.Bold,
                    Foreground = FindMarkText, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                },
            };
            if (current) mark.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = 0.35, Color = Color.FromRgb(0xE0, 0x6A, 0x00) };
            Canvas.SetLeft(mark, p1.X - 1);
            Canvas.SetTop(mark, p1.Y);
            FindOverlay.Children.Add(mark);
        }
    }

    // =====================================================================
    // Imprimir, Exportar, Compartilhar
    // =====================================================================

    void Print()
    {
        FlushContent();
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        try
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(72),
                ColumnWidth = double.PositiveInfinity,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PageWidth = dlg.PrintableAreaWidth,
                PageHeight = dlg.PrintableAreaHeight,
            };
            var ps = new Style(typeof(Paragraph));
            ps.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            doc.Resources.Add(typeof(Paragraph), ps);
            var ls = new Style(typeof(List));
            ls.Setters.Add(new Setter(Block.MarginProperty, new Thickness(0)));
            ls.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(20, 0, 0, 0)));
            doc.Resources.Add(typeof(List), ls);

            if (!string.IsNullOrWhiteSpace(_note.Title))
                doc.Blocks.Add(new Paragraph(new Run(_note.Title.Trim())) { FontSize = 20, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });

            var src = new TextRange(Editor.Document.ContentStart, Editor.Document.ContentEnd);
            using var ms = new MemoryStream();
            src.Save(ms, DataFormats.XamlPackage);
            ms.Position = 0;
            new TextRange(doc.ContentEnd, doc.ContentEnd).Load(ms, DataFormats.XamlPackage);
            NormalizeLists(doc.Blocks);
            RichEdit.Normalize(doc);

            dlg.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator, "MyPIN - " + _note.DisplayTitle);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
            MessageBox.Show(this, "Não foi possível imprimir:\n" + ex.Message, "MyPIN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Export()
    {
        FlushContent();
        var dlg = new SaveFileDialog
        {
            FileName = SafeFileName(_note.DisplayTitle),
            Filter = "Texto simples (*.txt)|*.txt|Texto formatado — abre no Word (*.rtf)|*.rtf",
            AddExtension = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (dlg.FileName.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase))
                File.WriteAllBytes(dlg.FileName, Encoding.Latin1.GetBytes(GetRtf()));
            else
                File.WriteAllText(dlg.FileName, _note.ShareText.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível exportar:\n" + ex.Message, "MyPIN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        if (clean.Length > 60) clean = clean[..60].Trim();
        return clean.Length == 0 || clean == "(nota vazia)" ? "nota" : clean;
    }

    void SetClipboardText(string text)
    {
        try { Clipboard.SetText(text); } catch (Exception ex) { Services.Log.Error(ex); }
    }

    void CopyFormatted()
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.Rtf, GetRtf());
            data.SetData(DataFormats.UnicodeText, _note.ShareText);
            Clipboard.SetDataObject(data, copy: true);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
        }
    }

    void SendEmail()
    {
        var body = _note.PlainText.Length > 1800 ? _note.PlainText[..1800] + "…" : _note.PlainText;
        OpenUri($"mailto:?subject={Uri.EscapeDataString(_note.DisplayTitle)}&body={Uri.EscapeDataString(body)}");
    }

    void SendWhatsApp()
    {
        var text = _note.ShareText.Length > 3000 ? _note.ShareText[..3000] + "…" : _note.ShareText;
        var escaped = Uri.EscapeDataString(text);
        // App do WhatsApp instalado? Senão, abre o WhatsApp Web no navegador.
        bool hasApp = false;
        try { using var k = Registry.ClassesRoot.OpenSubKey("whatsapp"); hasApp = k != null; } catch { }
        OpenUri(hasApp ? $"whatsapp://send?text={escaped}" : $"https://wa.me/?text={escaped}");
    }

    void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
            MessageBox.Show(this, "Não encontrei um programa para abrir isso.\n" + ex.Message, "MyPIN", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // =====================================================================
    // Teclado e formatação
    // =====================================================================

    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e) => StopRinging();

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        StopRinging();
        if (TitleBox.IsKeyboardFocusWithin) return;
        if (HighlightPaletteKey(e.Key == Key.System ? e.SystemKey : e.Key)) { e.Handled = true; return; }

        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Na caixa de Localizar e na da IA só valem os atalhos com Ctrl
        if ((FindBox.IsKeyboardFocusWithin || AiInput.IsKeyboardFocusWithin) && !mods.HasFlag(ModifierKeys.Control)) return;

        bool handled = true;
        if (mods == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.N: App.Current.CreateNote(_note); break;
                case Key.W: App.Current.HideNote(_note); break;
                case Key.T: if (CanEdit) ToggleStrike(); break;
                case Key.H: ShowHighlightPalette(); break;
                case Key.F: OpenFind(); break;
                case Key.P: Print(); break;
                case Key.S: Export(); break;
                case Key.D: App.Current.DuplicateNote(_note); break;
                case Key.M: ApplyRolled(!_note.RolledUp); break;
                case Key.OemPlus: case Key.Add: SetZoom(_note.Zoom + ZoomStep); break;
                case Key.OemMinus: case Key.Subtract: SetZoom(_note.Zoom - ZoomStep); break;
                case Key.D0: case Key.NumPad0: SetZoom(1.0); break;
                default: handled = false; break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            switch (key)
            {
                case Key.V: if (CanEdit) PastePlain(); break;
                case Key.OemPeriod: if (CanEdit) StepFontSize(+1); break;  // Ctrl+Shift+> (como no Word)
                case Key.OemComma: if (CanEdit) StepFontSize(-1); break;   // Ctrl+Shift+<
                case Key.C: if (CanEdit) ToggleCheckbox(); break;
                case Key.A: EditReminder(); break;
                case Key.T: SetTopmost(!_note.Topmost); break;
                case Key.B: SetLocked(!_note.Locked); break;
                case Key.W: App.Current.HideOthers(_note); break;
                case Key.Delete: App.Current.DeleteNote(_note); break;
                case Key.I: if (App.Current.Ai.Enabled) ToggleAi(); else handled = false; break;
                default: handled = false; break;
            }
        }
        else if (mods == ModifierKeys.None && key == Key.Enter && HandleEnter())
        {
            // saiu da tabela, ou "---" + Enter virou linha divisória
        }
        else if (mods == ModifierKeys.None && key is Key.Delete or Key.Back && HandleDeleteDivider(forward: key == Key.Delete))
        {
            // apagou a linha divisória inteira
        }
        else if (mods == ModifierKeys.None && key == Key.F5)
        {
            if (CanEdit) InsertDateTime();
        }
        else if (mods == ModifierKeys.None && key == Key.F2)
        {
            BeginTitleEdit();
        }
        else
        {
            handled = false;
        }
        e.Handled = handled;
    }

    // Cada botão da barra devolve o foco ao texto, para o cursor ficar visível
    // e você ver onde a formatação foi aplicada.
    void Bold_Click(object sender, RoutedEventArgs e) => Format(() => EditingCommands.ToggleBold.Execute(null, Editor));
    void Italic_Click(object sender, RoutedEventArgs e) => Format(() => EditingCommands.ToggleItalic.Execute(null, Editor));
    void Underline_Click(object sender, RoutedEventArgs e) => Format(() => EditingCommands.ToggleUnderline.Execute(null, Editor));
    void Strike_Click(object sender, RoutedEventArgs e) => Format(ToggleStrike);
    void Bullets_Click(object sender, RoutedEventArgs e) => Format(() => EditingCommands.ToggleBullets.Execute(null, Editor));
    void Checkbox_Click(object sender, RoutedEventArgs e) => Format(ToggleCheckbox);

    void Format(Action action)
    {
        Editor.Focus();
        if (CanEdit) action();
    }

    void ToggleStrike()
    {
        var sel = Editor.Selection;
        if (sel.IsEmpty) return;
        bool has = sel.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection tdc
                   && tdc.Any(d => d.Location == TextDecorationLocation.Strikethrough);
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    void PastePlain()
    {
        if (!CanEdit) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var text = Clipboard.GetText();
            Editor.Selection.Text = text;
            var end = Editor.Selection.End;
            Editor.Selection.Select(end, end);
            NormalizeCheckboxes();
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex);
        }
    }

    void InsertDateTime()
    {
        if (!CanEdit) return;
        Editor.Selection.Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
        var end = Editor.Selection.End;
        Editor.Selection.Select(end, end);
        Editor.Focus();
    }

    static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    /// <summary>
    /// A fonte do texto (Segoe UI) não tem ☐/☑, e o Windows buscava cada um numa fonte reserva
    /// diferente (o ☑ vinha da fonte de emoji: maior e mais grosso). Aqui as caixinhas passam a usar
    /// sempre a Segoe UI Symbol, em que as duas têm o mesmo tamanho e traço.
    /// </summary>
    void NormalizeCheckboxes()
    {
        var doc = Editor.Document;
        var boxes = new List<TextRange>();
        for (var nav = doc.ContentStart; nav != null && nav.CompareTo(doc.ContentEnd) < 0;
             nav = nav.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (nav.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var text = nav.GetTextInRun(LogicalDirection.Forward);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != BoxEmpty && text[i] != BoxChecked) continue;
                var a = nav.GetPositionAtOffset(i);
                var b = a?.GetPositionAtOffset(1);
                if (a != null && b != null) boxes.Add(new TextRange(a, b));
            }
        }
        foreach (var r in boxes)
        {
            if (r.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && f.Source == SymbolFont.Source) continue;
            r.ApplyPropertyValue(TextElement.FontFamilyProperty, SymbolFont);
        }
    }

    /// <summary>Coloca/tira "☐ " no começo do parágrafo atual.</summary>
    void ToggleCheckbox()
    {
        ToggleCheckboxCore();
        NormalizeCheckboxes();
    }

    /// <summary>Clicar numa caixa ☐ marca ☑ (e vice-versa).</summary>
    void Editor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanEdit || !Editor.Selection.IsEmpty) return;
        var pos = Editor.GetPositionFromPoint(e.GetPosition(Editor), snapToText: false);
        if (pos == null) return;

        foreach (var dir in new[] { LogicalDirection.Forward, LogicalDirection.Backward })
        {
            var run = pos.GetTextInRun(dir);
            if (run.Length == 0) continue;
            char ch = dir == LogicalDirection.Forward ? run[0] : run[^1];
            if (ch != BoxEmpty && ch != BoxChecked) continue;

            var a = dir == LogicalDirection.Forward ? pos : pos.GetPositionAtOffset(-1);
            var b = dir == LogicalDirection.Forward ? pos.GetPositionAtOffset(1) : pos;
            if (a == null || b == null) return;
            var range = new TextRange(a, b);
            range.Text = (ch == BoxEmpty ? BoxChecked : BoxEmpty).ToString();
            NormalizeCheckboxes();
            return;
        }
    }
}
