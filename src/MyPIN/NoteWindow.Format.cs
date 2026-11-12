using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MyPIN.Services;

namespace MyPIN;

/// <summary>Marca-texto com cores e caixas de tarefa em várias linhas.</summary>
public partial class NoteWindow
{
    // ---------- Marca-texto ----------

    /// <summary>As cores do marca-texto (nome, cor). A ordem é a dos atalhos 1–4 com a paleta aberta.</summary>
    static readonly (string Name, Color Color)[] HighlightColors =
    {
        ("Amarelo", Color.FromRgb(0xFF, 0xE9, 0x3D)),
        ("Laranja", Color.FromRgb(0xFF, 0xB8, 0x5C)),
        ("Verde-limão", Color.FromRgb(0xC6, 0xF4, 0x5A)),
        ("Azul", Color.FromRgb(0x9E, 0xD8, 0xFF)),
    };

    Popup? _highlightPopup;

    /// <summary>Abre a paleta de cores do marca-texto (botão do pincel ou Ctrl+H).</summary>
    void ShowHighlightPalette()
    {
        if (!CanEdit) return;
        Editor.Focus();
        var res = Application.Current.Resources;
        var bg = (Brush)res["Menu.Background"];
        var fg = (Brush)res["Menu.Foreground"];
        var line = (Brush)res["Menu.Border"];
        var muted = (Brush)res["Menu.Shortcut"];

        var popup = new Popup
        {
            PlacementTarget = HighlightButton.IsVisible ? HighlightButton : Editor,
            Placement = HighlightButton.IsVisible ? PlacementMode.Top : PlacementMode.Center,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        int n = 1;
        foreach (var (name, color) in HighlightColors)
        {
            var c = color;
            var swatch = new Border
            {
                Width = 24, Height = 24, Margin = new Thickness(3), CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(c), BorderBrush = line, BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand, ToolTip = $"{name} (tecla {n})",
            };
            AutomationPropertiesHelper.SetName(swatch, "Marca-texto " + name);
            swatch.MouseEnter += (_, _) => swatch.BorderBrush = fg;
            swatch.MouseLeave += (_, _) => swatch.BorderBrush = line;
            swatch.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; ApplyHighlight(c); };
            row.Children.Add(swatch);
            n++;
        }

        // último círculo, do mesmo tamanho: "×" = tirar o marca-texto (qualquer cor)
        var clear = new Border
        {
            Width = 24, Height = 24, Margin = new Thickness(3), CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent, BorderBrush = line, BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand, ToolTip = "Tirar o marca-texto (tecla 0)",
            Child = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 L 8,8 M 8,0 L 0,8"), Stroke = fg, StrokeThickness = 1.4,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        AutomationPropertiesHelper.SetName(clear, "Tirar marca-texto");
        clear.MouseEnter += (_, _) => clear.BorderBrush = fg;
        clear.MouseLeave += (_, _) => clear.BorderBrush = line;
        clear.MouseLeftButtonUp += (_, _) => { popup.IsOpen = false; ClearHighlight(); };
        row.Children.Add(clear);

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Marca-texto", FontWeight = FontWeights.SemiBold, FontSize = 12, Foreground = fg, Margin = new Thickness(3, 0, 0, 4) });
        panel.Children.Add(row);

        popup.Child = new Border
        {
            Margin = new Thickness(10, 10, 10, 6),
            Background = bg, BorderBrush = line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 8, 10, 8),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.3 },
            Child = panel,
        };
        TextElement.SetFontFamily(popup.Child, new FontFamily("Segoe UI"));
        TextElement.SetFontSize(popup.Child, 12);
        popup.Closed += (_, _) => { if (_highlightPopup == popup) _highlightPopup = null; };
        _highlightPopup = popup;
        popup.IsOpen = true;
    }

    /// <summary>Com a paleta aberta: 1–4 escolhem a cor, 0 tira, Esc fecha. Devolve true se tratou a tecla.</summary>
    bool HighlightPaletteKey(Key key)
    {
        if (_highlightPopup is not { IsOpen: true } popup) return false;
        int idx = key switch
        {
            Key.D1 or Key.NumPad1 => 0, Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2, Key.D4 or Key.NumPad4 => 3,
            Key.D0 or Key.NumPad0 => -1,
            Key.Escape => -2,
            _ => -3,
        };
        if (idx == -3) { popup.IsOpen = false; return false; } // outra tecla: fecha e deixa seguir
        popup.IsOpen = false;
        if (idx >= 0) ApplyHighlight(HighlightColors[idx].Color);
        else if (idx == -1) ClearHighlight();
        return true;
    }

    void ClearHighlight() => Format(() => Editor.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, null));

    /// <summary>Pinta o trecho com a cor; se o trecho já estiver todo nessa cor, tira a marca.</summary>
    void ApplyHighlight(Color color)
    {
        Format(() =>
        {
            var sel = Editor.Selection;
            bool same = sel.GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush b && b.Color == color;
            // sem seleção, vale para o que for digitado em seguida
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, same ? null : Freeze(new SolidColorBrush(color)));
        });
    }

    void Highlight_Click(object sender, RoutedEventArgs e) => ShowHighlightPalette();

    // ---------- Caixas de tarefa em várias linhas ----------

    /// <summary>
    /// Com várias linhas selecionadas, cada linha (com texto) recebe sua caixinha ☐. Se todas já
    /// tiverem, o botão tira de todas. Com uma linha só, põe/tira na linha do cursor.
    /// </summary>
    void ToggleCheckboxCore()
    {
        var lines = SelectedParagraphs()
            .Where(p => RichEdit.DividerKindOf(p) == null && new TextRange(p.ContentStart, p.ContentEnd).Text.Trim().Length > 0)
            .ToList();
        if (lines.Count > 1)
        {
            bool allHave = lines.All(p => BoxPrefixLength(p) > 0);
            Editor.BeginChange();
            try
            {
                foreach (var p in lines)
                {
                    int len = BoxPrefixLength(p);
                    var start = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                    if (allHave) new TextRange(start, start.GetPositionAtOffset(len)).Text = "";
                    else if (len == 0) start.InsertTextInRun(BoxEmpty + " ");
                }
            }
            finally { Editor.EndChange(); }
            return;
        }

        var para = Editor.CaretPosition.Paragraph;
        if (para == null)
        {
            Editor.CaretPosition.InsertTextInRun(BoxEmpty + " ");
            return;
        }
        int has = BoxPrefixLength(para);
        var at = para.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        if (has > 0) new TextRange(at, at.GetPositionAtOffset(has)).Text = "";
        else at.InsertTextInRun(BoxEmpty + " ");
        Editor.Focus();
    }

    /// <summary>Quantos caracteres a caixinha ocupa no começo da linha ("☐ " = 2, "☐" = 1, nenhuma = 0).</summary>
    static int BoxPrefixLength(Paragraph p)
    {
        var start = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        var text = start.GetTextInRun(LogicalDirection.Forward);
        if (text.Length == 0 || (text[0] != BoxEmpty && text[0] != BoxChecked)) return 0;
        return text.Length > 1 && text[1] == ' ' ? 2 : 1;
    }

    /// <summary>Os parágrafos (linhas) tocados pela seleção, na ordem do texto — inclusive dentro de listas e tabelas.</summary>
    List<Paragraph> SelectedParagraphs()
    {
        var sel = Editor.Selection;
        var result = new List<Paragraph>();
        if (sel.IsEmpty) return result;
        foreach (var p in AllParagraphs(Editor.Document.Blocks))
        {
            // a linha entra se a seleção começa antes do fim dela e termina depois do começo dela
            // (seleção que termina bem no começo de uma linha não leva essa linha)
            if (p.ContentEnd.CompareTo(sel.Start) >= 0 && p.ContentStart.CompareTo(sel.End) < 0
                && !(p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).CompareTo(sel.End) >= 0))
                result.Add(p);
        }
        return result;
    }

    static IEnumerable<Paragraph> AllParagraphs(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p: yield return p; break;
                case Section s: foreach (var x in AllParagraphs(s.Blocks)) yield return x; break;
                case List l:
                    foreach (var li in l.ListItems)
                        foreach (var x in AllParagraphs(li.Blocks)) yield return x;
                    break;
                case Table t:
                    foreach (var g in t.RowGroups)
                        foreach (var r in g.Rows)
                            foreach (var c in r.Cells)
                                foreach (var x in AllParagraphs(c.Blocks)) yield return x;
                    break;
            }
        }
    }
}

/// <summary>Atalho para dar nome acessível (usado também pelos testes de interface).</summary>
static class AutomationPropertiesHelper
{
    public static void SetName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);
}
