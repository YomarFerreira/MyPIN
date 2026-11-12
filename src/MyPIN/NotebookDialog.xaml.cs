using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using MyPIN.Models;

namespace MyPIN;

public partial class NotebookDialog : Window
{
    public string NotebookName { get; private set; } = "";
    public string NotebookColor { get; private set; } = "Yellow";

    readonly Notebook? _editing;

    /// <param name="editing">Caderno a editar, ou null para criar um novo.</param>
    public NotebookDialog(Notebook? editing)
    {
        InitializeComponent();
        _editing = editing;
        Title = editing == null ? "Novo caderno" : "Editar caderno";
        NameBox.Text = editing?.Name ?? "";
        NotebookColor = editing?.Color ?? "Yellow";

        foreach (var c in NoteColors.All)
        {
            var toggle = new ToggleButton
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 8, 8),
                Tag = c.Key,
                ToolTip = c.Name,
                Cursor = System.Windows.Input.Cursors.Hand,
                IsChecked = c.Key == NotebookColor,
                Template = SwatchTemplate(c),
            };
            toggle.Click += (_, _) =>
            {
                NotebookColor = (string)toggle.Tag;
                foreach (ToggleButton t in ColorPanel.Children) t.IsChecked = t == toggle;
            };
            ColorPanel.Children.Add(toggle);
        }

        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    static ControlTemplate SwatchTemplate(NoteColor c)
    {
        // Quadradinho da cor; quando selecionado ganha borda azul grossa
        var t = new ControlTemplate(typeof(ToggleButton));
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.BackgroundProperty, c.BodyBrush);
        border.SetValue(Border.BorderBrushProperty, c.HeaderBrush);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(3));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        t.VisualTree = border;
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)), "Bd"));
        t.Triggers.Add(checkedTrigger);
        return t;
    }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ShowError("Digite um nome para o caderno.");
            return;
        }
        bool duplicate = App.Store.Notebooks.Any(n => n != _editing && string.Equals(n.Name, name, StringComparison.CurrentCultureIgnoreCase));
        if (duplicate)
        {
            ShowError("Já existe um caderno com esse nome.");
            return;
        }
        NotebookName = name;
        DialogResult = true;
    }

    void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
