using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace MyPIN;

/// <summary>Tamanho (A− / A+) e tipo de letra do trecho selecionado.</summary>
public partial class NoteWindow
{
    // ---------- Tamanho da letra ----------

    /// <summary>Os passos do A− / A+ (em pontos de tela, como o tamanho padrão 14 da nota).</summary>
    static readonly double[] FontSteps = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72 };

    static double NextSize(double size, int dir)
    {
        if (dir > 0) return FontSteps.FirstOrDefault(s => s > size + 0.01, FontSteps[^1]);
        return FontSteps.LastOrDefault(s => s < size - 0.01, FontSteps[0]);
    }

    void FontBigger_Click(object sender, RoutedEventArgs e) => Format(() => StepFontSize(+1));
    void FontSmaller_Click(object sender, RoutedEventArgs e) => Format(() => StepFontSize(-1));

    /// <summary>
    /// Aumenta/diminui um passo. Sem seleção, vale para o que for digitado em seguida.
    /// Com tamanhos misturados na seleção, cada pedaço anda um passo a partir do seu próprio tamanho
    /// (o título grande continua maior que o resto).
    /// </summary>
    void StepFontSize(int dir)
    {
        var sel = Editor.Selection;
        if (sel.GetPropertyValue(TextElement.FontSizeProperty) is double size)
        {
            sel.ApplyPropertyValue(TextElement.FontSizeProperty, NextSize(size, dir));
            return;
        }

        // tamanhos misturados: um pedaço por vez (dentro de um só passo de desfazer)
        var pieces = new List<(TextPointer Start, TextPointer End, double Size)>();
        for (var nav = sel.Start; nav != null && nav.CompareTo(sel.End) < 0;
             nav = nav.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (nav.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var end = nav.GetNextContextPosition(LogicalDirection.Forward);
            if (end == null) break;
            if (end.CompareTo(sel.End) > 0) end = sel.End;
            var r = new TextRange(nav, end);
            if (r.GetPropertyValue(TextElement.FontSizeProperty) is double s) pieces.Add((nav, end, s));
        }
        Editor.BeginChange();
        try
        {
            foreach (var (start, end, s) in pieces)
                new TextRange(start, end).ApplyPropertyValue(TextElement.FontSizeProperty, NextSize(s, dir));
        }
        finally { Editor.EndChange(); }
    }

    // ---------- Tipo de letra ----------

    /// <summary>
    /// Fontes do Windows bem diferentes entre si (uma de cada "jeito"). Só aparecem as que existem
    /// no computador.
    /// </summary>
    static readonly (string Family, string Hint)[] FontChoices =
    {
        ("Arial", "reta e limpa"),
        ("Times New Roman", "com serifa, de livro"),
        ("Comic Sans MS", "informal"),
        ("Courier New", "máquina de escrever"),
        ("Segoe Print", "letra de mão"),
        ("Bahnschrift", "estreita, de placa"),
    };

    static List<(string Family, string Hint)>? _installedFonts;

    static List<(string Family, string Hint)> InstalledFonts => _installedFonts ??=
        FontChoices.Where(f => Fonts.SystemFontFamilies.Any(s => string.Equals(s.Source, f.Family, StringComparison.OrdinalIgnoreCase)))
                   .ToList();

    void FontFamily_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        var menu = new ContextMenu();
        AddFontItems(menu.Items);
        menu.PlacementTarget = FontFamilyButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>Itens da lista de fontes (cada nome escrito na própria fonte).</summary>
    void AddFontItems(ItemCollection target)
    {
        var res = Application.Current.Resources;
        var muted = (Brush)res["Menu.Shortcut"];
        var current = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty) as FontFamily;

        void Add(string label, FontFamily family, string hint)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = label, FontFamily = family, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(new TextBlock { Text = "  " + hint, FontSize = 11, Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
            var item = new MenuItem
            {
                Header = header,
                IsCheckable = false,
                IsChecked = current != null && string.Equals(current.Source, family.Source, StringComparison.OrdinalIgnoreCase),
            };
            System.Windows.Automation.AutomationProperties.SetName(item, "Fonte " + label);
            item.Click += (_, _) => Format(() => ApplyFontFamily(family));
            target.Add(item);
        }

        Add("Padrão", Editor.FontFamily, "(" + Editor.FontFamily.Source.Split(',')[0].Trim() + ")");
        target.Add(new Separator());
        foreach (var (family, hint) in InstalledFonts)
            Add(family, new FontFamily(family), hint);
    }

    /// <summary>Troca a fonte do trecho; as caixinhas ☐/☑ continuam na fonte delas.</summary>
    void ApplyFontFamily(FontFamily family)
    {
        Editor.BeginChange();
        try
        {
            Editor.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, family);
            NormalizeCheckboxes();
        }
        finally { Editor.EndChange(); }
    }
}
