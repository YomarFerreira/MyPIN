using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using MyPIN.Models;
using MyPIN.Services;

namespace MyPIN;

public partial class ManagerWindow : Window
{
    static readonly CompareInfo Compare = new CultureInfo("pt-BR").CompareInfo;

    public enum SideKind { All, Notebook, Trash }

    public sealed record SideItem(SideKind Kind, Notebook? Notebook, string Name, int Count, Brush? Dot, string Glyph, bool HiddenNb)
    {
        public Visibility DotVisibility => Dot != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HiddenVisibility => HiddenNb ? Visibility.Visible : Visibility.Collapsed;
        public string Key => Kind == SideKind.Notebook ? Notebook!.Id.ToString() : Kind.ToString();
    }

    public sealed record NoteItem(Note Note, string Title, string Preview, string Meta, Brush BodyBrush, Brush HeaderBrush);

    bool _refreshing;

    SideItem? CurrentSide => SideList.SelectedItem as SideItem;
    bool TrashMode => CurrentSide?.Kind == SideKind.Trash;
    Notebook? CurrentNotebook => CurrentSide?.Notebook;

    public ManagerWindow()
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        App.Store.Changed += Refresh;
        Closed += (_, _) => App.Store.Changed -= Refresh;
        Activated += (_, _) => Refresh();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
            else if (e.Key == Key.Escape && SearchBox.Text.Length > 0) { SearchBox.Clear(); e.Handled = true; }
        };
        Refresh();
    }

    // =====================================================================
    // Atualização das listas
    // =====================================================================

    /// <summary>Seleciona a "Lixeira" na coluna da esquerda.</summary>
    public void ShowTrash()
    {
        SearchBox.Clear();
        SideList.SelectedItem = SideList.Items.Cast<SideItem>().First(i => i.Kind == SideKind.Trash);
    }

    void Refresh()
    {
        RefreshSide();
        RefreshList();
    }

    void RefreshSide()
    {
        var selectedKey = CurrentSide?.Key ?? SideKind.All.ToString();
        var notes = App.Store.Notes;

        var items = new List<SideItem>
        {
            new(SideKind.All, null, "Todas as notas", notes.Count(n => !n.IsDeleted), null, "", false),
        };
        foreach (var nb in App.Store.OrderedNotebooks)
        {
            int count = notes.Count(n => !n.IsDeleted && App.Store.GetNotebook(n) == nb);
            items.Add(new(SideKind.Notebook, nb, nb.Name, count, NoteColors.Get(nb.Color).HeaderBrush, "", nb.Hidden));
        }
        items.Add(new(SideKind.Trash, null, "Lixeira", notes.Count(n => n.IsDeleted), null, "", false));

        _refreshing = true;
        SideList.ItemsSource = items;
        SideList.SelectedItem = items.FirstOrDefault(i => i.Key == selectedKey) ?? items[0];
        _refreshing = false;
    }

    void RefreshList()
    {
        var selectedIds = List.SelectedItems.Cast<NoteItem>().Select(i => i.Note.Id).ToHashSet();
        var query = SearchBox.Text.Trim();
        var side = CurrentSide;

        IEnumerable<Note> notes = App.Store.Notes.Where(n => n.IsDeleted == TrashMode);
        if (side?.Kind == SideKind.Notebook)
            notes = notes.Where(n => App.Store.GetNotebook(n) == side.Notebook);

        var items = notes
            .Where(n => query.Length == 0 || Matches(n, query))
            .OrderByDescending(n => TrashMode ? n.DeletedAt ?? n.Modified : n.Modified)
            .Select(ToItem)
            .ToList();

        List.ItemsSource = items;
        foreach (var item in items.Where(i => selectedIds.Contains(i.Note.Id)))
            List.SelectedItems.Add(item);

        NotesButtons.Visibility = TrashMode ? Visibility.Collapsed : Visibility.Visible;
        TrashButtons.Visibility = TrashMode ? Visibility.Visible : Visibility.Collapsed;

        int total = side?.Count ?? 0;
        StatusText.Text = side?.Kind switch
        {
            SideKind.Trash => App.Store.Settings.TrashRetentionDays <= 0
                ? $"{total} nota(s) na lixeira · ficam guardadas até você apagar (prazo: ícone da bandeja → Lixeira)"
                : $"{total} nota(s) na lixeira · apagadas automaticamente após {NoteStore.RetentionLabel(App.Store.Settings.TrashRetentionDays)} (prazo: ícone da bandeja → Lixeira)",
            SideKind.Notebook when side.Notebook!.Hidden => $"{total} nota(s) · caderno oculto da tela (botão direito para mostrar)",
            _ => $"{total} nota(s)",
        };

        EmptyText.Text = query.Length > 0 ? "Nenhuma nota encontrada."
            : TrashMode ? "A lixeira está vazia."
            : "Nenhuma nota aqui ainda.";
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static bool Matches(Note n, string query)
    {
        const CompareOptions opts = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        return Compare.IndexOf(n.Title, query, opts) >= 0 || Compare.IndexOf(n.PlainText, query, opts) >= 0;
    }

    NoteItem ToItem(Note n)
    {
        var color = NoteColors.Get(n.Color);
        var lines = n.PlainText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (string.IsNullOrWhiteSpace(n.Title) && lines.Count > 0) lines.RemoveAt(0); // 1ª linha já é o título
        var preview = string.Join("  ·  ", lines.Take(6));

        var parts = new List<string>();
        if (n.IsDeleted)
        {
            parts.Add($"excluída {Friendly(n.DeletedAt ?? n.Modified)}");
        }
        else
        {
            if (CurrentSide?.Kind == SideKind.All) parts.Add(App.Store.GetNotebook(n).Name);
            if (n.Locked) parts.Add("🔒");
            if (n.ReminderAt is DateTime r) parts.Add($"⏰ {r:dd/MM HH:mm}" + (n.ReminderRepeat != ReminderRepeat.None ? " 🔁" : ""));
            if (n.SnoozeAt is DateTime sz) parts.Add($"adiado p/ {sz:HH:mm}");
            if (!n.IsOpen) parts.Add("oculta");
            parts.Add(Friendly(n.Modified));
        }
        return new NoteItem(n, n.DisplayTitle, preview, string.Join("  ·  ", parts), color.BodyBrush, color.HeaderBrush);
    }

    static string Friendly(DateTime d)
    {
        if (d.Date == DateTime.Today) return $"hoje {d:HH:mm}";
        if (d.Date == DateTime.Today.AddDays(-1)) return $"ontem {d:HH:mm}";
        return d.Year == DateTime.Today.Year ? d.ToString("dd/MM HH:mm") : d.ToString("dd/MM/yyyy");
    }

    List<Note> Selected() => List.SelectedItems.Cast<NoteItem>().Select(i => i.Note).ToList();

    // =====================================================================
    // Cadernos
    // =====================================================================

    void SideList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        List.SelectedItems.Clear();
        RefreshList();
    }

    void SideList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CurrentNotebook is Notebook nb) EditNotebook(nb);
    }

    void SideList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = SideList.ContextMenu!;
        menu.Items.Clear();
        if (CurrentNotebook is not Notebook nb) { e.Handled = true; return; }

        menu.Items.Add(MenuItem("Nova nota neste caderno", "", () => App.Current.CreateNote(notebook: nb)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Editar nome e cor...", "", () => EditNotebook(nb)));
        menu.Items.Add(MenuItem(nb.Hidden ? "Mostrar notas na tela" : "Ocultar notas da tela",
            nb.Hidden ? "" : "", () => App.Current.SetNotebookHidden(nb, !nb.Hidden)));
        if (!nb.IsDefault)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuItem("Excluir caderno", "", () => DeleteNotebook(nb)));
        }
    }

    void NewNotebook_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new NotebookDialog(null) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var nb = new Notebook { Name = dlg.NotebookName, Color = dlg.NotebookColor };
        App.Store.Notebooks.Add(nb);
        App.Store.MarkDirty();
        Refresh();
        SideList.SelectedItem = SideList.Items.Cast<SideItem>().FirstOrDefault(i => i.Notebook == nb);
    }

    void EditNotebook(Notebook nb)
    {
        var dlg = new NotebookDialog(nb) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        nb.Name = dlg.NotebookName;
        nb.Color = dlg.NotebookColor;
        App.Store.MarkDirty();
        App.Store.NotifyChanged();
    }

    void DeleteNotebook(Notebook nb)
    {
        int count = App.Store.Notes.Count(n => App.Store.GetNotebook(n) == nb);
        var msg = count == 0
            ? $"Excluir o caderno \"{nb.Name}\"?"
            : $"Excluir o caderno \"{nb.Name}\"?\n\nSuas {count} nota(s) não serão apagadas: elas vão para \"{App.Store.DefaultNotebook.Name}\".";
        if (MessageBox.Show(this, msg, "MyPIN", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (nb.Hidden) App.Current.SetNotebookHidden(nb, false); // as notas voltam a aparecer no caderno padrão
        App.Store.DeleteNotebook(nb);
    }

    // =====================================================================
    // Notas
    // =====================================================================

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshList();
    }

    void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is not NoteItem item) return;
        if (TrashMode) App.Current.RestoreNote(item.Note);
        else App.Current.OpenNote(item.Note);
    }

    void List_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { if (TrashMode) Purge_Click(sender, e); else Delete_Click(sender, e); }
        else if (e.Key == Key.Enter) { if (TrashMode) Restore_Click(sender, e); else Open_Click(sender, e); }
    }

    void List_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = List.ContextMenu!;
        menu.Items.Clear();
        if (List.SelectedItems.Count == 0) { e.Handled = true; return; }

        if (TrashMode)
        {
            menu.Items.Add(MenuItem("Restaurar", "", () => Restore_Click(this, new RoutedEventArgs())));
            menu.Items.Add(MenuItem("Excluir definitivamente", "", () => Purge_Click(this, new RoutedEventArgs())));
            return;
        }
        menu.Items.Add(MenuItem("Abrir", "", () => Open_Click(this, new RoutedEventArgs())));
        menu.Items.Add(MenuItem("Ocultar", "", () => Hide_Click(this, new RoutedEventArgs())));
        menu.Items.Add(BuildMoveMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Excluir", "", () => Delete_Click(this, new RoutedEventArgs())));
    }

    MenuItem BuildMoveMenu()
    {
        var move = new MenuItem { Header = "Mover para caderno", Icon = Glyph("") };
        foreach (var it in MoveItems()) move.Items.Add(it);
        return move;
    }

    List<MenuItem> MoveItems()
    {
        var notes = Selected();
        var result = new List<MenuItem>();
        foreach (var nb in App.Store.OrderedNotebooks)
        {
            var target = nb;
            var it = MenuItem(nb.Name, null, () => { foreach (var n in notes) App.Current.MoveNote(n, target); });
            it.Icon = new System.Windows.Shapes.Ellipse { Width = 12, Height = 12, Fill = NoteColors.Get(nb.Color).HeaderBrush };
            it.IsChecked = notes.Count > 0 && notes.All(n => App.Store.GetNotebook(n) == nb);
            result.Add(it);
        }
        return result;
    }

    void Move_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItems.Count == 0) return;
        var menu = new ContextMenu { PlacementTarget = MoveButton, Placement = PlacementMode.Top };
        foreach (var it in MoveItems()) menu.Items.Add(it);
        menu.IsOpen = true;
    }

    void New_Click(object sender, RoutedEventArgs e) =>
        App.Current.CreateNote(notebook: CurrentNotebook);

    void Open_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in Selected()) App.Current.OpenNote(n);
    }

    void Hide_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in Selected()) App.Current.HideNote(n);
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in Selected()) App.Current.DeleteNote(n);
    }

    void Restore_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in Selected()) App.Current.RestoreNote(n);
    }

    void Purge_Click(object sender, RoutedEventArgs e)
    {
        var notes = Selected();
        if (notes.Count == 0) return;
        if (MessageBox.Show(this, $"Apagar definitivamente {notes.Count} nota(s)? Isso não pode ser desfeito.",
                "MyPIN", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var n in notes) App.Current.PurgeNote(n);
    }

    void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Store.Notes.Any(n => n.IsDeleted)) return;
        if (MessageBox.Show(this, "Esvaziar a lixeira? Isso não pode ser desfeito.",
                "MyPIN", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        App.Current.EmptyTrash();
    }

    // =====================================================================
    // "Mais": abrir todas, backup
    // =====================================================================

    void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Top };
        menu.Items.Add(MenuItem("Abrir todas as notas", "", () =>
        {
            foreach (var n in App.Store.Notes.Where(n => !n.IsDeleted).ToList()) App.Current.OpenNote(n, activate: false);
        }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Exportar backup...", "", ExportBackup));
        menu.Items.Add(MenuItem("Importar backup...", "", ImportBackup));
        menu.Items.Add(MenuItem("Abrir pasta de dados", "", () =>
            Process.Start(new ProcessStartInfo("explorer.exe", NoteStore.DataDir) { UseShellExecute = true })));
        menu.IsOpen = true;
    }

    static MenuItem MenuItem(string header, string? glyph, Action action)
    {
        var item = new MenuItem { Header = header };
        if (glyph != null) item.Icon = Glyph(glyph);
        item.Click += (_, e) =>
        {
            if (e.OriginalSource != item) return;
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

    void ExportBackup()
    {
        foreach (Window w in Application.Current.Windows)
            if (w is NoteWindow nw) nw.FlushContent();

        var dlg = new SaveFileDialog
        {
            FileName = $"MyPIN-backup-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "Backup do MyPIN (*.json)|*.json",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            App.Store.Export(dlg.FileName);
            MessageBox.Show(this, "Backup salvo com sucesso.", "MyPIN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível salvar o backup:\n" + ex.Message, "MyPIN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void ImportBackup()
    {
        var dlg = new OpenFileDialog { Filter = "Backup do MyPIN (*.json)|*.json" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            int count = App.Store.Import(dlg.FileName);
            MessageBox.Show(this, $"{count} nota(s) importada(s). Elas aparecem aqui na lista (ocultas).",
                "MyPIN", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível importar:\n" + ex.Message, "MyPIN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
