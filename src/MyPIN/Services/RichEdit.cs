using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MyPIN.Services;

public enum DividerKind { Thin, Double, Dotted }

/// <summary>
/// Linhas divisórias e tabelas dentro do texto da nota.
///
/// As notas são salvas em RTF, que não guarda tudo do WPF (ex.: linha pontilhada). Por isso cada
/// divisória leva um "marcador" invisível que sobrevive ao RTF (um espaço com uma cor específica),
/// e ao reabrir a nota o estilo é refeito a partir dele. O mesmo vale para as tabelas: ao reabrir,
/// o estilo (linhas finas, espaçamento) é reaplicado.
/// </summary>
public static class RichEdit
{
    static readonly Brush LineBrush = Frz(new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0)));
    static Brush Frz(Brush b) { b.Freeze(); return b; }

    // =====================================================================
    // Linhas divisórias
    // =====================================================================

    // Cores dos marcadores (quase brancas, num espaço: invisíveis)
    static readonly Dictionary<DividerKind, Color> Markers = new()
    {
        [DividerKind.Thin] = Color.FromRgb(0xFD, 0xFD, 0xF1),
        [DividerKind.Double] = Color.FromRgb(0xFD, 0xFD, 0xF2),
        [DividerKind.Dotted] = Color.FromRgb(0xFD, 0xFD, 0xF3),
    };

    static readonly Brush DottedBrush = MakeDottedBrush();

    static Brush MakeDottedBrush()
    {
        var dot = new GeometryDrawing(LineBrush, null, new RectangleGeometry(new Rect(0, 0, 2, 1)));
        var b = new DrawingBrush(dot)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 4, 1),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 4, 1),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        b.Freeze();
        return b;
    }

    /// <summary>Qual divisória o texto representa ("---", "___", "===", "***"), se algum.</summary>
    public static DividerKind? DividerFromText(string text)
    {
        var t = text.Trim();
        if (Regex.IsMatch(t, @"^(-{3,}|_{3,})$")) return DividerKind.Thin;
        if (Regex.IsMatch(t, @"^={3,}$")) return DividerKind.Double;
        if (Regex.IsMatch(t, @"^\*{3,}$")) return DividerKind.Dotted;
        return null;
    }

    static void StyleDivider(Paragraph p, DividerKind kind)
    {
        p.Margin = new Thickness(0, 4, 0, 4);
        p.Padding = new Thickness(0);
        p.TextIndent = 0;
        p.BorderBrush = kind == DividerKind.Dotted ? DottedBrush : LineBrush;
        p.BorderThickness = kind == DividerKind.Double ? new Thickness(0, 1, 0, 1) : new Thickness(0, 0, 0, 1);
    }

    static Paragraph NewDivider(DividerKind kind)
    {
        var run = new Run(" ") { FontSize = 3, Foreground = new SolidColorBrush(Markers[kind]) };
        var p = new Paragraph(run);
        StyleDivider(p, kind);
        return p;
    }

    public static DividerKind? DividerKindOf(Paragraph p)
    {
        // Ao reabrir do RTF o texto vem embrulhado em Span; o TextRange enxerga através disso
        var range = new TextRange(p.ContentStart, p.ContentEnd);
        if (range.Text != " ") return null;
        if (range.GetPropertyValue(TextElement.ForegroundProperty) is not SolidColorBrush b) return null;
        foreach (var (kind, color) in Markers)
            if (b.Color.R == color.R && b.Color.G == color.G && b.Color.B == color.B) return kind;
        return null;
    }

    /// <summary>
    /// Ao apertar Enter numa linha que só tem "---", "___", "===" ou "***": vira divisória.
    /// Tudo numa etapa só de desfazer: Ctrl+Z volta para o texto digitado.
    /// </summary>
    public static bool TryConvertDividerOnEnter(RichTextBox rtb)
    {
        if (!rtb.Selection.IsEmpty) return false;
        var para = rtb.CaretPosition.Paragraph;
        if (para == null || para.Parent is TableCell) return false;
        var text = new TextRange(para.ContentStart, para.ContentEnd).Text;
        if (DividerFromText(text) is not DividerKind kind) return false;
        // só se o cursor estiver no fim da linha
        if (new TextRange(rtb.CaretPosition, para.ContentEnd).Text.Trim().Length > 0) return false;

        rtb.BeginChange();
        try
        {
            var divider = NewDivider(kind);
            var blocks = SiblingBlocks(para);
            blocks.InsertBefore(para, divider);
            para.Inlines.Clear();                     // a linha atual vira a linha vazia depois da divisória
            rtb.CaretPosition = para.ContentStart;
        }
        finally
        {
            rtb.EndChange();
        }
        return true;
    }

    /// <summary>
    /// Del/Backspace junto de uma divisória apaga a divisória inteira de uma vez:
    /// cursor "dentro" dela, Del no fim da linha de cima, ou Backspace no começo da linha de baixo.
    /// </summary>
    public static bool TryDeleteDivider(RichTextBox rtb, bool forward)
    {
        if (!rtb.Selection.IsEmpty) return false;
        var caret = rtb.CaretPosition;
        var para = caret.Paragraph;
        if (para == null) return false;

        Paragraph? target = null;
        if (DividerKindOf(para) != null) target = para;
        // Olha a próxima/anterior posição de cursor (funciona também quando a linha vizinha está
        // depois/antes de uma tabela: aí o cursor está dentro da última/primeira célula)
        else if (forward && new TextRange(caret, para.ContentEnd).Text.Length == 0 &&
                 NeighborParagraph(caret, LogicalDirection.Forward) is { } next && DividerKindOf(next) != null)
            target = next;
        else if (!forward && new TextRange(para.ContentStart, caret).Text.Length == 0 &&
                 NeighborParagraph(caret, LogicalDirection.Backward) is { } prev && DividerKindOf(prev) != null)
            target = prev;
        if (target == null) return false;

        rtb.BeginChange();
        try
        {
            var stay = target == para ? (target.NextBlock ?? target.PreviousBlock) : null;
            SiblingBlocks(target).Remove(target);
            if (stay != null) rtb.CaretPosition = forward || stay == para.NextBlock ? stay.ContentStart : stay.ContentEnd;
            if (rtb.Document.Blocks.Count == 0) rtb.Document.Blocks.Add(new Paragraph());
            // se a divisória estava entre uma tabela e o fim do texto, garante uma linha para escrever
            if (rtb.Document.Blocks.LastBlock is Table) rtb.Document.Blocks.Add(new Paragraph());
        }
        finally
        {
            rtb.EndChange();
        }
        return true;
    }

    /// <summary>
    /// Parágrafo da próxima (ou anterior) posição de cursor, pulando as posições que não ficam em
    /// parágrafo nenhum (ex.: o "fim de linha" de uma tabela, logo depois da última célula).
    /// </summary>
    static Paragraph? NeighborParagraph(TextPointer from, LogicalDirection dir)
    {
        var pos = from;
        for (int i = 0; i < 4; i++)
        {
            pos = pos.GetNextInsertionPosition(dir);
            if (pos == null) return null;
            if (pos.Paragraph is { } p && p != from.Paragraph) return p;
        }
        return null;
    }

    /// <summary>Insere uma divisória onde está o cursor (botão da barra).</summary>
    public static void InsertDivider(RichTextBox rtb, DividerKind kind)
    {
        rtb.BeginChange();
        try
        {
            var para = rtb.CaretPosition.Paragraph;
            if (para == null || para.Parent is TableCell)
            {
                // fora de um parágrafo comum (ex.: dentro de tabela): coloca no fim do texto
                rtb.Document.Blocks.Add(NewDivider(kind));
                var after = new Paragraph();
                rtb.Document.Blocks.Add(after);
                rtb.CaretPosition = after.ContentStart;
                return;
            }
            var blocks = SiblingBlocks(para);
            bool empty = new TextRange(para.ContentStart, para.ContentEnd).Text.Trim().Length == 0;
            if (empty)
            {
                blocks.InsertBefore(para, NewDivider(kind));
                rtb.CaretPosition = para.ContentStart;
            }
            else
            {
                var divider = NewDivider(kind);
                blocks.InsertAfter(para, divider);
                var next = new Paragraph();
                blocks.InsertAfter(divider, next);
                rtb.CaretPosition = next.ContentStart;
            }
        }
        finally
        {
            rtb.EndChange();
        }
    }

    static BlockCollection SiblingBlocks(Block b) => b.Parent switch
    {
        FlowDocument d => d.Blocks,
        Section s => s.Blocks,
        ListItem li => li.Blocks,
        TableCell c => c.Blocks,
        _ => throw new InvalidOperationException("bloco sem pai"),
    };

    // =====================================================================
    // Ao abrir a nota: refaz o estilo de divisórias e tabelas
    // =====================================================================

    public static void Normalize(FlowDocument doc) => NormalizeBlocks(doc.Blocks);

    static void NormalizeBlocks(BlockCollection blocks)
    {
        foreach (var b in blocks.ToList())
        {
            switch (b)
            {
                case Paragraph p when DividerKindOf(p) is DividerKind k:
                    StyleDivider(p, k);
                    break;
                case Table t:
                    StyleTable(t);
                    foreach (var cell in t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
                        NormalizeBlocks(cell.Blocks);
                    break;
                case Section s:
                    NormalizeBlocks(s.Blocks);
                    break;
                case List l:
                    foreach (var li in l.ListItems) NormalizeBlocks(li.Blocks);
                    break;
            }
        }
    }

    // =====================================================================
    // Salvar em RTF
    // =====================================================================

    /// <summary>
    /// Gera o RTF da nota (as larguras das colunas são em pixels, que o RTF guarda direto).
    /// Tabelas minimizadas são gravadas COMPLETAS: numa cópia do texto, as linhas escondidas voltam
    /// para o lugar antes de salvar — assim nada se perde.
    /// </summary>
    public static string ToRtf(FlowDocument doc, Func<Table, CollapsedTable?>? collapsedOf = null)
    {
        var target = doc;
        var live = AllTables(doc.Blocks).ToList();
        if (collapsedOf != null && live.Any(t => collapsedOf(t) != null))
        {
            using var pkg = new System.IO.MemoryStream();
            new TextRange(doc.ContentStart, doc.ContentEnd).Save(pkg, DataFormats.XamlPackage);
            pkg.Position = 0;
            var clone = new FlowDocument();
            new TextRange(clone.ContentStart, clone.ContentEnd).Load(pkg, DataFormats.XamlPackage);
            var cloned = AllTables(clone.Blocks).ToList();
            for (int i = 0; i < Math.Min(live.Count, cloned.Count); i++)
                if (collapsedOf(live[i]) is { } info) ExpandCore(cloned[i], info);
            target = clone;
        }
        using var ms = new System.IO.MemoryStream();
        new TextRange(target.ContentStart, target.ContentEnd).Save(ms, DataFormats.Rtf);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    // =====================================================================
    // Minimizar tabela (fica só a 1ª linha) e restaurar
    // =====================================================================

    /// <summary>Linhas escondidas de uma tabela minimizada (guardadas como texto) + mesclagens da 1ª linha.</summary>
    public sealed record CollapsedTable(string HiddenRowsXaml, int[] FirstRowSpans);

    /// <summary>Esconde todas as linhas menos a 1ª. Devolve o que foi guardado (null se só tem 1 linha).</summary>
    public static CollapsedTable? Collapse(RichTextBox? rtb, Table t)
    {
        var group = t.RowGroups[0];
        if (group.Rows.Count <= 1) return null;
        CollapsedTable? result = null;
        void Work()
        {
            // células da 1ª linha mescladas para baixo: guardam o tamanho e viram 1 linha só
            var spans = group.Rows[0].Cells.Select(c => Math.Max(1, c.RowSpan)).ToArray();
            foreach (var c in group.Rows[0].Cells) c.RowSpan = 1;
            var hidden = group.Rows.Skip(1).ToList();
            foreach (var r in hidden) group.Rows.Remove(r);

            // guarda as linhas escondidas como texto (numa tabela temporária com as mesmas colunas)
            var temp = new FlowDocument();
            var tt = new Table();
            foreach (var col in t.Columns) tt.Columns.Add(new TableColumn { Width = col.Width });
            var tg = new TableRowGroup();
            tt.RowGroups.Add(tg);
            foreach (var r in hidden) tg.Rows.Add(r);
            temp.Blocks.Add(tt);
            using var ms = new System.IO.MemoryStream();
            new TextRange(temp.ContentStart, temp.ContentEnd).Save(ms, DataFormats.XamlPackage);
            result = new CollapsedTable(Convert.ToBase64String(ms.ToArray()), spans);
        }
        if (rtb != null) Change(rtb, Work); else Work();
        return result;
    }

    /// <summary>Devolve as linhas escondidas para a tabela (e as mesclagens da 1ª linha).</summary>
    public static void Expand(RichTextBox rtb, Table t, CollapsedTable info) => Change(rtb, () => ExpandCore(t, info));

    static void ExpandCore(Table t, CollapsedTable info)
    {
        var temp = new FlowDocument();
        using (var ms = new System.IO.MemoryStream(Convert.FromBase64String(info.HiddenRowsXaml)))
            new TextRange(temp.ContentStart, temp.ContentEnd).Load(ms, DataFormats.XamlPackage);
        var source = AllTables(temp.Blocks).FirstOrDefault();
        if (source == null) return;
        var rows = source.RowGroups.SelectMany(g => g.Rows).ToList();
        foreach (var g in source.RowGroups) g.Rows.Clear();
        var group = t.RowGroups[0];
        foreach (var r in rows) group.Rows.Add(r);
        var first = group.Rows[0].Cells;
        for (int i = 0; i < Math.Min(first.Count, info.FirstRowSpans.Length); i++) first[i].RowSpan = info.FirstRowSpans[i];
        StyleTable(t);
    }

    /// <summary>
    /// Ao abrir: tabela mais larga que a nota (ex.: salva por uma versão anterior) é reduzida
    /// proporcionalmente para caber.
    /// </summary>
    public static void FitTables(FlowDocument doc, double maxWidth)
    {
        if (maxWidth < 60) return;
        foreach (var t in AllTables(doc.Blocks))
        {
            double left = SafeLeft(t);
            double total = left + t.Columns.Sum(c => c.Width.IsAbsolute ? c.Width.Value : MinColWidth);
            if (total <= maxWidth) continue;
            double k = maxWidth / total;
            foreach (var c in t.Columns)
                c.Width = new GridLength(Math.Max(MinColWidth, Math.Floor((c.Width.IsAbsolute ? c.Width.Value : MinColWidth) * k)));
            t.Margin = new Thickness(Math.Floor(left * k), 8, 0, 4);
        }
    }

    /// <summary>Recuo à esquerda da tabela (a margem padrão do WPF é "automática" = NaN).</summary>
    static double SafeLeft(Table t) => double.IsNaN(t.Margin.Left) || t.Margin.Left < 0 ? 0 : t.Margin.Left;

    // =====================================================================
    // Redimensionar: onde estão as bordas das tabelas (em coordenadas do RichTextBox)
    // =====================================================================

    public const double MinColWidth = 24;
    public const double NewTableIndent = 8;

    public enum ResizeKind { Column, Row }

    /// <summary>
    /// Borda encontrada. Column: Index = nº da borda vertical (0 = esquerda da tabela, N = direita);
    /// Row: Index = linha cuja borda de baixo foi pega.
    /// </summary>
    public sealed record ResizeHit(ResizeKind Kind, Table Table, int Index, TableLayout Layout);

    /// <summary>Posição das bordas de uma tabela já desenhada.</summary>
    public sealed class TableLayout
    {
        public double[] Edges = Array.Empty<double>();     // x das bordas verticais (Cols + 1 valores)
        public double[] RowTop = Array.Empty<double>();
        public double Bottom = double.NaN;
        public double Left => Edges[0];
        public double Right => Edges[^1];
        public double Top => RowTop[0];
    }

    public static TableLayout? Measure(Table t)
    {
        var g = new Grid(t);
        if (g.Rows == 0 || g.Cols == 0 || t.Columns.Count != g.Cols) return null;
        var lay = new TableLayout { RowTop = Enumerable.Repeat(double.NaN, g.Rows).ToArray() };
        double left = double.NaN;
        foreach (var row in g.Group.Rows)
            foreach (var cell in row.Cells)
            {
                var rect = cell.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                if (rect.IsEmpty) continue;
                var (r, c) = g.PosOf(cell);
                if (c == 0 && double.IsNaN(left)) left = rect.Left - cell.Padding.Left - cell.BorderThickness.Left;
                if (double.IsNaN(lay.RowTop[r])) lay.RowTop[r] = rect.Top - cell.Padding.Top - cell.BorderThickness.Top;
            }
        if (double.IsNaN(left) || double.IsNaN(lay.RowTop[0])) return null;

        // bordas verticais: a partir da esquerda, somando as larguras (em pixels) das colunas
        lay.Edges = new double[g.Cols + 1];
        lay.Edges[0] = left;
        for (int c = 0; c < g.Cols; c++) lay.Edges[c + 1] = lay.Edges[c] + t.Columns[c].Width.Value;

        // fim da tabela: começo do bloco seguinte, menos as margens
        if (t.NextBlock is { } next)
        {
            var nr = next.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (!nr.IsEmpty) lay.Bottom = nr.Top - next.Margin.Top - next.Padding.Top - t.Margin.Bottom;
        }
        if (double.IsNaN(lay.Bottom)) lay.Bottom = lay.RowTop.Where(y => !double.IsNaN(y)).Max() + 22;
        return lay;
    }

    /// <summary>Mede todas as tabelas do texto (para guardar e reaproveitar enquanto nada muda).</summary>
    public static List<(Table table, TableLayout layout)> MeasureAll(FlowDocument doc) =>
        AllTables(doc.Blocks).Select(t => (t, Measure(t))).Where(x => x.Item2 != null).Select(x => (x.t, x.Item2!)).ToList();

    /// <summary>
    /// Célula embaixo do ponto, pela GEOMETRIA (bordas da tabela). Não dá para confiar no "texto mais
    /// próximo" do WPF: numa célula mesclada alta e vazia, clicar na parte de baixo dela "pega" o
    /// texto da célula vizinha.
    /// </summary>
    public static TableCell? CellAtPoint(IEnumerable<(Table table, TableLayout layout)> layouts, Point p)
    {
        foreach (var (t, lay) in layouts)
        {
            if (p.X < lay.Left || p.X >= lay.Right || p.Y < lay.Top || p.Y >= lay.Bottom) continue;
            int r = -1, c = -1;
            for (int i = 0; i < lay.RowTop.Length; i++) if (!double.IsNaN(lay.RowTop[i]) && p.Y >= lay.RowTop[i]) r = i;
            for (int i = 0; i < lay.Edges.Length - 1; i++) if (p.X >= lay.Edges[i]) c = i;
            if (r >= 0 && c >= 0) return new Grid(t).At(r, c);
        }
        return null;
    }

    /// <summary>Tabela embaixo do ponto (com uma folga em volta), para mostrar os botões "+".</summary>
    public static (Table table, TableLayout layout)? TableNear(IEnumerable<(Table table, TableLayout layout)> layouts, Point p, double margin)
    {
        foreach (var (t, lay) in layouts)
            if (p.X >= lay.Left - margin && p.X <= lay.Right + margin && p.Y >= lay.Top - margin && p.Y <= lay.Bottom + margin)
                return (t, lay);
        return null;
    }

    public static ResizeHit? HitTestResize(RichTextBox rtb, Point p, double grip = 4) =>
        HitTestResize(MeasureAll(rtb.Document), p, grip);

    public static ResizeHit? HitTestResize(IEnumerable<(Table table, TableLayout layout)> layouts, Point p, double grip = 4)
    {
        foreach (var (t, lay) in layouts)
        {
            if (p.Y < lay.Top - grip || p.Y > lay.Bottom + grip || p.X < lay.Left - grip || p.X > lay.Right + grip) continue;

            // bordas verticais, inclusive a da esquerda e a da direita
            for (int c = 0; c < lay.Edges.Length; c++)
                if (Math.Abs(p.X - lay.Edges[c]) <= grip)
                    return new ResizeHit(ResizeKind.Column, t, c, lay);

            // embaixo de cada linha (a última usa o fim da tabela)
            for (int r = 0; r < lay.RowTop.Length; r++)
            {
                double rowBottom = r + 1 < lay.RowTop.Length ? lay.RowTop[r + 1] : lay.Bottom;
                if (!double.IsNaN(rowBottom) && Math.Abs(p.Y - rowBottom) <= grip)
                    return new ResizeHit(ResizeKind.Row, t, r, lay);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    // Selecionar linha/coluna inteira (setinha à esquerda da linha / acima da coluna)
    // ---------------------------------------------------------------------

    public enum SelectKind { Row, Column }

    public sealed record SelectHit(SelectKind Kind, Table Table, int Index);

    /// <summary>Faixa à esquerda da tabela (linha) ou logo acima dela (coluna).</summary>
    public static SelectHit? HitTestSelect(IEnumerable<(Table table, TableLayout layout)> layouts, Point p)
    {
        foreach (var (t, lay) in layouts)
        {
            // à esquerda: entre a borda do texto e 2 px antes da borda esquerda da tabela
            if (p.X < lay.Left - 2 && p.X >= lay.Left - 16 && p.Y >= lay.Top && p.Y < lay.Bottom)
            {
                for (int r = lay.RowTop.Length - 1; r >= 0; r--)
                    if (!double.IsNaN(lay.RowTop[r]) && p.Y >= lay.RowTop[r])
                        return new SelectHit(SelectKind.Row, t, r);
            }
            // acima: uma faixa de 8 px logo antes do topo da tabela
            if (p.Y < lay.Top - 1 && p.Y >= lay.Top - 9 && p.X >= lay.Left && p.X < lay.Right)
            {
                for (int c = lay.Edges.Length - 2; c >= 0; c--)
                    if (p.X >= lay.Edges[c])
                        return new SelectHit(SelectKind.Column, t, c);
            }
        }
        return null;
    }

    /// <summary>Seleciona a linha/coluna inteira; se ela já estava selecionada, desmarca.</summary>
    public static void ToggleSelect(RichTextBox rtb, SelectHit hit)
    {
        var g = new Grid(hit.Table);
        TableCell? first, last;
        int r1, c1, r2, c2;
        if (hit.Kind == SelectKind.Row)
        {
            (r1, c1, r2, c2) = (hit.Index, 0, hit.Index, g.Cols - 1);
            // começa na 1ª célula que COMEÇA nesta linha: se a 1ª coluna for uma mesclada que vem
            // de cima, começar nela espalharia a seleção pelas linhas de cima
            var row = g.Group.Rows[hit.Index];
            first = row.Cells.Count > 0 ? row.Cells[0] : g.At(r1, c1);
        }
        else
        {
            (r1, c1, r2, c2) = (0, hit.Index, g.Rows - 1, hit.Index);
            first = g.At(r1, c1);
        }
        last = g.At(r2, c2);
        if (first == null || last == null) return;
        c1 = g.PosOf(first).Col;

        // já está exatamente selecionada? então desmarca
        if (SelectedRect(rtb) is var (t, sr1, sc1, sr2, sc2) && t == hit.Table)
        {
            var (fr, fc) = g.PosOf(first);
            var (lr, lc) = g.PosOf(last);
            if (sr1 == Math.Min(fr, r1) && sc1 == Math.Min(fc, c1) &&
                sr2 == Math.Max(lr + g.RowSpan(last) - 1, r2) && sc2 == Math.Max(lc + g.ColSpan(last) - 1, c2))
            {
                var pos = first.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                rtb.Selection.Select(pos, pos);
                return;
            }
        }
        // Pelas bordas das células (como o WPF faz ao arrastar o mouse). Pelo texto não dá: numa
        // célula VAZIA o "fim do texto" é igual ao começo, e ela ficava de fora da seleção.
        rtb.Selection.Select(first.ElementStart, last.ElementEnd);
    }

    /// <summary>Últimos parágrafos das células que começam na linha <paramref name="row"/> (guardam a altura extra).</summary>
    public static List<Paragraph> RowParagraphs(Table t, int row)
    {
        var g = new Grid(t);
        return g.Group.Rows[row].Cells
            .Where(c => g.RowSpan(c) == 1)
            .Select(c => c.Blocks.LastBlock as Paragraph)
            .OfType<Paragraph>()
            .ToList();
    }

    public static IEnumerable<Table> AllTables(BlockCollection blocks)
    {
        foreach (var b in blocks)
        {
            if (b is Table t) yield return t;
            else if (b is Section s) foreach (var x in AllTables(s.Blocks)) yield return x;
            else if (b is List l) foreach (var li in l.ListItems) foreach (var x in AllTables(li.Blocks)) yield return x;
        }
    }

    // =====================================================================
    // Tabelas
    // =====================================================================

    /// <summary>Cria uma tabela com colunas iguais que, juntas, ocupam <paramref name="width"/> pixels.</summary>
    public static Table CreateTable(int rows, int cols, IReadOnlyList<IReadOnlyList<string>>? data = null, double width = 240)
    {
        // recuo de 8 px: sobra uma faixa à esquerda para a setinha que seleciona a linha
        var table = new Table { Margin = new Thickness(NewTableIndent, 8, 0, 4) };
        width -= NewTableIndent;
        double colWidth = Math.Max(MinColWidth, Math.Floor(width / Math.Max(1, cols)));
        for (int c = 0; c < cols; c++) table.Columns.Add(new TableColumn { Width = new GridLength(colWidth) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < cols; c++)
            {
                string text = data != null && r < data.Count && c < data[r].Count ? data[r][c] : "";
                row.Cells.Add(NewCell(text));
            }
            group.Rows.Add(row);
        }
        StyleTable(table);
        return table;
    }

    static TableCell NewCell(string text = "")
    {
        var p = new Paragraph();
        if (text.Length > 0) p.Inlines.Add(new Run(text));
        return new TableCell(p);
    }

    /// <summary>
    /// Estilo da nota: linhas finas, sem espaço entre células. Larguras em pixels (a tabela pode ser
    /// mais estreita que a nota); coluna sem largura recebe a média das outras. O recuo à esquerda é mantido.
    /// </summary>
    static void StyleTable(Table t)
    {
        t.CellSpacing = 0;
        // 8 px acima: espaço para a setinha que seleciona a coluna
        t.Margin = new Thickness(SafeLeft(t), 8, 0, 4);
        t.BorderBrush = LineBrush;
        t.BorderThickness = new Thickness(0.5);
        var sized = t.Columns.Where(c => c.Width.IsAbsolute && c.Width.Value > 0).Select(c => c.Width.Value).ToList();
        double avg = sized.Count > 0 ? sized.Average() : 80;
        foreach (var col in t.Columns)
            if (!col.Width.IsAbsolute || col.Width.Value <= 0) col.Width = new GridLength(Math.Max(MinColWidth, Math.Floor(avg)));
        foreach (var cell in t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells))
        {
            cell.BorderBrush = LineBrush;
            cell.BorderThickness = new Thickness(0.5);
            cell.Padding = new Thickness(4, 1, 4, 1);
            // o espaço abaixo do último parágrafo guarda a altura extra da linha (redimensionar linha)
            var paragraphs = cell.Blocks.OfType<Paragraph>().ToList();
            foreach (var p in paragraphs)
            {
                bool hasLocal = p.ReadLocalValue(Block.MarginProperty) != DependencyProperty.UnsetValue;
                double extra = p == paragraphs[^1] && hasLocal ? Math.Max(0, p.Margin.Bottom) : 0;
                p.Margin = new Thickness(0, 0, 0, extra);
            }
        }
    }

    /// <summary>Insere a tabela onde está o cursor e coloca o cursor na 1ª célula.</summary>
    public static void InsertTable(RichTextBox rtb, Table table)
    {
        rtb.BeginChange();
        try
        {
            var para = rtb.CaretPosition.Paragraph;
            if (para == null)
            {
                rtb.Document.Blocks.Add(table);
            }
            else if (para.Parent is TableCell cell && TableOf(cell) is Table outer)
            {
                SiblingBlocks(outer).InsertAfter(outer, table); // cursor dentro de outra tabela: entra depois dela
            }
            else
            {
                var blocks = SiblingBlocks(para);
                bool empty = new TextRange(para.ContentStart, para.ContentEnd).Text.Trim().Length == 0;
                if (empty) blocks.InsertBefore(para, table);
                else blocks.InsertAfter(para, table);
            }

            // sempre deixa um parágrafo depois da tabela, para continuar escrevendo
            var siblings = SiblingBlocks(table);
            if (table.NextBlock == null) siblings.Add(new Paragraph());

            var first = table.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock;
            rtb.CaretPosition = first.ContentStart;
        }
        finally
        {
            rtb.EndChange();
        }
    }

    /// <summary>
    /// Tamanho da maior tabela num RTF (nº de linhas, maior nº de células numa linha), contando os
    /// marcadores de linha (\row) e de célula (\cell). (0,0) se não houver tabela.
    /// </summary>
    public static (int rows, int cols) RtfTableSize(string rtf)
    {
        int rows = 0, maxCols = 0, cells = 0;
        foreach (Match m in Regex.Matches(rtf, @"\\(row|cell|nestrow|nestcell)(?![a-z])"))
        {
            switch (m.Groups[1].Value)
            {
                case "cell": cells++; break;
                case "row": rows++; maxCols = Math.Max(maxCols, cells); cells = 0; break;
            }
        }
        return (rows, maxCols);
    }

    /// <summary>
    /// Texto copiado de planilha (Excel, Google Planilhas): colunas separadas por TAB e linhas por
    /// quebra de linha; células com aspas podem ter quebras de linha dentro.
    /// </summary>
    public static List<List<string>> ParseTsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else cell.Append(ch);
            }
            else if (ch == '"' && cell.Length == 0) quoted = true;
            else if (ch == '\t') { row.Add(cell.ToString()); cell.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
            else cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    // ---------------------------------------------------------------------
    // Mapa da grade: em que linha/coluna cada célula começa (considerando mesclagens)
    // ---------------------------------------------------------------------

    public sealed class Grid
    {
        public Table Table { get; }
        public TableRowGroup Group { get; }
        public int Rows => Group.Rows.Count;
        public int Cols { get; }
        readonly TableCell?[,] _map;
        readonly Dictionary<TableCell, (int Row, int Col)> _pos = new();

        public Grid(Table table)
        {
            Table = table;
            Group = table.RowGroups[0];
            int rows = Group.Rows.Count;
            // nº de colunas = maior soma de ColumnSpan + ocupações de RowSpan
            int maxCols = Math.Max(table.Columns.Count, 1);
            var occ = new List<bool[]>();
            var map = new Dictionary<(int, int), TableCell>();
            for (int r = 0; r < rows; r++)
            {
                int c = 0;
                foreach (var cell in Group.Rows[r].Cells)
                {
                    while (map.ContainsKey((r, c))) c++;
                    _pos[cell] = (r, c);
                    for (int dr = 0; dr < Math.Max(1, cell.RowSpan); dr++)
                        for (int dc = 0; dc < Math.Max(1, cell.ColumnSpan); dc++)
                            map[(r + dr, c + dc)] = cell;
                    c += Math.Max(1, cell.ColumnSpan);
                    maxCols = Math.Max(maxCols, c);
                }
            }
            Cols = maxCols;
            _map = new TableCell?[Math.Max(rows, 1), Cols];
            foreach (var ((r, c), cell) in map)
                if (r < rows && c < Cols) _map[r, c] = cell;
        }

        public TableCell? At(int r, int c) => r >= 0 && c >= 0 && r < Rows && c < Cols ? _map[r, c] : null;
        public (int Row, int Col) PosOf(TableCell cell) => _pos[cell];
        public int RowSpan(TableCell c) => Math.Max(1, c.RowSpan);
        public int ColSpan(TableCell c) => Math.Max(1, c.ColumnSpan);

        /// <summary>Em que posição da lista de células da linha <paramref name="r"/> entra uma célula que começa na coluna <paramref name="col"/>.</summary>
        public int InsertIndex(int r, int col) =>
            Group.Rows[r].Cells.Count(cell => PosOf(cell).Col < col);
    }

    public static TableCell? CellAt(TextPointer? pos)
    {
        DependencyObject? o = pos?.Parent;
        while (o != null && o is not TableCell)
            o = o is FrameworkContentElement fce ? fce.Parent : null;
        return o as TableCell;
    }

    /// <summary>
    /// Enter com o cursor no fim da tabela (fim da última célula, ou no "fim de linha" depois dela):
    /// sai da tabela e vai para a linha de baixo, em vez de criar uma linha nova na tabela.
    /// No fim de uma linha que não é a última, vai para a 1ª célula da linha seguinte.
    /// </summary>
    public static bool TryExitTableOnEnter(RichTextBox rtb)
    {
        if (!rtb.Selection.IsEmpty) return false;
        var caret = rtb.CaretPosition;
        var cell = CellAt(caret);
        TableRow? row = cell?.Parent as TableRow;
        bool atRowEnd = false;
        if (cell == null)
        {
            // cursor no "fim de linha" (fora das células, mas dentro da tabela)
            row = caret.Parent as TableRow ?? (caret.GetAdjacentElement(LogicalDirection.Backward) as TableCell)?.Parent as TableRow;
            if (row == null) return false;
            atRowEnd = true;
        }
        else
        {
            // só no fim da ÚLTIMA célula da linha
            if (row == null || row.Cells[^1] != cell || new TextRange(caret, cell.ContentEnd).Text.Trim().Length > 0) return false;
            atRowEnd = true;
        }
        if (!atRowEnd || row.Parent is not TableRowGroup group || group.Parent is not Table table) return false;

        int index = group.Rows.IndexOf(row);
        if (index < group.Rows.Count - 1)
        {
            // fim de uma linha do meio: Enter dentro da célula (comportamento normal) se estiver numa célula
            if (cell != null) return false;
            rtb.CaretPosition = group.Rows[index + 1].Cells[0].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
            return true;
        }

        rtb.BeginChange();
        try
        {
            if (table.NextBlock is not Paragraph after)
            {
                after = new Paragraph();
                SiblingBlocks(table).InsertAfter(table, after);
            }
            rtb.CaretPosition = after.ContentStart;
        }
        finally
        {
            rtb.EndChange();
        }
        return true;
    }

    /// <summary>Botão "+" embaixo da tabela: nova linha no fim.</summary>
    public static void AddRowAtEnd(RichTextBox rtb, Table t)
    {
        var g = new Grid(t);
        var last = g.At(g.Rows - 1, 0);
        if (last != null) InsertRow(rtb, last, below: true);
    }

    /// <summary>Botão "+" à direita da tabela: nova coluna no fim.</summary>
    public static void AddColumnAtEnd(RichTextBox rtb, Table t)
    {
        var g = new Grid(t);
        var last = g.At(0, g.Cols - 1);
        if (last != null) InsertColumn(rtb, last, right: true);
    }

    /// <summary>Célula que contém o ponto ou, se ele estiver entre células, a vizinha na direção dada.</summary>
    public static TableCell? CellNear(TextPointer? pos, LogicalDirection dir)
    {
        if (pos == null) return null;
        if (CellAt(pos) is { } inside) return inside;
        var adjacent = pos.GetAdjacentElement(dir);
        if (adjacent is TableCell cell) return cell;
        if (adjacent is TableRow row && row.Cells.Count > 0)
            return dir == LogicalDirection.Forward ? row.Cells[0] : row.Cells[^1];
        var ins = pos.GetInsertionPosition(dir);
        return ins != null ? CellAt(ins) : null;
    }

    public static Table? TableOf(TableCell cell) =>
        cell.Parent is TableRow { Parent: TableRowGroup { Parent: Table t } } ? t : null;

    // ---------------------------------------------------------------------
    // Operações (sempre dentro de BeginChange/EndChange: um Ctrl+Z desfaz cada uma)
    // ---------------------------------------------------------------------

    public static void InsertRow(RichTextBox rtb, TableCell at, bool below)
    {
        var table = TableOf(at)!;
        Change(rtb, () =>
        {
            var g = new Grid(table);
            var (r0, _) = g.PosOf(at);
            int newIndex = below ? r0 + g.RowSpan(at) : r0;
            var row = new TableRow();
            for (int c = 0; c < g.Cols;)
            {
                var above = newIndex > 0 ? g.At(newIndex - 1, c) : null;
                var here = g.At(newIndex, c);
                if (above != null && above == here) // uma célula mesclada atravessa: estica ela
                {
                    above.RowSpan = g.RowSpan(above) + 1;
                    c += g.ColSpan(above);
                }
                else { row.Cells.Add(NewCell()); c++; }
            }
            if (newIndex >= g.Rows) g.Group.Rows.Add(row);
            else g.Group.Rows.Insert(newIndex, row);
            StyleTable(table);
        });
    }

    public static void InsertColumn(RichTextBox rtb, TableCell at, bool right)
    {
        var table = TableOf(at)!;
        Change(rtb, () =>
        {
            var g = new Grid(table);
            var (_, c0) = g.PosOf(at);
            int newCol = right ? c0 + g.ColSpan(at) : c0;
            var widened = new HashSet<TableCell>();
            for (int r = 0; r < g.Rows; r++)
            {
                var left = newCol > 0 ? g.At(r, newCol - 1) : null;
                var here = g.At(r, newCol);
                if (left != null && left == here) // mesclada atravessa a nova coluna: alarga
                {
                    if (widened.Add(left)) left.ColumnSpan = g.ColSpan(left) + 1;
                    continue;
                }
                // célula que vem de cima (RowSpan) já cobre esta linha nesta coluna? então pula
                if (here != null && g.PosOf(here).Row != r && g.PosOf(here).Col == newCol) continue;
                g.Group.Rows[r].Cells.Insert(g.InsertIndex(r, newCol), NewCell());
            }
            table.Columns.Insert(Math.Min(newCol, table.Columns.Count), new TableColumn());
            StyleTable(table);
        });
    }

    public static void DeleteRow(RichTextBox rtb, TableCell at)
    {
        var table = TableOf(at)!;
        var g0 = new Grid(table);
        if (g0.Rows <= 1) { DeleteTable(rtb, at); return; }
        Change(rtb, () =>
        {
            var g = new Grid(table);
            int r = g.PosOf(at).Row;
            var row = g.Group.Rows[r];
            for (int c = 0; c < g.Cols;)
            {
                var cell = g.At(r, c)!;
                int span = g.ColSpan(cell);
                var (cr, _) = g.PosOf(cell);
                if (g.RowSpan(cell) > 1)
                {
                    if (cr < r) cell.RowSpan--;                   // vem de cima: só encurta
                    else                                          // começa aqui e desce: passa para a linha de baixo
                    {
                        row.Cells.Remove(cell);
                        cell.RowSpan--;
                        g.Group.Rows[r + 1].Cells.Insert(g.InsertIndex(r + 1, c), cell);
                    }
                }
                c += span;
            }
            g.Group.Rows.Remove(row);
            StyleTable(table);
        });
    }

    public static void DeleteColumn(RichTextBox rtb, TableCell at)
    {
        var table = TableOf(at)!;
        var g0 = new Grid(table);
        if (g0.Cols <= 1) { DeleteTable(rtb, at); return; }
        Change(rtb, () =>
        {
            var g = new Grid(table);
            int c = g.PosOf(at).Col;
            var done = new HashSet<TableCell>();
            for (int r = 0; r < g.Rows; r++)
            {
                var cell = g.At(r, c);
                if (cell == null || !done.Add(cell)) continue;
                if (g.ColSpan(cell) > 1) cell.ColumnSpan--;
                else g.Group.Rows[g.PosOf(cell).Row].Cells.Remove(cell);
            }
            if (table.Columns.Count > 0) table.Columns.RemoveAt(Math.Min(c, table.Columns.Count - 1));
            StyleTable(table);
        });
    }

    public static void DeleteTable(RichTextBox rtb, TableCell at)
    {
        var table = TableOf(at)!;
        Change(rtb, () =>
        {
            var blocks = SiblingBlocks(table);
            var next = table.NextBlock;
            blocks.Remove(table);
            if (next != null) rtb.CaretPosition = next.ContentStart;
        });
    }

    /// <summary>Células do retângulo entre o início e o fim da seleção (se a seleção estiver numa tabela só).</summary>
    public static (Table table, int r1, int c1, int r2, int c2)? SelectedRect(RichTextBox rtb)
    {
        // Sem seleção (só o cursor piscando) não há "células selecionadas" — mesmo que o cursor
        // esteja numa célula mesclada, que ocupa várias posições da grade.
        if (rtb.Selection.IsEmpty) return null;

        // Seleção feita com o mouse sobre várias células: o WPF põe o início/fim ENTRE as células
        // (fora do texto delas), por isso olhamos também a célula vizinha a cada ponta.
        var a = CellNear(rtb.Selection.Start, LogicalDirection.Forward);
        var end = rtb.Selection.End;
        var b = CellNear(end, LogicalDirection.Backward);
        // o fim pode cair no comecinho da célula seguinte: aí vale a célula anterior
        if (b != null && b != a && CellAt(end) == b && new TextRange(b.ContentStart, end).Text.Length == 0 &&
            end.GetNextInsertionPosition(LogicalDirection.Backward) is { } prev && CellAt(prev) is { } prevCell)
            b = prevCell;
        if (a == null || b == null || a == b) return null; // uma célula só (mesmo mesclada): nada a mesclar
        var t = TableOf(a);
        if (t == null || TableOf(b) != t) return null;
        var g = new Grid(t);
        var pa = g.PosOf(a); var pb = g.PosOf(b);
        int r1 = Math.Min(pa.Row, pb.Row), c1 = Math.Min(pa.Col, pb.Col);
        int r2 = Math.Max(pa.Row + g.RowSpan(a) - 1, pb.Row + g.RowSpan(b) - 1);
        int c2 = Math.Max(pa.Col + g.ColSpan(a) - 1, pb.Col + g.ColSpan(b) - 1);
        // expande até não cortar nenhuma célula mesclada ao meio
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++)
                {
                    var cell = g.At(r, c);
                    if (cell == null) continue;
                    var p = g.PosOf(cell);
                    int er = p.Row + g.RowSpan(cell) - 1, ec = p.Col + g.ColSpan(cell) - 1;
                    if (p.Row < r1) { r1 = p.Row; changed = true; }
                    if (p.Col < c1) { c1 = p.Col; changed = true; }
                    if (er > r2) { r2 = er; changed = true; }
                    if (ec > c2) { c2 = ec; changed = true; }
                }
        }
        if (r1 == r2 && c1 == c2) return null;
        return (t, r1, c1, r2, c2);
    }

    /// <summary>Mescla as células do retângulo selecionado; o texto delas é juntado na primeira.</summary>
    public static void MergeSelection(RichTextBox rtb)
    {
        if (SelectedRect(rtb) is not var (table, r1, c1, r2, c2)) return;
        Change(rtb, () =>
        {
            var g = new Grid(table);
            var target = g.At(r1, c1)!;
            var others = new List<TableCell>();
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++)
                    if (g.At(r, c) is { } cell && cell != target && !others.Contains(cell)) others.Add(cell);

            foreach (var cell in others)
            {
                bool hasText = new TextRange(cell.ContentStart, cell.ContentEnd).Text.Trim().Length > 0;
                if (hasText)
                    foreach (var block in cell.Blocks.ToList()) { cell.Blocks.Remove(block); target.Blocks.Add(block); }
                ((TableRow)cell.Parent).Cells.Remove(cell);
            }
            // tira parágrafos vazios que sobraram no começo da célula mesclada
            while (target.Blocks.Count > 1 && target.Blocks.FirstBlock is Paragraph p0 &&
                   new TextRange(p0.ContentStart, p0.ContentEnd).Text.Trim().Length == 0)
                target.Blocks.Remove(p0);

            target.RowSpan = r2 - r1 + 1;
            target.ColumnSpan = c2 - c1 + 1;
            rtb.CaretPosition = target.ContentStart;
            StyleTable(table);
        });
    }

    public static bool CanUnmerge(TableCell cell) => cell.RowSpan > 1 || cell.ColumnSpan > 1;

    /// <summary>Células mescladas dentro da seleção (ou só a célula clicada, se ela for mesclada).</summary>
    public static List<TableCell> MergedCellsToSplit(RichTextBox rtb, TableCell? clicked)
    {
        var result = new List<TableCell>();
        if (SelectedRect(rtb) is var (table, r1, c1, r2, c2))
        {
            var g = new Grid(table);
            for (int r = r1; r <= r2; r++)
                for (int c = c1; c <= c2; c++)
                    if (g.At(r, c) is { } cell && CanUnmerge(cell) && !result.Contains(cell)) result.Add(cell);
        }
        if (clicked != null && CanUnmerge(clicked) && !result.Contains(clicked)) result.Add(clicked);
        return result;
    }

    /// <summary>Desmescla todas as células indicadas, numa etapa só de desfazer.</summary>
    public static void UnmergeAll(RichTextBox rtb, IReadOnlyList<TableCell> cells)
    {
        if (cells.Count == 0) return;
        Change(rtb, () =>
        {
            foreach (var cell in cells)
                if (CanUnmerge(cell) && TableOf(cell) != null) UnmergeCore(cell);
        });
    }

    /// <summary>Desfaz a mesclagem: a célula volta a 1×1 e as posições liberadas ganham células vazias.</summary>
    public static void Unmerge(RichTextBox rtb, TableCell cell) => Change(rtb, () => UnmergeCore(cell));

    static void UnmergeCore(TableCell cell)
    {
        var table = TableOf(cell)!;
        {
            var g = new Grid(table);
            var (r0, c0) = g.PosOf(cell);
            int rs = g.RowSpan(cell), cs = g.ColSpan(cell);
            cell.RowSpan = 1;
            cell.ColumnSpan = 1;
            for (int r = r0; r < r0 + rs; r++)
            {
                int start = r == r0 ? c0 + 1 : c0;
                int count = r == r0 ? cs - 1 : cs;
                if (count <= 0) continue;
                var g2 = new Grid(table); // o mapa muda a cada inserção
                int index = g2.InsertIndex(r, start);
                for (int k = 0; k < count; k++) g2.Group.Rows[r].Cells.Insert(index + k, NewCell());
            }
            StyleTable(table);
        }
    }

    static void Change(RichTextBox rtb, Action action)
    {
        rtb.BeginChange();
        try { action(); }
        finally { rtb.EndChange(); }
    }
}
