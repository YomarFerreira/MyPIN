using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MyPIN.Services;

namespace MyPIN;

/// <summary>
/// Barra de baixo: escolher quais botões aparecem (vale para todas as notas) e o submenu
/// "Formatar" do menu ⋯, com tudo o que a barra faz.
/// </summary>
public partial class NoteWindow
{
    /// <summary>Botões da barra de baixo (a chave fica no Tag de cada botão no XAML).</summary>
    public static readonly (string Key, string Label)[] FooterItems =
    {
        ("menu", "Menu ☰"),
        ("undo", "Desfazer"),
        ("redo", "Refazer"),
        ("size", "A− / A+ (tamanho da letra)"),
        ("font", "Tipo de letra"),
        ("bold", "Negrito"),
        ("italic", "Itálico"),
        ("underline", "Sublinhado"),
        ("strike", "Tachado"),
        ("highlight", "Marca-texto"),
        ("bullets", "Lista com marcadores"),
        ("checkbox", "Caixa de tarefa"),
        ("table", "Tabela"),
        ("divider", "Linha divisória"),
    };

    void InitFooter()
    {
        ApplyFooterButtons();
        App.Current.FooterChanged += ApplyFooterButtons;
        Closed += (_, _) => App.Current.FooterChanged -= ApplyFooterButtons;
    }

    /// <summary>Mostra/esconde os botões conforme a configuração.</summary>
    void ApplyFooterButtons()
    {
        var hidden = App.Store.Settings.HiddenFooterButtons;
        foreach (var child in FooterButtons.Children.OfType<FrameworkElement>())
            if (child.Tag is string key)
                child.Visibility = hidden.Contains(key) ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------- Menu ⋯ → "Barra de baixo" ----------

    MenuItem BuildFooterMenu()
    {
        var bar = new MenuItem { Header = "Barra de baixo", Icon = Glyph("") };
        var hidden = App.Store.Settings.HiddenFooterButtons;
        var toggles = new List<MenuItem>();
        var showAll = new MenuItem { Header = "Mostrar todos", IsEnabled = hidden.Count > 0, StaysOpenOnClick = true };
        foreach (var (key, label) in FooterItems)
        {
            var k = key;
            var it = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = !hidden.Contains(key),
                StaysOpenOnClick = true, // dá para marcar/desmarcar vários de uma vez
            };
            it.Click += (_, _) =>
            {
                App.Current.SetFooterButton(k, it.IsChecked);
                showAll.IsEnabled = hidden.Count > 0;
            };
            toggles.Add(it);
            bar.Items.Add(it);
        }
        showAll.Click += (_, _) =>
        {
            App.Current.ShowAllFooterButtons();
            foreach (var t in toggles) t.IsChecked = true;
            showAll.IsEnabled = false;
        };
        bar.Items.Add(new Separator());
        bar.Items.Add(showAll);
        bar.Items.Add(new MenuItem { Header = "Vale para todas as notas", IsEnabled = false });
        return bar;
    }

    // ---------- Menu ⋯ → "Formatar" (tudo o que a barra de baixo faz) ----------

    MenuItem BuildFormatMenu()
    {
        var f = new MenuItem { Header = "Formatar", Icon = Glyph("") };
        var items = f.Items;
        bool edit = CanEdit;

        items.Add(Item("Desfazer", "", () => Format(() => ApplicationCommands.Undo.Execute(null, Editor)), "Ctrl+Z", enabled: edit));
        items.Add(Item("Refazer", "", () => Format(() => ApplicationCommands.Redo.Execute(null, Editor)), "Ctrl+Y", enabled: edit));
        items.Add(new Separator());

        items.Add(WithIcon(Item("Diminuir letra", null, () => Format(() => StepFontSize(-1)), "Ctrl+Shift+<", enabled: edit), Letter("A−", 11)));
        items.Add(WithIcon(Item("Aumentar letra", null, () => Format(() => StepFontSize(+1)), "Ctrl+Shift+>", enabled: edit), Letter("A+", 14)));
        var fonts = new MenuItem { Header = "Tipo de letra", Icon = Letter("Aa", 13, family: new FontFamily("Times New Roman")), IsEnabled = edit };
        AddFontItems(fonts.Items);
        items.Add(fonts);
        items.Add(new Separator());

        items.Add(WithIcon(Item("Negrito", null, () => Format(() => EditingCommands.ToggleBold.Execute(null, Editor)), "Ctrl+B", enabled: edit), Letter("B", 13, bold: true)));
        items.Add(WithIcon(Item("Itálico", null, () => Format(() => EditingCommands.ToggleItalic.Execute(null, Editor)), "Ctrl+I", enabled: edit), Letter("I", 13, italic: true)));
        items.Add(WithIcon(Item("Sublinhado", null, () => Format(() => EditingCommands.ToggleUnderline.Execute(null, Editor)), "Ctrl+U", enabled: edit), Letter("U", 13, deco: TextDecorations.Underline)));
        items.Add(WithIcon(Item("Tachado", null, () => Format(ToggleStrike), "Ctrl+T", enabled: edit), Letter("S", 13, deco: TextDecorations.Strikethrough)));

        var hl = new MenuItem { Header = "Marca-texto", Icon = Glyph(""), IsEnabled = edit };
        foreach (var (name, color) in HighlightColors)
        {
            var c = color;
            hl.Items.Add(WithIcon(Item(name, null, () => ApplyHighlight(c)),
                new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(c), Stroke = (Brush)Application.Current.Resources["Menu.Border"] }));
        }
        hl.Items.Add(new Separator());
        hl.Items.Add(Item("Tirar marca-texto", "", ClearHighlight));
        items.Add(hl);
        items.Add(new Separator());

        items.Add(Item("Lista com marcadores", "", () => Format(() => EditingCommands.ToggleBullets.Execute(null, Editor)), "Ctrl+Shift+L", enabled: edit));
        items.Add(Item("Caixa de tarefa ☐", "", () => Format(ToggleCheckbox), "Ctrl+Shift+C", enabled: edit));
        items.Add(Item("Tabela...", null, () => Table_Click(this, new RoutedEventArgs()), enabled: edit));
        var div = new MenuItem { Header = "Linha divisória", IsEnabled = edit };
        div.Items.Add(Item("Linha fina", null, () => Format(() => RichEdit.InsertDivider(Editor, DividerKind.Thin)), "---"));
        div.Items.Add(Item("Linha dupla", null, () => Format(() => RichEdit.InsertDivider(Editor, DividerKind.Double)), "==="));
        div.Items.Add(Item("Linha pontilhada", null, () => Format(() => RichEdit.InsertDivider(Editor, DividerKind.Dotted)), "***"));
        items.Add(div);
        return f;
    }

    static MenuItem WithIcon(MenuItem item, UIElement icon)
    {
        item.Icon = icon;
        return item;
    }

    static TextBlock Letter(string text, double size, bool bold = false, bool italic = false,
                            TextDecorationCollection? deco = null, FontFamily? family = null)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        };
        if (italic) t.FontFamily = new FontFamily("Georgia");
        if (family != null) t.FontFamily = family;
        if (deco != null) t.TextDecorations = deco;
        return t;
    }
}
