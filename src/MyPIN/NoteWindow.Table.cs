using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using MyPIN.Services;

namespace MyPIN;

/// <summary>Tabelas, linhas divisórias, colar do Excel e o botão Desfazer.</summary>
public partial class NoteWindow
{
    const int PickerMax = 4; // grade do seletor: 4×4 (tamanhos maiores: digitando)

    // Limite das tabelas digitadas. Colunas: cada uma precisa de ~24 px para ser usável, e 10 colunas
    // ainda cabem numa nota normal. Linhas: o editor do Windows fica lento para desenhar, rolar e
    // redimensionar com muitas centenas de células; 500 células continua leve.
    const int MaxTableRows = 50;
    const int MaxTableCols = 10;

    void InitRichEdit()
    {
        DataObject.AddPastingHandler(Editor, Editor_Pasting);
        Editor.PreviewTextInput += Editor_PreviewTextInput;
        Editor.PreviewMouseMove += Resize_PreviewMouseMove;
        Editor.PreviewMouseLeftButtonDown += Resize_PreviewMouseLeftButtonDown;
        Editor.LostMouseCapture += (_, _) => EndResize();
        Editor.MouseLeave += (_, _) => { if (!_resizing) SetResizeCursor(null); };
        MouseLeave += (_, _) => HideTableButtons();
        // segurança: nunca deixar um arrasto "preso"
        PreviewMouseLeftButtonUp += (_, _) => EndResize();
        Deactivated += (_, _) => EndResize();
        // marcas do Localizar acompanham rolagem, tamanho e edição do texto
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => DrawFindMarks()));
        Editor.SizeChanged += (_, _) => DrawFindMarks();
        Editor.TextChanged += (_, _) =>
        {
            if (FindBar.Visibility == Visibility.Visible)
                Dispatcher.BeginInvoke(DrawFindMarks, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        // medidas das tabelas ficam velhas quando algo muda
        Editor.TextChanged += (_, _) => InvalidateTableLayouts();
        Editor.SizeChanged += (_, _) => InvalidateTableLayouts();
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) => InvalidateTableLayouts()));
        // depois do clique a tabela muda de tamanho: esconde os "+" na hora (senão ficam em cima da
        // tabela, no lugar antigo) e refaz as medidas; ao voltar à borda eles reaparecem no lugar certo
        AddRowButton.Click += (_, _) =>
        {
            if (_plusTable != null) Format(() => RichEdit.AddRowAtEnd(Editor, _plusTable));
            HideTableButtons(force: true);
            InvalidateTableLayouts();
        };
        TableMinButton.Click += (_, _) => TableMin_Click();
        TableRestoreButton.Click += (_, _) => TableRestore_Click();
        TableCloseButton.Click += (_, _) => TableClose_Click();
        TableConfirmYes.Click += (_, _) => ConfirmDeleteTable();
        // a faixa de confirmação some com Esc, ao digitar, rolar, clicar no texto ou sair da nota
        PreviewKeyDown += (_, e) =>
        {
            if (_confirmTable == null) return;
            HideTableConfirm();
            if (e.Key == Key.Escape) e.Handled = true;
        };
        Editor.TextChanged += (_, _) => HideTableConfirm();
        // clicar em qualquer lugar fora da faixa cancela
        PreviewMouseDown += (_, e) =>
        {
            if (_confirmTable != null && !TableConfirmBar.IsMouseOver) HideTableConfirm();
        };
        Editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            // só rolagem de verdade (mudar o tamanho da área, p.ex. ao aparecer o rodapé, não conta)
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) HideTableConfirm();
        }));
        Deactivated += (_, _) => HideTableConfirm();
        AddColButton.Click += (_, _) =>
        {
            if (_plusTable == null) return;
            Format(() => RichEdit.AddColumnAtEnd(Editor, _plusTable));
            FitTableToNote(_plusTable);
            HideTableButtons(force: true);
            InvalidateTableLayouts();
        };
    }

    /// <summary>Largura útil do texto da nota (para a tabela não passar dela).</summary>
    double AvailableTextWidth() =>
        Editor.ViewportWidth > 0 ? Editor.ViewportWidth - 4 : Math.Max(120, ActualWidth - 16 - 2 - 12 - 20);

    void FitTableToNote(Table t)
    {
        Editor.UpdateLayout();
        RichEdit.FitTables(Editor.Document, AvailableTextWidth());
    }

    // ---------- Desfazer ----------

    void Undo_Click(object sender, RoutedEventArgs e) =>
        Format(() => ApplicationCommands.Undo.Execute(null, Editor));

    void Redo_Click(object sender, RoutedEventArgs e) =>
        Format(() => ApplicationCommands.Redo.Execute(null, Editor));

    // ---------- Linha divisória ----------

    void Divider_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var (kind, label, data, dashed) in new[]
                 {
                     (DividerKind.Thin, "Linha fina", "M 0,4 H 40", false),
                     (DividerKind.Double, "Linha dupla", "M 0,2.5 H 40 M 0,5.5 H 40", false),
                     (DividerKind.Dotted, "Linha pontilhada", "M 0,4 H 40", true),
                 })
        {
            var k = kind;
            var preview = new Path
            {
                Data = Geometry.Parse(data),
                Stroke = (Brush)Application.Current.Resources["Menu.Foreground"],
                StrokeThickness = 1,
                Width = 40,
                Height = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (dashed) preview.StrokeDashArray = new DoubleCollection { 2, 2 };
            var item = new MenuItem { Header = label, Icon = preview };
            item.Click += (_, _) => Format(() => RichEdit.InsertDivider(Editor, k));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Dica: digite --- === ou ***", IsEnabled = false });
        menu.PlacementTarget = DividerButton;
        menu.Placement = PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>Ao digitar o 3º "-", "_", "=" ou "*" numa linha vazia, ela vira divisória na hora.</summary>
    void Editor_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!CanEdit || e.Text.Length != 1 || "-_=*".IndexOf(e.Text[0]) < 0 || !Editor.Selection.IsEmpty) return;
        var para = Editor.CaretPosition.Paragraph;
        if (para == null || para.Parent is TableCell) return;
        var before = new TextRange(para.ContentStart, para.ContentEnd).Text;
        if (before != new string(e.Text[0], 2)) return;
        if (new TextRange(Editor.CaretPosition, para.ContentEnd).Text.Length > 0) return;
        // deixa o caractere entrar (fica como uma etapa de desfazer) e depois converte (outra etapa):
        // assim Ctrl+Z volta para "---"
        Dispatcher.BeginInvoke(() => RichEdit.TryConvertDividerOnEnter(Editor), DispatcherPriority.Background);
    }

    /// <summary>Enter: sai da tabela no fim dela; "---" + Enter vira divisória.</summary>
    bool HandleEnter() =>
        CanEdit && Editor.IsKeyboardFocusWithin &&
        (RichEdit.TryExitTableOnEnter(Editor) || RichEdit.TryConvertDividerOnEnter(Editor));

    /// <summary>Del/Backspace junto de uma divisória apaga a divisória inteira.</summary>
    bool HandleDeleteDivider(bool forward) =>
        CanEdit && Editor.IsKeyboardFocusWithin && RichEdit.TryDeleteDivider(Editor, forward);

    // ---------- Tabela: seletor de tamanho (tipo Word) ----------

    void Table_Click(object sender, RoutedEventArgs e)
    {
        if (!CanEdit) return;
        var res = Application.Current.Resources;
        var bg = (Brush)res["Menu.Background"];
        var fg = (Brush)res["Menu.Foreground"];
        var line = (Brush)res["Menu.Border"];
        var hover = (Brush)res["Menu.Hover"];
        var accent = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        var accentSoft = new SolidColorBrush(Color.FromArgb(0x55, 0x3B, 0x82, 0xF6));

        var muted = (Brush)res["Menu.Shortcut"];

        var title = new TextBlock { Text = "Inserir tabela", FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = fg };
        var size = new TextBlock
        {
            Text = "passe o mouse ou digite abaixo", FontSize = 12, Foreground = muted,
            Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
        };
        var grid = new UniformGrid { Rows = PickerMax, Columns = PickerMax, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        var cells = new Border[PickerMax, PickerMax];
        var popup = new Popup
        {
            // botão escondido (ou aberto pelo menu ⋯): aparece no meio da nota
            PlacementTarget = TableButton.IsVisible ? TableButton : Editor,
            Placement = TableButton.IsVisible ? PlacementMode.Top : PlacementMode.Center,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
        };

        // campos: linhas × colunas
        TextBox NumberBox() => new()
        {
            Width = 38, Height = 24, Padding = new Thickness(2, 0, 2, 0), FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Background = hover, Foreground = fg, BorderBrush = line, CaretBrush = fg, MaxLength = 3,
        };
        var rowsBox = NumberBox();
        var colsBox = NumberBox();
        var insert = new Button { Content = "Inserir", Style = (Style)res["MenuMiniButton"], Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 0, 10, 0) };
        var error = new TextBlock
        {
            FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed, MaxWidth = 190,
        };
        TextBlock Label(string t) => new() { Text = t, FontSize = 12, Foreground = muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var inputs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        inputs.Children.Add(Label("Linhas"));
        inputs.Children.Add(rowsBox);
        inputs.Children.Add(new TextBlock { Text = "×", Foreground = muted, Margin = new Thickness(6, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        inputs.Children.Add(Label("Colunas"));
        inputs.Children.Add(colsBox);
        inputs.Children.Add(insert);

        void Insert(int rows, int cols)
        {
            popup.IsOpen = false;
            Format(() => RichEdit.InsertTable(Editor, RichEdit.CreateTable(rows, cols, width: Math.Min(AvailableTextWidth(), 64 * cols))));
        }

        void Highlight(int rows, int cols)
        {
            for (int r = 0; r < PickerMax; r++)
                for (int c = 0; c < PickerMax; c++)
                {
                    bool on = r < rows && c < cols;
                    cells[r, c].Background = on ? accentSoft : hover;
                    cells[r, c].BorderBrush = on ? accent : line;
                }
            size.Text = $"{rows} {(rows == 1 ? "linha" : "linhas")} × {cols} {(cols == 1 ? "coluna" : "colunas")}";
            size.Foreground = fg;
            rowsBox.Text = rows.ToString();
            colsBox.Text = cols.ToString();
            error.Visibility = Visibility.Collapsed;
        }

        void InsertTyped()
        {
            bool okR = int.TryParse(rowsBox.Text.Trim(), out int r) && r >= 1;
            bool okC = int.TryParse(colsBox.Text.Trim(), out int c) && c >= 1;
            if (!okR || !okC || r > MaxTableRows || c > MaxTableCols)
            {
                error.Text = $"Use de 1 a {MaxTableRows} linhas e de 1 a {MaxTableCols} colunas.";
                error.Visibility = Visibility.Visible;
                return;
            }
            Insert(r, c);
        }

        insert.Click += (_, _) => InsertTyped();
        foreach (var box in new[] { rowsBox, colsBox })
        {
            box.PreviewTextInput += (_, ev) => ev.Handled = !ev.Text.All(char.IsDigit); // só números
            box.KeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter) { InsertTyped(); ev.Handled = true; }
                else if (ev.Key == Key.Escape) { popup.IsOpen = false; ev.Handled = true; }
            };
            box.GotKeyboardFocus += (_, _) => box.SelectAll();
        }

        for (int r = 0; r < PickerMax; r++)
            for (int c = 0; c < PickerMax; c++)
            {
                int rows = r + 1, cols = c + 1;
                var cell = new Border
                {
                    Width = 22, Height = 22, Margin = new Thickness(2), CornerRadius = new CornerRadius(3),
                    BorderBrush = line, BorderThickness = new Thickness(1), Background = hover, Cursor = Cursors.Hand,
                };
                cell.MouseEnter += (_, _) => Highlight(rows, cols);
                cell.MouseLeftButtonUp += (_, _) => Insert(rows, cols);
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }

        var panel = new StackPanel();
        panel.Children.Add(title);
        panel.Children.Add(grid);
        panel.Children.Add(size);
        panel.Children.Add(inputs);
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock
        {
            Text = $"máximo {MaxTableRows} linhas × {MaxTableCols} colunas", FontSize = 11, Foreground = muted,
            Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
        });

        // margem transparente em volta para a sombra
        popup.Child = new Border
        {
            Margin = new Thickness(10, 10, 10, 6),
            Background = bg,
            BorderBrush = line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Direction = 270, Opacity = 0.3 },
            Child = panel,
        };
        // o botão da barra usa a fonte de ícones e o popup herdava dela (texto virava "□□□"):
        // aqui o texto precisa da fonte normal
        TextElement.SetFontFamily(popup.Child, new FontFamily("Segoe UI"));
        popup.IsOpen = true;
    }

    // ---------- Colar células do Excel / Google Planilhas ----------

    void Editor_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!CanEdit) return;
        try
        {
            var data = e.DataObject;
            // Excel coloca "XML Spreadsheet"/"Csv" junto; Google Planilhas manda HTML com <table>
            bool fromSheet = data.GetDataPresent("XML Spreadsheet") || data.GetDataPresent(DataFormats.CommaSeparatedValue) ||
                             (data.GetDataPresent(DataFormats.Html) && (data.GetData(DataFormats.Html) as string ?? "")
                                 .Contains("<table", StringComparison.OrdinalIgnoreCase));
            if (!fromSheet || data.GetData(DataFormats.UnicodeText) is not string text || !text.Contains('\t') && !text.Contains('\n'))
            {
                // Outros programas (ex.: WordPad) mandam a tabela só em RTF: confere o tamanho também
                if (data.GetData(DataFormats.Rtf) is string rtf && RichEdit.RtfTableSize(rtf) is var (rr, rc) && rr > 0)
                {
                    if (rr > MaxTableRows || rc > MaxTableCols) { e.CancelCommand(); WarnTableTooBig(rr, rc); return; }
                    // dentro do limite: deixa colar e depois aplica o estilo das tabelas da nota
                    Dispatcher.BeginInvoke(() =>
                    {
                        RichEdit.Normalize(Editor.Document);
                        RichEdit.FitTables(Editor.Document, AvailableTextWidth());
                    }, DispatcherPriority.Background);
                }
                return;
            }

            var rows = RichEdit.ParseTsv(text);
            while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            if (rows.Count == 0 || rows.Count == 1 && cols <= 1) return; // uma célula só: cola como texto normal

            e.CancelCommand();
            if (rows.Count > MaxTableRows || cols > MaxTableCols)
            {
                WarnTableTooBig(rows.Count, cols); // grande demais: avisa e não cola nada
                return;
            }
            var table = RichEdit.CreateTable(rows.Count, cols, rows.Select(r => (IReadOnlyList<string>)r).ToList(), AvailableTextWidth());
            RichEdit.InsertTable(Editor, table);
        }
        catch (Exception ex)
        {
            Log.Error(ex); // se algo der errado, deixa colar do jeito normal
        }
    }

    /// <summary>
    /// Ícones de tabela desenhados em vetor (16×16): a fonte de ícones do Windows não tem ícones de
    /// tabela. Mini-tabela + "+" no lado onde entra a linha/coluna; "×" para excluir; setas para
    /// mesclar (→|←) e desmesclar (←|→).
    /// </summary>
    static class TableIcons
    {
        public const string RowAbove = "M 2.5,7.5 H 13.5 V 14.5 H 2.5 Z M 2.5,11 H 13.5 M 6.2,7.5 V 14.5 M 9.8,7.5 V 14.5 M 8,1 V 6 M 5.5,3.5 H 10.5";
        public const string RowBelow = "M 2.5,1.5 H 13.5 V 8.5 H 2.5 Z M 2.5,5 H 13.5 M 6.2,1.5 V 8.5 M 9.8,1.5 V 8.5 M 8,10 V 15 M 5.5,12.5 H 10.5";
        public const string ColLeft = "M 7.5,2.5 H 14.5 V 13.5 H 7.5 Z M 11,2.5 V 13.5 M 7.5,6.2 H 14.5 M 7.5,9.8 H 14.5 M 1,8 H 6 M 3.5,5.5 V 10.5";
        public const string ColRight = "M 1.5,2.5 H 8.5 V 13.5 H 1.5 Z M 5,2.5 V 13.5 M 1.5,6.2 H 8.5 M 1.5,9.8 H 8.5 M 10,8 H 15 M 12.5,5.5 V 10.5";
        public const string DeleteRow = "M 1.5,3.5 H 10.5 V 12.5 H 1.5 Z M 1.5,6.5 H 10.5 M 1.5,9.5 H 10.5 M 12,6.5 L 15,9.5 M 15,6.5 L 12,9.5";
        public const string DeleteCol = "M 3.5,5.5 H 12.5 V 14.5 H 3.5 Z M 6.5,5.5 V 14.5 M 9.5,5.5 V 14.5 M 6.5,1 L 9.5,4 M 9.5,1 L 6.5,4";
        public const string Merge = "M 1.5,4.5 H 14.5 V 11.5 H 1.5 Z M 3,8 H 6.5 M 5,6.5 L 6.5,8 L 5,9.5 M 13,8 H 9.5 M 11,6.5 L 9.5,8 L 11,9.5";
        public const string Split = "M 1.5,4.5 H 14.5 V 11.5 H 1.5 Z M 8,4.5 V 11.5 M 6.5,8 H 3 M 4.5,6.5 L 3,8 L 4.5,9.5 M 9.5,8 H 13 M 11.5,6.5 L 13,8 L 11.5,9.5";
    }

    static FrameworkElement TableIcon(string data)
    {
        var path = new Path
        {
            Data = Geometry.Parse(data),
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        path.SetResourceReference(Shape.StrokeProperty, "Menu.Foreground"); // acompanha o tema claro/escuro
        return new Viewbox { Width = 15, Height = 15, Child = new Canvas { Width = 16, Height = 16, Children = { path } } };
    }

    void WarnTableTooBig(int rows, int cols) =>
        MessageBox.Show(this,
            $"A tabela que você está colando tem {rows} {(rows == 1 ? "linha" : "linhas")} × {cols} {(cols == 1 ? "coluna" : "colunas")}.\n\n" +
            $"O MyPIN aceita tabelas de até {MaxTableRows} linhas × {MaxTableCols} colunas, para a nota continuar leve e rápida.\n\n" +
            "Nada foi colado. Copie um pedaço menor da planilha.",
            "MyPIN — tabela grande demais", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------- Menu do botão direito numa célula ----------

    /// <summary>Acrescenta as opções de tabela no menu do botão direito, se o clique foi numa célula.</summary>
    void AddTableMenuItems(ContextMenu menu, TextPointer? clickPos, Point mouse)
    {
        InvalidateTableLayouts();
        // célula pela posição na tela (confiável); o "texto mais próximo" só como plano B
        var cell = RichEdit.CellAtPoint(TableLayouts(), mouse)
                   ?? RichEdit.CellAt(clickPos) ?? RichEdit.CellNear(Editor.Selection.Start, LogicalDirection.Forward);
        if (cell == null || RichEdit.TableOf(cell) == null) return;
        bool edit = CanEdit;

        MenuItem TableItem(string header, string icon, Action action, bool enabled)
        {
            var it = Item(header, null, action, enabled: enabled);
            it.Icon = TableIcon(icon);
            return it;
        }

        // Tabela minimizada: só restaurar ou excluir (mexer em linhas agora misturaria com as escondidas)
        var cellTable = RichEdit.TableOf(cell)!;
        if (IsCollapsed(cellTable))
        {
            menu.Items.Add(new MenuItem { Header = "Tabela minimizada", IsEnabled = false });
            menu.Items.Add(Item("Restaurar tabela", "", () => { _plusTable = cellTable; TableRestore_Click(); }));
            menu.Items.Add(Item("Excluir tabela", "", () => { _plusTable = cellTable; TableClose_Click(); }, enabled: edit));
            menu.Items.Add(new Separator());
            return;
        }

        menu.Items.Add(TableItem("Inserir linha acima", TableIcons.RowAbove, () => RichEdit.InsertRow(Editor, cell, below: false), edit));
        menu.Items.Add(TableItem("Inserir linha abaixo", TableIcons.RowBelow, () => RichEdit.InsertRow(Editor, cell, below: true), edit));
        menu.Items.Add(TableItem("Inserir coluna à esquerda", TableIcons.ColLeft, () => RichEdit.InsertColumn(Editor, cell, right: false), edit));
        menu.Items.Add(TableItem("Inserir coluna à direita", TableIcons.ColRight, () => RichEdit.InsertColumn(Editor, cell, right: true), edit));
        menu.Items.Add(new Separator());
        menu.Items.Add(TableItem("Excluir linha", TableIcons.DeleteRow, () => RichEdit.DeleteRow(Editor, cell), edit));
        menu.Items.Add(TableItem("Excluir coluna", TableIcons.DeleteCol, () => RichEdit.DeleteColumn(Editor, cell), edit));
        // Mesclar / desmesclar: sempre visíveis; habilitadas só quando dá para usar (com dica do porquê)
        menu.Items.Add(new Separator());
        MenuItem Conditional(string header, string icon, Action action, bool can, string whyNot)
        {
            var it = TableItem(header, icon, action, edit && can);
            if (!can) it.ToolTip = whyNot;
            ToolTipService.SetShowOnDisabled(it, true);
            return it;
        }

        bool canMerge = RichEdit.SelectedRect(Editor) != null; // 2+ células diferentes selecionadas
        menu.Items.Add(Conditional("Mesclar células", TableIcons.Merge, () => RichEdit.MergeSelection(Editor), canMerge,
            "Selecione duas ou mais células para mesclar"));

        var toSplit = RichEdit.MergedCellsToSplit(Editor, cell); // a clicada (se mesclada) e as mescladas da seleção
        menu.Items.Add(Conditional(toSplit.Count > 1 ? $"Desmesclar células ({toSplit.Count})" : "Desmesclar células", TableIcons.Split,
            () => RichEdit.UnmergeAll(Editor, toSplit), toSplit.Count > 0,
            "Clique numa célula mesclada (ou selecione células mescladas) para desmesclar"));

        var table = RichEdit.TableOf(cell)!;
        var allMerged = table.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).Where(RichEdit.CanUnmerge).ToList();
        menu.Items.Add(Conditional(allMerged.Count > 0 ? $"Desmesclar a tabela toda ({allMerged.Count})" : "Desmesclar a tabela toda",
            TableIcons.Split, () => RichEdit.UnmergeAll(Editor, allMerged), allMerged.Count > 0,
            "Esta tabela não tem células mescladas"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Excluir tabela", "", () => RichEdit.DeleteTable(Editor, cell), enabled: edit));
        menu.Items.Add(new Separator());
    }

    // ---------- Posições das bordas: medidas uma vez e reaproveitadas ----------
    //
    // Medir as tabelas a cada movimento do mouse deixava a nota pesada. Agora as medidas ficam
    // guardadas e só são refeitas quando algo muda (texto, tamanho, rolagem, zoom), e no máximo
    // a cada 250 ms.

    List<(Table table, RichEdit.TableLayout layout)>? _layouts;
    DateTime _layoutsAt;

    void InvalidateTableLayouts() => _layouts = null;

    List<(Table table, RichEdit.TableLayout layout)> TableLayouts()
    {
        if (_layouts != null && (DateTime.Now - _layoutsAt).TotalMilliseconds < 250) return _layouts;
        _layouts = RichEdit.AllTables(Editor.Document.Blocks).Any()
            ? RichEdit.MeasureAll(Editor.Document)
            : new List<(Table, RichEdit.TableLayout)>();
        _layoutsAt = DateTime.Now;
        return _layouts;
    }

    // ---------- Minimizar / restaurar / excluir tabela (canto superior direito) ----------
    //
    // Minimizada = só a 1ª linha na tela; as outras ficam guardadas em _collapsed. Ao salvar, a nota
    // grava a tabela COMPLETA (RichEdit.ToRtf) e anota em _note.CollapsedTables quais estavam
    // minimizadas, para minimizar de novo ao reabrir. A entrada continua guardada depois de restaurar:
    // se um Ctrl+Z desfizer o "restaurar", a tabela volta a 1 linha e as linhas guardadas continuam lá.

    readonly Dictionary<Table, RichEdit.CollapsedTable> _collapsed = new();

    bool IsCollapsed(Table t) => _collapsed.ContainsKey(t) && t.RowGroups.Count > 0 && t.RowGroups[0].Rows.Count == 1;

    RichEdit.CollapsedTable? CollapsedOf(Table t) => IsCollapsed(t) ? _collapsed[t] : null;

    /// <summary>Posição (entre as tabelas da nota) das que estão minimizadas — gravado junto com a nota.</summary>
    List<int> CollapsedOrdinals() =>
        RichEdit.AllTables(Editor.Document.Blocks).Select((t, i) => (t, i)).Where(x => IsCollapsed(x.t)).Select(x => x.i).ToList();

    /// <summary>Ao abrir a nota: minimiza de novo as tabelas que estavam minimizadas.</summary>
    void RestoreCollapsedState()
    {
        var tables = RichEdit.AllTables(Editor.Document.Blocks).ToList();
        foreach (var i in _note.CollapsedTables.Where(i => i >= 0 && i < tables.Count))
            if (RichEdit.Collapse(null, tables[i]) is { } info) _collapsed[tables[i]] = info;
    }

    void TableMin_Click()
    {
        if (_plusTable == null || !CanEdit) return;
        Editor.Focus();
        if (RichEdit.Collapse(Editor, _plusTable) is { } info) _collapsed[_plusTable] = info;
        HideTableButtons(force: true);
        InvalidateTableLayouts();
    }

    void TableRestore_Click()
    {
        if (_plusTable == null || !_collapsed.TryGetValue(_plusTable, out var info) || !IsCollapsed(_plusTable)) return;
        Editor.Focus();
        RichEdit.Expand(Editor, _plusTable, info);
        HideTableButtons(force: true);
        InvalidateTableLayouts();
    }

    void TableClose_Click()
    {
        var table = _plusTable;
        if (table == null || !CanEdit) return;
        HideTableButtons(force: true);
        ShowTableConfirm(table);
    }

    // ---------- Confirmação de excluir (faixa baixa acima da tabela, sem janela) ----------

    Table? _confirmTable;

    void ShowTableConfirm(Table table)
    {
        var lay = TableLayouts().FirstOrDefault(x => x.table == table).layout;
        if (lay == null) return;
        _confirmTable = table;
        TableConfirmBar.Visibility = Visibility.Visible;
        TableConfirmBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = TableConfirmBar.DesiredSize;
        var corner = Editor.TranslatePoint(new Point(lay.Right, lay.Top), TableOverlay);
        // alinhada à direita da tabela, logo acima dela; se não couber em cima, encosta no topo
        double x = Math.Clamp(corner.X - size.Width, 0, Math.Max(0, TableOverlay.ActualWidth - size.Width));
        double y = Math.Max(0, corner.Y - size.Height - 2);
        Canvas.SetLeft(TableConfirmBar, x);
        Canvas.SetTop(TableConfirmBar, y);
    }

    void HideTableConfirm()
    {
        if (_confirmTable == null) return;
        _confirmTable = null;
        TableConfirmBar.Visibility = Visibility.Collapsed;
    }

    void ConfirmDeleteTable()
    {
        var table = _confirmTable;
        HideTableConfirm();
        if (table == null || !CanEdit || table.Parent == null) return;
        Editor.Focus();
        RichEdit.DeleteTable(Editor, table.RowGroups[0].Rows[0].Cells[0]);
        _collapsed.Remove(table);
        InvalidateTableLayouts();
    }

    // ---------- Botões "+" (nova linha embaixo / nova coluna à direita) ----------

    Table? _plusTable;

    /// <summary>Distância entre a borda da tabela e o botão "+" (fora do ponto de arrastar a borda).</summary>
    const double PlusGap = 8;

    /// <summary>
    /// O "+" de linha só aparece com o mouse perto da borda de BAIXO da tabela; o de coluna, só perto
    /// da borda da DIREITA. Ficam sempre no MEIO da borda e um pouco para fora, para nunca ficarem em
    /// cima do ponto onde se pega a borda para redimensionar.
    /// </summary>
    void UpdateTableButtons(Point p)
    {
        if (!CanEdit || _resizing || _confirmTable != null) { HideTableButtons(); return; }
        if (RichEdit.TableNear(TableLayouts(), p, 50) is not var (table, lay)) { HideTableButtons(); return; }
        _plusTable = table;

        // acima da tabela, alinhados à direita: "−" e "×" (ou "□" se a tabela estiver minimizada)
        var corner = Editor.TranslatePoint(new Point(lay.Right, lay.Top), TableOverlay);
        double right = Math.Min(corner.X, TableOverlay.ActualWidth);
        double cy = Math.Max(0, corner.Y - TableMinButton.Height - 1);
        double cx = right - 2 * TableMinButton.Width;
        bool collapsed = IsCollapsed(table);
        if (collapsed)
        {
            MoveTo(TableRestoreButton, right - TableRestoreButton.Width, cy);
            TableRestoreButton.Visibility = Visibility.Visible;
            TableMinButton.Visibility = TableCloseButton.Visibility = Visibility.Collapsed;
            // minimizada: não dá para acrescentar linhas/colunas
            AddRowButton.Visibility = AddColButton.Visibility = Visibility.Collapsed;
            return;
        }
        TableRestoreButton.Visibility = Visibility.Collapsed;
        MoveTo(TableMinButton, cx, cy);
        MoveTo(TableCloseButton, cx + TableMinButton.Width, cy);
        TableMinButton.Visibility = TableCloseButton.Visibility = Visibility.Visible;

        // faixa de "aproximação": da borda até um pouco além do botão
        double reach = PlusGap + AddRowButton.Height + 6;
        bool nearBottom = p.X >= lay.Left - 4 && p.X <= lay.Right + 4 && p.Y >= lay.Bottom - 6 && p.Y <= lay.Bottom + reach;
        bool nearRight = p.Y >= lay.Top - 4 && p.Y <= lay.Bottom + 4 && p.X >= lay.Right - 6 && p.X <= lay.Right + reach;

        // coordenadas do texto → coordenadas da camada dos botões (considera o zoom)
        if (nearBottom)
        {
            var at = Editor.TranslatePoint(new Point((lay.Left + lay.Right) / 2, lay.Bottom), TableOverlay);
            // se a tabela estiver no fim da área visível, o botão encosta no limite em vez de sumir
            double y = Math.Min(at.Y + PlusGap, TableOverlay.ActualHeight - AddRowButton.Height);
            MoveTo(AddRowButton, at.X - AddRowButton.Width / 2, y);
            AddRowButton.Visibility = Visibility.Visible;
        }
        else if (!AddRowButton.IsMouseOver) AddRowButton.Visibility = Visibility.Collapsed;

        if (nearRight)
        {
            var at = Editor.TranslatePoint(new Point(lay.Right, (lay.Top + lay.Bottom) / 2), TableOverlay);
            // tabela encostada na direita da nota: o botão encosta no limite em vez de ser cortado
            double x = Math.Min(at.X + PlusGap, TableOverlay.ActualWidth - AddColButton.Width);
            double y = at.Y - AddColButton.Height / 2;
            MoveTo(AddColButton, x, y);
            AddColButton.Visibility = Visibility.Visible;
        }
        else if (!AddColButton.IsMouseOver) AddColButton.Visibility = Visibility.Collapsed;
    }

    static void MoveTo(UIElement e, double x, double y)
    {
        // só mexe se mudou (evita refazer o layout a cada movimento do mouse)
        if (Math.Abs(Canvas.GetLeft(e) - x) > 0.5 || double.IsNaN(Canvas.GetLeft(e))) Canvas.SetLeft(e, x);
        if (Math.Abs(Canvas.GetTop(e) - y) > 0.5 || double.IsNaN(Canvas.GetTop(e))) Canvas.SetTop(e, y);
    }

    IEnumerable<Button> TableButtons => new[] { AddRowButton, AddColButton, TableMinButton, TableCloseButton, TableRestoreButton };

    void HideTableButtons(bool force = false)
    {
        // normalmente não esconde com o mouse em cima (dá tempo de clicar); depois do clique, força
        if (!force && TableButtons.Any(b => b.IsMouseOver)) return;
        foreach (var b in TableButtons)
            if (b.Visibility != Visibility.Collapsed) b.Visibility = Visibility.Collapsed;
    }

    // ---------- Redimensionar colunas e linhas arrastando as bordas ----------
    //
    // Cuidados para nunca "prender" a nota:
    //  • só começa se conseguir capturar o mouse;
    //  • durante o arrasto NÃO deixa um bloco de alteração aberto (isso escondia o cursor e a barra);
    //    a etapa de desfazer é criada de uma vez, no fim;
    //  • termina sozinho se o botão do mouse já não estiver pressionado ou se a nota perder o foco.

    RichEdit.ResizeHit? _resize;
    bool _resizing;
    bool _cursorOverridden;
    Point _resizeStart;
    double _w0, _m0, _maxRight;                       // coluna: largura e recuo iniciais; limite à direita
    List<(Paragraph p, double m0)> _rowParas = new();

    void SetResizeCursor(RichEdit.ResizeKind? kind) =>
        SetTableCursor(kind == null ? null : kind == RichEdit.ResizeKind.Column ? Cursors.SizeWE : Cursors.SizeNS);

    void SetTableCursor(Cursor? cursor)
    {
        if (cursor == null)
        {
            if (_cursorOverridden) { Mouse.OverrideCursor = null; _cursorOverridden = false; }
            return;
        }
        if (Mouse.OverrideCursor != cursor) Mouse.OverrideCursor = cursor;
        _cursorOverridden = true;
    }

    void Resize_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizing)
        {
            if (e.LeftButton != MouseButtonState.Pressed) { EndResize(); return; } // soltou fora: encerra
            DragResize(e.GetPosition(Editor));
            e.Handled = true;
            return;
        }
        if (!CanEdit) { SetResizeCursor(null); return; }
        var layouts = TableLayouts();
        if (layouts.Count == 0) { SetResizeCursor(null); HideTableButtons(); return; }
        var p = e.GetPosition(Editor);
        if (RichEdit.HitTestResize(layouts, p) is { } resize)
            SetResizeCursor(resize.Kind);
        else if (RichEdit.HitTestSelect(layouts, p) is { } select)
            SetTableCursor(select.Kind == RichEdit.SelectKind.Row ? TableCursors.SelectRow : TableCursors.SelectColumn);
        else
            SetTableCursor(null);
        UpdateTableButtons(p);
    }

    void Resize_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        HideTableButtons(); // clicou na tabela/texto: os "+" somem
        if (!CanEdit || _resizing) return;
        InvalidateTableLayouts(); // mede de novo agora, para o clique ser exato
        var p = e.GetPosition(Editor);
        if (RichEdit.HitTestResize(TableLayouts(), p) is not { } hit)
        {
            // setinha à esquerda da linha / acima da coluna: seleciona (ou desmarca) inteira
            if (RichEdit.HitTestSelect(TableLayouts(), p) is { } select)
            {
                Editor.Focus();
                RichEdit.ToggleSelect(Editor, select);
                e.Handled = true;
            }
            return;
        }
        if (!Editor.CaptureMouse()) return; // sem capturar o mouse não dá para arrastar com segurança

        _resize = hit;
        _resizeStart = p;
        if (hit.Kind == RichEdit.ResizeKind.Column)
        {
            var cols = hit.Table.Columns;
            _w0 = hit.Index == 0 ? cols[0].Width.Value : cols[hit.Index - 1].Width.Value;
            _m0 = double.IsNaN(hit.Table.Margin.Left) ? 0 : hit.Table.Margin.Left;
            _maxRight = hit.Layout.Left - _m0 + AvailableTextWidth(); // a tabela não passa da largura da nota
        }
        else
        {
            _rowParas = RichEdit.RowParagraphs(hit.Table, hit.Index).Select(x => (x, x.Margin.Bottom)).ToList();
        }
        _resizing = true;
        HideTableButtons();
        SetResizeCursor(hit.Kind);
        e.Handled = true; // não deixa começar seleção de texto
    }

    void DragResize(Point p)
    {
        var hit = _resize!;
        double dx = p.X - _resizeStart.X;
        var t = hit.Table;
        if (hit.Kind == RichEdit.ResizeKind.Column)
        {
            double min = RichEdit.MinColWidth;
            if (hit.Index == 0)
            {
                // borda esquerda: muda o recuo; a borda direita da 1ª coluna fica parada
                double margin = Math.Clamp(_m0 + dx, 0, _m0 + _w0 - min);
                t.Margin = new Thickness(margin, t.Margin.Top, 0, t.Margin.Bottom);
                t.Columns[0].Width = new GridLength(_w0 - (margin - _m0));
            }
            else
            {
                // borda entre colunas (ou a da direita): muda a largura da coluna da esquerda
                double rightOfOthers = hit.Layout.Right - hit.Layout.Edges[hit.Index]; // colunas depois da borda
                double maxWidth = _maxRight - hit.Layout.Edges[hit.Index - 1] - rightOfOthers;
                double width = Math.Clamp(_w0 + dx, min, Math.Max(min, maxWidth));
                t.Columns[hit.Index - 1].Width = new GridLength(Math.Round(width));
            }
        }
        else
        {
            double dy = p.Y - _resizeStart.Y;
            foreach (var (para, m0) in _rowParas)
                para.Margin = new Thickness(0, 0, 0, Math.Max(0, m0 + dy));
        }
    }

    void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        var hit = _resize;
        _resize = null;
        if (Editor.IsMouseCaptured) Editor.ReleaseMouseCapture();
        SetResizeCursor(null);

        // Altura de linha: refaz a mudança numa etapa só de desfazer (Ctrl+Z volta ao tamanho anterior)
        if (hit?.Kind == RichEdit.ResizeKind.Row && _rowParas.Count > 0)
        {
            var finals = _rowParas.Select(x => (x.p, final: x.p.Margin.Bottom)).ToList();
            foreach (var (para, m0) in _rowParas) para.Margin = new Thickness(0, 0, 0, m0);
            Editor.BeginChange();
            try { foreach (var (para, final) in finals) para.Margin = new Thickness(0, 0, 0, final); }
            finally { Editor.EndChange(); }
        }
        _rowParas.Clear();
        InvalidateTableLayouts();

        // largura de coluna não dispara TextChanged: marca para salvar
        _contentDirty = true;
        _flushTimer.Stop();
        _flushTimer.Start();
    }
}
