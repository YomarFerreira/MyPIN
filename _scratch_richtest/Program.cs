using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using MyPIN.Services;

class P
{
    static int fails;
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "  OK    " : "  FALHA ") + what); if (!ok) fails++; }

    [STAThread]
    static void Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var rtb = new RichTextBox { Width = 300, Height = 300 };
        var win = new Window { Content = rtb, Width = 320, Height = 320, ShowActivated = false, Left = -2000 };
        win.Loaded += (_, _) =>
        {
            try { Run(rtb); } catch (Exception ex) { Console.WriteLine(ex); fails++; }
            Console.WriteLine(fails == 0 ? "\nTUDO OK" : $"\n{fails} FALHA(S)");
            app.Shutdown();
        };
        win.Show();
        app.Run();
    }

    static void Type(RichTextBox r, string s) { r.Selection.Text = s; var e = r.Selection.End; r.Selection.Select(e, e); }

    static string Rtf(FlowDocument d)
    {
        var ms = new MemoryStream();
        new TextRange(d.ContentStart, d.ContentEnd).Save(ms, DataFormats.Rtf);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    static FlowDocument Reload(string rtf)
    {
        var d = new FlowDocument();
        new TextRange(d.ContentStart, d.ContentEnd).Load(new MemoryStream(Encoding.Latin1.GetBytes(rtf)), DataFormats.Rtf);
        RichEdit.Normalize(d);
        return d;
    }

    static string Text(Block b) => new TextRange(b.ContentStart, b.ContentEnd).Text.Trim();

    static string Describe(Table t)
    {
        var g = new RichEdit.Grid(t);
        var sb = new StringBuilder($"{g.Rows}x{g.Cols}: ");
        foreach (var row in g.Group.Rows)
        {
            sb.Append('[');
            foreach (var c in row.Cells)
            {
                var (r, col) = g.PosOf(c);
                var txt = new TextRange(c.ContentStart, c.ContentEnd).Text.Trim().Replace("\r\n", "/");
                sb.Append($"({r},{col}{(c.RowSpan > 1 || c.ColumnSpan > 1 ? $" {c.RowSpan}x{c.ColumnSpan}" : "")}){txt} ");
            }
            sb.Append("] ");
        }
        return sb.ToString().TrimEnd();
    }

    static void Run(RichTextBox rtb)
    {
        // ---------------- 1. divisória + desfazer ----------------
        Console.WriteLine("1) Divisória ao apertar Enter, e Ctrl+Z");
        rtb.Focus();
        rtb.Document = new FlowDocument(new Paragraph(new Run("Texto antes")));
        rtb.CaretPosition = rtb.Document.ContentEnd;
        EditingCommands.EnterParagraphBreak.Execute(null, rtb);
        Type(rtb, "---");
        bool converted = RichEdit.TryConvertDividerOnEnter(rtb);
        var blocks = rtb.Document.Blocks.ToList();
        Check(converted, "converteu '---'");
        Check(blocks.Count == 3 && blocks[1] is Paragraph p1 && RichEdit.DividerKindOf(p1) == DividerKind.Thin,
              $"estrutura: texto / divisória fina / linha vazia (tem {blocks.Count} blocos)");
        ApplicationCommands.Undo.Execute(null, rtb);
        blocks = rtb.Document.Blocks.ToList();
        Check(blocks.Count == 2 && Text(blocks[1]) == "---" && blocks.All(b => b is not Paragraph pp || RichEdit.DividerKindOf(pp) == null),
              $"Ctrl+Z volta para '---' (blocos: {string.Join(" | ", blocks.Select(Text))})");
        ApplicationCommands.Redo.Execute(null, rtb);
        Check(rtb.Document.Blocks.Count == 3, "Ctrl+Y refaz a divisória");
        Check(RichEdit.DividerFromText("___") == DividerKind.Thin && RichEdit.DividerFromText("=====") == DividerKind.Double &&
              RichEdit.DividerFromText("***") == DividerKind.Dotted && RichEdit.DividerFromText("--") == null &&
              RichEdit.DividerFromText("-a-") == null, "reconhece ___ / === / *** e ignora -- e -a-");

        // ---------------- 2. divisórias sobrevivem ao salvar/reabrir ----------------
        Console.WriteLine("2) Divisórias depois de salvar e reabrir (RTF)");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        foreach (var k in new[] { DividerKind.Thin, DividerKind.Double, DividerKind.Dotted }) RichEdit.InsertDivider(rtb, k);
        Console.WriteLine("     antes de salvar: " + string.Join(" | ", rtb.Document.Blocks.OfType<Paragraph>().Select(p => RichEdit.DividerKindOf(p)?.ToString() ?? "texto")));
        var rtf = Rtf(rtb.Document);
        Console.WriteLine("     RTF (trecho): " + rtf.Substring(Math.Max(0, rtf.IndexOf("\\pard")), Math.Min(400, rtf.Length - Math.Max(0, rtf.IndexOf("\\pard")))).Replace("\r\n", " "));
        var reloaded = Reload(rtf);
        Console.WriteLine("     depois de reabrir: " + string.Join(" | ", reloaded.Blocks.OfType<Paragraph>().Select(p =>
            RichEdit.DividerKindOf(p)?.ToString() ?? $"texto[{p.Inlines.Count} inlines: {string.Join(",", p.Inlines.Select(i => i.GetType().Name + (i is Run r ? $"'{r.Text}' {(r.Foreground as System.Windows.Media.SolidColorBrush)?.Color}" : "")))}]")));
        var kinds = reloaded.Blocks.OfType<Paragraph>().Select(RichEdit.DividerKindOf).Where(k => k != null).ToList();
        Check(kinds.SequenceEqual(new DividerKind?[] { DividerKind.Thin, DividerKind.Double, DividerKind.Dotted }),
              $"as 3 divisórias voltam iguais ({string.Join(", ", kinds)})");
        var dotted = reloaded.Blocks.OfType<Paragraph>().First(p => RichEdit.DividerKindOf(p) == DividerKind.Dotted);
        Check(dotted.BorderBrush is System.Windows.Media.DrawingBrush, "a pontilhada volta pontilhada");

        // ---------------- 3. tabela ----------------
        Console.WriteLine("3) Tabela: inserir, mesclar, linhas/colunas");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        var data = new List<IReadOnlyList<string>> { new[] { "A1", "B1", "C1" }, new[] { "A2", "B2", "C2" }, new[] { "A3", "B3", "C3" } };
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(3, 3, data));
        var table = rtb.Document.Blocks.OfType<Table>().Single();
        Console.WriteLine("     " + Describe(table));
        Check(table.NextBlock is Paragraph, "tem parágrafo depois da tabela");

        var g = new RichEdit.Grid(table);
        rtb.Selection.Select(g.At(0, 0).ContentStart, g.At(1, 1).ContentEnd);
        RichEdit.MergeSelection(rtb);
        Console.WriteLine("     mesclar A1..B2: " + Describe(table));
        g = new RichEdit.Grid(table);
        Check(g.At(0, 0) == g.At(1, 1) && g.At(0, 0).RowSpan == 2 && g.At(0, 0).ColumnSpan == 2 && g.Rows == 3 && g.Cols == 3, "mesclou 2x2");

        RichEdit.InsertRow(rtb, g.At(0, 2), below: true);           // nova linha entre 1 e 2: a mesclada deve esticar
        g = new RichEdit.Grid(table);
        Console.WriteLine("     linha abaixo de C1: " + Describe(table));
        Check(g.Rows == 4 && g.At(0, 0).RowSpan == 3, "inserir linha no meio estica a célula mesclada");

        RichEdit.InsertColumn(rtb, g.At(0, 2), right: true);
        g = new RichEdit.Grid(table);
        Console.WriteLine("     coluna à direita de C1: " + Describe(table));
        Check(g.Cols == 4 && g.Group.Rows.All(r => r.Cells.Sum(c => c.ColumnSpan) + 0 >= 1), "inseriu coluna (4 colunas)");

        RichEdit.InsertColumn(rtb, g.At(3, 0), right: true);         // à direita de A3 = no meio da mesclada: deve alargar
        g = new RichEdit.Grid(table);
        Console.WriteLine("     coluna à direita de A3: " + Describe(table));
        Check(g.Cols == 5 && g.At(0, 0).ColumnSpan == 3, "coluna no meio da mesclada alarga ela");

        RichEdit.DeleteColumn(rtb, g.At(0, 4));
        RichEdit.DeleteRow(rtb, new RichEdit.Grid(table).At(1, 3));   // linha do meio: a mesclada encolhe
        g = new RichEdit.Grid(table);
        Console.WriteLine("     depois de excluir col/linha: " + Describe(table));
        Check(g.Rows == 3 && g.Cols == 4 && g.At(0, 0).RowSpan == 2, "excluir coluna e linha");

        var before = Describe(table);
        var reloadedT = Reload(Rtf(rtb.Document)).Blocks.OfType<Table>().Single();
        Console.WriteLine("     depois de salvar/reabrir: " + Describe(reloadedT));
        Check(Describe(reloadedT) == before, "tabela com mesclagem sobrevive ao salvar/reabrir");

        RichEdit.Unmerge(rtb, g.At(0, 0));
        g = new RichEdit.Grid(table);
        Console.WriteLine("     desfazer mesclagem: " + Describe(table));
        Check(g.Rows == 3 && g.Cols == 4 && g.Group.Rows.All(r => r.Cells.Count == 4), "desfazer mesclagem volta a 3x4 completo");

        ApplicationCommands.Undo.Execute(null, rtb);
        Check(Describe(table) == before, "Ctrl+Z desfaz a última operação de tabela");

        // ---------------- 3b. mesclada ocupando a altura toda ----------------
        Console.WriteLine("3b) Mesclada ocupando a altura toda da tabela");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(2, 3, new List<IReadOnlyList<string>> { new[] { "A", "B", "C" }, new[] { "D", "E", "F" } }, 240));
        var t2 = rtb.Document.Blocks.OfType<Table>().Single();
        var g2 = new RichEdit.Grid(t2);
        rtb.Selection.Select(g2.At(0, 0).ContentStart, g2.At(1, 1).ContentEnd);
        RichEdit.MergeSelection(rtb);
        rtb.UpdateLayout();
        var re2 = Reload(Rtf(rtb.Document)).Blocks.OfType<Table>().Single();
        var w = re2.Columns.Select(c => c.Width.Value).ToList();
        Console.WriteLine($"     antes: {Describe(t2)}\n     depois: {Describe(re2)}  larguras: {string.Join(" / ", w.Select(x => x.ToString("0")))}");
        Check(re2.Columns.Count >= 2 && w[0] > w[^1] * 1.5, "a parte mesclada continua com o dobro da largura");

        // ---------------- amostra para ver na tela ----------------
        rtb.Document = new FlowDocument(new Paragraph(new Run("Compras do mês")));
        rtb.CaretPosition = rtb.Document.ContentEnd;
        EditingCommands.EnterParagraphBreak.Execute(null, rtb);
        RichEdit.InsertDivider(rtb, DividerKind.Thin);
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(3, 3, new List<IReadOnlyList<string>> {
            new[] { "Item", "Qtd", "Preço" }, new[] { "Café", "2", "18,90" }, new[] { "Leite", "6", "5,49" } }));
        rtb.CaretPosition = rtb.Document.ContentEnd;
        Type(rtb, "Fina, dupla e pontilhada:");
        EditingCommands.EnterParagraphBreak.Execute(null, rtb);
        RichEdit.InsertDivider(rtb, DividerKind.Thin);
        RichEdit.InsertDivider(rtb, DividerKind.Double);
        RichEdit.InsertDivider(rtb, DividerKind.Dotted);
        Type(rtb, "fim");
        File.WriteAllText(@"C:\workspace\MyPIN\_testdata\rich.rtf", Rtf(rtb.Document), Encoding.Latin1);
        var rawDoc = new FlowDocument();
        new TextRange(rawDoc.ContentStart, rawDoc.ContentEnd).Load(new MemoryStream(Encoding.Latin1.GetBytes(Rtf(rtb.Document))), DataFormats.Rtf);
        var rawT = rawDoc.Blocks.OfType<Table>().First();
        Console.WriteLine($"     colunas ao reabrir (antes do estilo): {rawT.Columns.Count} -> " +
            string.Join(" / ", rawT.Columns.Select(c => $"{c.Width.Value}{c.Width.GridUnitType}")));
        RichEdit.Normalize(rawDoc);
        Console.WriteLine($"     depois do estilo: " + string.Join(" / ", rawT.Columns.Select(c => $"{c.Width.Value}{c.Width.GridUnitType}")) +
            $" | células por linha: {string.Join(",", rawT.RowGroups[0].Rows.Select(r => r.Cells.Count))}");
        rtb.Document = rawDoc;
        rtb.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(300, 300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(rtb);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = File.Create(@"C:\workspace\MyPIN\_testdata\shots\32_tabela_harness.png")) enc.Save(fs);
        Console.WriteLine("6) Bordas para redimensionar (inclusive as de fora)");
        var lay = RichEdit.Measure(rawT);
        Console.WriteLine($"     bordas verticais em x = {string.Join(" / ", lay.Edges.Select(x => x.ToString("0")))};" +
                          $" linhas em y = {string.Join(" / ", lay.RowTop.Select(y => y.ToString("0")))}; fim y = {lay.Bottom:0}");
        double h = lay.RowTop[1] - lay.RowTop[0], mid = lay.RowTop[0] + h / 2;
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[0], mid)) is { Kind: RichEdit.ResizeKind.Column, Index: 0 }, "borda ESQUERDA da tabela é reconhecida");
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[1] + 1, mid)) is { Kind: RichEdit.ResizeKind.Column, Index: 1 }, "borda entre colunas 1 e 2 é reconhecida");
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[^1] - 1, mid)) is { Kind: RichEdit.ResizeKind.Column } hr && hr.Index == lay.Edges.Length - 1, "borda DIREITA da tabela é reconhecida");
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[0] + 30, lay.RowTop[1] - 1)) is { Kind: RichEdit.ResizeKind.Row, Index: 0 }, "borda embaixo da 1ª linha é reconhecida");
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[0] + 30, lay.Bottom)) is { Kind: RichEdit.ResizeKind.Row, Index: 2 }, "borda embaixo da última linha é reconhecida");
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[0] + 30, mid)) == null, "no meio da célula não é borda (texto normal)");
        Check(RichEdit.HitTestResize(rtb, new Point(lay.Edges[^1] + 30, mid)) == null, "fora da tabela (à direita) não é borda");

        Console.WriteLine("10) Setinha para selecionar linha/coluna inteira");
        rtb.Document = new FlowDocument(new Paragraph(new Run("título")));
        rtb.CaretPosition = rtb.Document.ContentEnd;
        EditingCommands.EnterParagraphBreak.Execute(null, rtb);
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(3, 3, new List<IReadOnlyList<string>> {
            new[] { "a", "b", "c" }, new[] { "d", "e", "f" }, new[] { "g", "h", "i" } }, 240));
        rtb.UpdateLayout();
        var ts = rtb.Document.Blocks.OfType<Table>().Single();
        var ls = RichEdit.MeasureAll(rtb.Document);
        var lay2 = ls.Single().layout;
        double rowMid1 = (lay2.RowTop[1] + lay2.RowTop[2]) / 2, colMid2 = (lay2.Edges[2] + lay2.Edges[3]) / 2;
        Console.WriteLine($"     tabela começa em x={lay2.Left:0}, y={lay2.Top:0}");
        var hRow = RichEdit.HitTestSelect(ls, new Point(lay2.Left - 8, rowMid1));
        var hCol = RichEdit.HitTestSelect(ls, new Point(colMid2, lay2.Top - 5));
        Check(hRow is { Kind: RichEdit.SelectKind.Row, Index: 1 }, "à esquerda da 2ª linha: setinha de linha");
        Check(hCol is { Kind: RichEdit.SelectKind.Column, Index: 2 }, "acima da 3ª coluna: setinha de coluna");
        Check(RichEdit.HitTestSelect(ls, new Point(lay2.Left + 20, rowMid1)) == null, "dentro da célula: nada (texto normal)");
        Check(lay2.Left >= 10, $"tabela nova tem recuo (espaço para a setinha): começa em x={lay2.Left:0}");

        RichEdit.ToggleSelect(rtb, hRow);
        Check(RichEdit.SelectedRect(rtb) is { r1: 1, c1: 0, r2: 1, c2: 2 }, "clique seleciona a linha inteira");
        RichEdit.ToggleSelect(rtb, hRow);
        Check(rtb.Selection.IsEmpty, "clique de novo desmarca");
        RichEdit.ToggleSelect(rtb, hCol);
        Check(RichEdit.SelectedRect(rtb) is { r1: 0, c1: 2, r2: 2, c2: 2 }, "clique acima seleciona a coluna inteira");
        RichEdit.ToggleSelect(rtb, hCol);
        Check(rtb.Selection.IsEmpty, "e desmarca no segundo clique");

        Console.WriteLine("11) Selecionar linha inteira em tabela com mesclagem");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(4, 5, new List<IReadOnlyList<string>> {
            new[] { "a1", "b1", "c1", "d1", "e1" }, new[] { "a2", "b2", "c2", "d2", "e2" },
            new[] { "a3", "b3", "c3", "d3", "e3" }, new[] { "a4", "b4", "c4", "d4", "e4" } }, 260));
        var tmm = rtb.Document.Blocks.OfType<Table>().Single();
        var gmm = new RichEdit.Grid(tmm);
        rtb.Selection.Select(gmm.At(0, 0).ContentStart, gmm.At(2, 0).ContentEnd);   // 1ª coluna: 3 linhas mescladas
        RichEdit.MergeSelection(rtb);
        rtb.UpdateLayout();
        Console.WriteLine("     tabela: " + Describe(tmm));
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Row, tmm, 3));
        var selText = rtb.Selection.Text.Replace("\r\n", " ").Replace("\t", " ").Trim();
        Console.WriteLine($"     texto selecionado (última linha): '{selText}'");
        Check(new[] { "a4", "b4", "c4", "d4", "e4" }.All(selText.Contains), "a seleção da última linha pega as 5 células");
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Row, tmm, 1));
        selText = rtb.Selection.Text.Replace("\r\n", " ").Replace("\t", " ").Trim();
        Console.WriteLine($"     texto selecionado (2ª linha): '{selText}'");
        Check(new[] { "b2", "c2", "d2", "e2" }.All(selText.Contains), "a seleção da 2ª linha pega as células dela");

        Console.WriteLine("13) O que o WPF PINTA ao selecionar a linha (última célula vazia)");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(3, 4, new List<IReadOnlyList<string>> {
            new[] { "a", "b", "c", "" }, new[] { "d", "e", "f", "g" }, new[] { "h", "i", "j", "" } }, 240));
        rtb.UpdateLayout();
        var tv = rtb.Document.Blocks.OfType<Table>().Single();
        // segmentos que o WPF pinta (propriedade interna ITextRange.TextSegments)
        int PaintedCells(Table table, int row)
        {
            var itr = rtb.Selection.GetType().GetInterfaces().First(i => i.Name == "ITextRange");
            var segs = (System.Collections.IEnumerable)itr.GetProperty("TextSegments").GetValue(rtb.Selection);
            var ranges = new List<(TextPointer s, TextPointer e)>();
            foreach (var seg in segs)
            {
                var st = seg.GetType();
                const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                ranges.Add(((TextPointer)st.GetProperty("Start", bf).GetValue(seg), (TextPointer)st.GetProperty("End", bf).GetValue(seg)));
            }
            // a célula conta se algum trecho pintado se sobrepõe a ela
            return table.RowGroups[0].Rows[row].Cells.Count(c => ranges.Any(r =>
                r.s.CompareTo(c.ElementEnd) < 0 && r.e.CompareTo(c.ContentEnd) >= 0 || r.s.CompareTo(c.ContentStart) <= 0 && r.e.CompareTo(c.ContentStart) > 0
                || r.s.CompareTo(c.ContentStart) >= 0 && r.s.CompareTo(c.ContentEnd) < 0));
        }
        var gv = new RichEdit.Grid(tv);
        var row0 = tv.RowGroups[0].Rows[0];
        var c0 = gv.At(0, 0); var c3 = gv.At(0, 3);
        foreach (var (name, s, e) in new (string, TextPointer, TextPointer)[]
                 {
                     ("ElementStart → row.ContentEnd", c0.ElementStart, row0.ContentEnd),
                     ("ElementStart → row.ElementEnd", c0.ElementStart, row0.ElementEnd),
                     ("ContentStart → c3.ContentStart", c0.ContentStart, c3.ContentStart),
                     ("ContentStart → c3 parágrafo.ContentEnd", c0.ContentStart, ((Paragraph)c3.Blocks.FirstBlock).ContentEnd),
                     ("ContentStart → c3 parágrafo.ElementEnd", c0.ContentStart, c3.Blocks.FirstBlock.ElementEnd),
                     ("row.ContentStart → row.ContentEnd", row0.ContentStart, row0.ContentEnd),
                     ("c3 → c0 (de trás p/ frente)", c3.ContentEnd, c0.ContentStart),
                 })
        {
            rtb.Selection.Select(s, e);
            Console.WriteLine($"     teste {name}: WPF pinta {PaintedCells(tv, 0)} de 4");
        }
        // calibração: linha 1 tem texto na última célula
        rtb.Selection.Select(gv.At(1, 0).ContentStart, gv.At(1, 3).ContentEnd);
        Console.WriteLine($"     calibração (linha com 'g' no fim): WPF pinta {PaintedCells(tv, 1)} de 4");
        int Off(TextPointer p) => rtb.Document.ContentStart.GetOffsetToPosition(p);
        {
            var itr = rtb.Selection.GetType().GetInterfaces().First(i => i.Name == "ITextRange");
            const System.Reflection.BindingFlags bf2 = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            rtb.Selection.Select(c0.ElementStart, c3.ElementEnd);
            foreach (var seg in (System.Collections.IEnumerable)itr.GetProperty("TextSegments").GetValue(rtb.Selection))
                Console.WriteLine($"     segmento pintado: {Off((TextPointer)seg.GetType().GetProperty("Start", bf2).GetValue(seg))}..{Off((TextPointer)seg.GetType().GetProperty("End", bf2).GetValue(seg))}");
            foreach (var c in row0.Cells) Console.WriteLine($"     célula: elemento {Off(c.ElementStart)}..{Off(c.ElementEnd)}, texto {Off(c.ContentStart)}..{Off(c.ContentEnd)}");
        }
        rtb.Selection.Select(gv.At(0, 0).ContentStart, gv.At(0, 3).ContentEnd);
        Console.WriteLine($"     jeito antigo (pelo texto): WPF pinta {PaintedCells(tv, 0)} de 4 células");
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Row, tv, 0));
        int painted = PaintedCells(tv, 0);
        Console.WriteLine($"     jeito novo (pelas bordas): WPF pinta {painted} de 4 células");
        Check(painted == 4, "WPF pinta as 4 células da linha (inclusive a vazia)");
        rtb.Selection.Select(rtb.Document.ContentStart, rtb.Document.ContentStart);
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Row, tv, 2));
        Check(PaintedCells(tv, 2) == 4, "também na última linha (com célula vazia no fim)");

        Console.WriteLine("14) Pixel a pixel: quais células ficam AZUIS na tela");
        rtb.IsInactiveSelectionHighlightEnabled = true;
        rtb.Focus();
        string Blue(Table table, int row, string label)
        {
            rtb.UpdateLayout();
            var lay = RichEdit.Measure(table);
            var host = Window.GetWindow(rtb);
            host.UpdateLayout();
            // deixa o WPF desenhar a seleção (é feito de forma assíncrona)
            for (int k = 0; k < 5; k++)
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var w = (int)host.ActualWidth; var h = (int)host.ActualHeight;
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(host); // a janela inteira inclui a camada onde o WPF pinta a seleção
            var px = new byte[w * h * 4]; bmp.CopyPixels(px, w * 4, 0);
            double yMid = (lay.RowTop[row] + (row + 1 < lay.RowTop.Length ? lay.RowTop[row + 1] : lay.Bottom)) / 2;
            var res = "";
            int blues = 0;
            for (int c = 0; c < lay.Edges.Length - 1; c++)
            {
                int x = (int)((lay.Edges[c] + lay.Edges[c + 1]) / 2) + 6, y = (int)yMid;   // +6: um pouco à direita do meio (longe do texto)
                int i = (y * w + x) * 4;
                byte b = px[i], gg = px[i + 1], r = px[i + 2];
                bool isBlue = b > r + 30;
                if (isBlue) blues++;
                res += isBlue ? "■" : "□";
            }
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create($@"C:\workspace\MyPIN\_testdata\shots\40_{label}.png")) enc.Save(fs);
            Console.WriteLine($"     {label}: {res}  ({blues} azuis)");
            return res;
        }
        rtb.Selection.Select(gv.At(0, 0).ContentStart, gv.At(0, 3).ContentEnd);
        Blue(tv, 0, "antigo_pelo_texto");
        rtb.Selection.Select(rtb.Document.ContentStart, rtb.Document.ContentStart);
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Row, tv, 0));
        var novo = Blue(tv, 0, "novo_pelas_bordas");
        Check(novo == "■■■■", "na tela, as 4 células da linha ficam azuis (inclusive a vazia)");
        rtb.Selection.Select(rtb.Document.ContentStart, rtb.Document.ContentStart);
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Column, tv, 3));
        var colRes = string.Concat(Enumerable.Range(0, 3).Select(r => Blue(tv, r, $"coluna_linha{r}").Substring(3, 1)));
        Check(colRes == "■■■", $"na tela, a coluna inteira (com células vazias) fica azul: {colRes}");

        Console.WriteLine("15) Limite ao colar");
        var sizeRtf = RichEdit.RtfTableSize(Rtf(rtb.Document));
        Check(sizeRtf == (3, 4), $"conta o tamanho de uma tabela num RTF (3×4): deu {sizeRtf.rows}×{sizeRtf.cols}");
        Check(RichEdit.RtfTableSize(@"{\rtf1\ansi texto sem tabela\par}") == (0, 0), "RTF sem tabela: 0×0");
        var big = string.Join("\r\n", Enumerable.Range(1, 60).Select(r => string.Join("\t", Enumerable.Range(1, 12).Select(c => $"{r}-{c}"))));
        var bigRows = RichEdit.ParseTsv(big);
        Check(bigRows.Count == 60 && bigRows.Max(r => r.Count) == 12, "planilha de 60×12 é medida certo (passa do limite de 50×10)");

        Console.WriteLine("18) Cursor dentro de célula mesclada, sem seleção: nada para mesclar");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(2, 3, new List<IReadOnlyList<string>> { new[] { "a", "b", "c" }, new[] { "d", "e", "f" } }, 240));
        var tq = rtb.Document.Blocks.OfType<Table>().Single();
        var gq = new RichEdit.Grid(tq);
        rtb.Selection.Select(gq.At(0, 0).ElementStart, gq.At(1, 1).ElementEnd); RichEdit.MergeSelection(rtb);
        gq = new RichEdit.Grid(tq);
        var inside = gq.At(0, 0).ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        rtb.Selection.Select(inside, inside);
        Check(RichEdit.SelectedRect(rtb) == null, "cursor numa mesclada (sem selecionar): Mesclar fica desabilitado");
        Check(RichEdit.MergedCellsToSplit(rtb, gq.At(0, 0)).Count == 1, "...e Desmesclar fica habilitado");
        rtb.Selection.Select(gq.At(0, 2).ElementStart, gq.At(1, 2).ElementEnd);
        Check(RichEdit.SelectedRect(rtb) != null, "2 células normais selecionadas: Mesclar habilitado");

        Console.WriteLine("19) Minimizar e restaurar tabela");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(4, 3, new List<IReadOnlyList<string>> {
            new[] { "a", "b", "c" }, new[] { "d", "e", "f" }, new[] { "g", "h", "i" }, new[] { "j", "k", "l" } }, 240));
        var tc = rtb.Document.Blocks.OfType<Table>().Single();
        var gc = new RichEdit.Grid(tc);
        rtb.Selection.Select(gc.At(0, 0).ElementStart, gc.At(2, 0).ElementEnd); RichEdit.MergeSelection(rtb);   // 1ª coluna: 3 linhas
        var full = Describe(tc);
        Console.WriteLine("     original:   " + full);
        var info = RichEdit.Collapse(rtb, tc);
        Console.WriteLine("     minimizada: " + Describe(tc));
        Check(info != null && tc.RowGroups[0].Rows.Count == 1, "minimizada: fica só a 1ª linha");
        Check(tc.RowGroups[0].Rows[0].Cells.All(c => c.RowSpan <= 1), "a mesclada da 1ª linha vira 1 linha enquanto minimizada");

        var savedRtf = RichEdit.ToRtf(rtb.Document, t => t == tc ? info : null);
        var reopened = Reload(savedRtf).Blocks.OfType<Table>().Single();
        Console.WriteLine("     salva:      " + Describe(reopened));
        Check(Describe(reopened) == full, "ao salvar minimizada, o arquivo guarda a tabela COMPLETA");
        Check(tc.RowGroups[0].Rows.Count == 1, "...e a tabela na tela continua minimizada");

        RichEdit.Expand(rtb, tc, info);
        Console.WriteLine("     restaurada: " + Describe(tc));
        Check(Describe(tc) == full, "restaurar volta exatamente como era (inclusive a mesclagem)");

        RichEdit.Collapse(rtb, tc);
        ApplicationCommands.Undo.Execute(null, rtb);
        Check(Describe(tc) == full, "Ctrl+Z depois de minimizar traz as linhas de volta");

        Console.WriteLine("17) Prévia dos ícones de tabela");
        {
            var iconsType = typeof(MyPIN.NoteWindow).GetNestedType("TableIcons", System.Reflection.BindingFlags.NonPublic);
            var panel = new StackPanel { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2C)) };
            foreach (var f in iconsType.GetFields())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
                var path = new System.Windows.Shapes.Path
                {
                    Data = System.Windows.Media.Geometry.Parse((string)f.GetValue(null)), Stroke = System.Windows.Media.Brushes.White,
                    StrokeThickness = 1.1, StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round, StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                };
                row.Children.Add(new Viewbox { Width = 30, Height = 30, Child = new Canvas { Width = 16, Height = 16, Children = { path } } });
                row.Children.Add(new TextBlock { Text = "  " + f.Name, Foreground = System.Windows.Media.Brushes.White, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(row);
            }
            panel.Measure(new Size(300, 1000)); panel.Arrange(new Rect(panel.DesiredSize));
            var ib = new System.Windows.Media.Imaging.RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            ib.Render(panel);
            var ie = new System.Windows.Media.Imaging.PngBitmapEncoder(); ie.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(ib));
            using (var fs = File.Create(@"C:\workspace\MyPIN\_testdata\shots\41_icones_tabela.png")) ie.Save(fs);
        }

        Console.WriteLine("16) Desmesclar células (depois de salvar e reabrir)");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(4, 4, new List<IReadOnlyList<string>> {
            new[] { "a", "b", "c", "d" }, new[] { "e", "f", "g", "h" }, new[] { "i", "j", "k", "l" }, new[] { "m", "n", "o", "p" } }, 240));
        var tu = rtb.Document.Blocks.OfType<Table>().Single();
        var gu = new RichEdit.Grid(tu);
        rtb.Selection.Select(gu.At(0, 0).ElementStart, gu.At(1, 1).ElementEnd); RichEdit.MergeSelection(rtb);   // 2x2
        gu = new RichEdit.Grid(tu);
        rtb.Selection.Select(gu.At(3, 2).ElementStart, gu.At(3, 3).ElementEnd); RichEdit.MergeSelection(rtb);   // 1x2 embaixo
        rtb.UpdateLayout();
        rtb.Document = Reload(RichEdit.ToRtf(rtb.Document));                                                     // salva e reabre
        rtb.UpdateLayout();
        tu = rtb.Document.Blocks.OfType<Table>().Single();
        Console.WriteLine("     reaberta: " + Describe(tu));
        gu = new RichEdit.Grid(tu);
        RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Column, tu, 0));                   // seleciona 1ª coluna
        rtb.Selection.Select(gu.At(0, 0).ElementStart, gu.At(3, 3).ElementEnd);                                   // tabela inteira
        var toSplit = RichEdit.MergedCellsToSplit(rtb, null);
        Check(toSplit.Count == 2, $"acha as 2 mescladas na seleção ({toSplit.Count})");
        var beforeSplit = Describe(tu);
        RichEdit.UnmergeAll(rtb, toSplit);
        gu = new RichEdit.Grid(tu);
        Console.WriteLine("     desmesclada: " + Describe(tu));
        Check(gu.Rows == 4 && gu.Cols == 4 && gu.Group.Rows.All(r => r.Cells.Count == 4 && r.Cells.All(c => c.RowSpan <= 1 && c.ColumnSpan <= 1)),
              "todas viram células normais (4×4 completo)");
        ApplicationCommands.Undo.Execute(null, rtb);
        Check(Describe(tu) == beforeSplit, "Ctrl+Z volta as duas mesclagens de uma vez");
        gu = new RichEdit.Grid(tu);
        rtb.Selection.Select(gu.At(2, 2).ContentStart, gu.At(2, 2).ContentStart);                               // sem seleção
        var only = RichEdit.MergedCellsToSplit(rtb, gu.At(3, 2));
        RichEdit.UnmergeAll(rtb, only);
        gu = new RichEdit.Grid(tu);
        Check(only.Count == 1 && gu.At(0, 0).RowSpan == 2 && gu.Group.Rows[3].Cells.Count == 4, "clicando numa mesclada: desmescla só ela");

        Console.WriteLine("12) Tabelas da nota real 'Teste' (cópia, só leitura; pula se não houver cópia)");
        var userPath = @"C:\workspace\MyPIN\_testdata\user_note.rtf";
        var userRtf = File.Exists(userPath) ? File.ReadAllText(userPath, Encoding.Latin1) : @"{\rtf1\ansi }";
        rtb.Document = Reload(userRtf);
        rtb.UpdateLayout();
        if (rtb.Document.Blocks.OfType<Table>().FirstOrDefault() is { } ut0)
        {
            var lu = RichEdit.MeasureAll(rtb.Document);
            var lay0 = lu.First(x => x.table == ut0).layout;
            var merged = new RichEdit.Grid(ut0).At(0, 0);
            // clique na parte de BAIXO da célula mesclada alta (3ª linha dela)
            var pLow = new Point((lay0.Edges[0] + lay0.Edges[1]) / 2, (lay0.RowTop[2] + lay0.RowTop[3]) / 2);
            var hitCell = RichEdit.CellAtPoint(lu, pLow);
            var snapCell = RichEdit.CellAt(rtb.GetPositionFromPoint(pLow, true));
            Console.WriteLine($"     clique embaixo da mesclada: pela geometria = {(hitCell == merged ? "a mesclada" : "outra")}; pelo texto mais próximo = {(snapCell == merged ? "a mesclada" : "OUTRA célula")}");
            Check(hitCell == merged && RichEdit.MergedCellsToSplit(rtb, hitCell).Count == 1, "clique na parte de baixo da mesclada: acha ela e oferece desmesclar");
        }
        foreach (var ut in rtb.Document.Blocks.OfType<Table>())
        {
            var ug = new RichEdit.Grid(ut);
            Console.WriteLine($"     tabela {ug.Rows}x{ug.Cols} (colunas declaradas: {ut.Columns.Count}): {Describe(ut)}");
            for (int r = 0; r < ug.Rows; r++)
            {
                RichEdit.ToggleSelect(rtb, new RichEdit.SelectHit(RichEdit.SelectKind.Row, ut, r));
                var cellsSel = ug.Group.Rows.SelectMany(x => x.Cells).Count(c =>
                    rtb.Selection.Contains(c.ContentStart.GetInsertionPosition(LogicalDirection.Forward)));
                var rect = RichEdit.SelectedRect(rtb);
                Console.WriteLine($"       linha {r}: seleção cobre {cellsSel} células; linhas {rect?.r1}..{rect?.r2}, colunas {rect?.c1}..{rect?.c2}");
                Check(rect?.c2 == ug.Cols - 1, $"  linha {r} vai até a última coluna ({ug.Cols - 1})");
                rtb.Selection.Select(rtb.Document.ContentStart, rtb.Document.ContentStart);
            }
        }

        Console.WriteLine("5) Larguras (pixels), recuo e altura de linha depois de salvar/reabrir");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(2, 2, width: 200));
        var tw = rtb.Document.Blocks.OfType<Table>().Single();
        Check(tw.Columns.All(c => c.Width.IsAbsolute && Math.Abs(c.Width.Value - 96) < 1), "tabela nova: colunas em pixels (200 - 8 de recuo = 2 × 96)");
        tw.Columns[0].Width = new GridLength(150); tw.Columns[1].Width = new GridLength(50);
        tw.Margin = new Thickness(20, 4, 0, 4);
        foreach (var c in tw.RowGroups[0].Rows[0].Cells) ((Paragraph)c.Blocks.LastBlock).Margin = new Thickness(0, 0, 0, 30);
        rtb.UpdateLayout();
        var reW = Reload(RichEdit.ToRtf(rtb.Document)).Blocks.OfType<Table>().Single();
        var ws = reW.Columns.Select(c => c.Width.Value).ToList();
        var extra = ((Paragraph)reW.RowGroups[0].Rows[0].Cells[0].Blocks.LastBlock).Margin.Bottom;
        var extra2 = ((Paragraph)reW.RowGroups[0].Rows[1].Cells[0].Blocks.LastBlock).Margin.Bottom;
        Console.WriteLine($"     larguras: {string.Join(" / ", ws.Select(x => x.ToString("0")))}  recuo: {reW.Margin.Left:0}  altura extra: {extra:0} / {extra2:0}");
        Check(Math.Abs(ws[0] - 150) < 1 && Math.Abs(ws[1] - 50) < 1, "larguras 150/50 voltam iguais");
        Check(Math.Abs(reW.Margin.Left - 20) < 1, "recuo à esquerda volta igual");
        Check(Math.Abs(extra - 30) < 1 && extra2 < 1, "altura extra da 1ª linha volta igual (e a 2ª continua normal)");

        var wide = new FlowDocument();
        wide.Blocks.Add(RichEdit.CreateTable(1, 3, width: 600));
        RichEdit.FitTables(wide, 240);
        var wt = wide.Blocks.OfType<Table>().Single();
        Check(wt.Columns.Sum(c => c.Width.Value) <= 240, $"tabela larga demais (600) é reduzida para caber na nota ({wt.Columns.Sum(c => c.Width.Value):0})");

        Console.WriteLine("7) Mesclar células lado a lado (seleção do mouse fica ENTRE as células)");
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(2, 3, new List<IReadOnlyList<string>> { new[] { "a", "b", "c" }, new[] { "d", "e", "f" } }));
        var tm = rtb.Document.Blocks.OfType<Table>().Single();
        var gm = new RichEdit.Grid(tm);
        rtb.Selection.Select(gm.At(1, 1).ElementStart, gm.At(1, 2).ElementEnd); // como o WPF faz com o mouse
        Console.WriteLine($"     seleção: início em {rtb.Selection.Start.Parent?.GetType().Name}, fim em {rtb.Selection.End.Parent?.GetType().Name}");
        Check(RichEdit.SelectedRect(rtb) is { r1: 1, c1: 1, r2: 1, c2: 2 }, "reconhece as 2 células lado a lado");
        RichEdit.MergeSelection(rtb);
        gm = new RichEdit.Grid(tm);
        Check(gm.At(1, 1) == gm.At(1, 2) && gm.At(1, 1).ColumnSpan == 2, "mesclou na horizontal");

        Console.WriteLine("8) Enter no fim da tabela sai dela");
        rtb.Document = new FlowDocument(new Paragraph(new Run("antes")));
        rtb.CaretPosition = rtb.Document.ContentEnd;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(2, 2, new List<IReadOnlyList<string>> { new[] { "1", "2" }, new[] { "3", "4" } }));
        var te = rtb.Document.Blocks.OfType<Table>().Single();
        var ge = new RichEdit.Grid(te);
        rtb.CaretPosition = ge.At(1, 1).ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        bool exited = RichEdit.TryExitTableOnEnter(rtb);
        Check(exited && RichEdit.CellAt(rtb.CaretPosition) == null && rtb.CaretPosition.Paragraph == te.NextBlock, "no fim da última célula: cursor vai para depois da tabela");
        Check(new RichEdit.Grid(te).Rows == 2, "a tabela continua com 2 linhas");
        rtb.CaretPosition = ge.At(0, 0).ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        Check(!RichEdit.TryExitTableOnEnter(rtb), "no meio da tabela o Enter continua normal (nova linha dentro da célula)");

        Console.WriteLine("9) Del/Backspace apagam a divisória inteira");
        rtb.Document = new FlowDocument(new Paragraph(new Run("cima")));
        rtb.CaretPosition = rtb.Document.ContentEnd;
        EditingCommands.EnterParagraphBreak.Execute(null, rtb);
        Type(rtb, "---");
        RichEdit.TryConvertDividerOnEnter(rtb);                            // vira: cima / divisória / (linha vazia)
        Type(rtb, "baixo");
        var baixo = rtb.CaretPosition.Paragraph;
        int Divs() => rtb.Document.Blocks.OfType<Paragraph>().Count(p => RichEdit.DividerKindOf(p) != null);
        Check(Divs() == 1, "divisória inserida entre as linhas");
        rtb.CaretPosition = baixo.ContentStart;
        Check(RichEdit.TryDeleteDivider(rtb, forward: false) && Divs() == 0, "Backspace no começo da linha de baixo apaga a divisória");
        ApplicationCommands.Undo.Execute(null, rtb);
        var cima = (Paragraph)rtb.Document.Blocks.FirstBlock;
        rtb.CaretPosition = cima.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        Check(Divs() == 1 && RichEdit.TryDeleteDivider(rtb, forward: true) && Divs() == 0, "Del no fim da linha de cima apaga a divisória");
        Check(Text(rtb.Document.Blocks.FirstBlock) == "cima" && Text(rtb.Document.Blocks.LastBlock) == "baixo", "o texto em volta fica intacto");

        // tabela seguida de divisória: Del no fim da última célula apaga a divisória
        rtb.Document = new FlowDocument(new Paragraph());
        rtb.CaretPosition = rtb.Document.ContentStart;
        RichEdit.InsertTable(rtb, RichEdit.CreateTable(1, 2, new List<IReadOnlyList<string>> { new[] { "x", "y" } }));
        var tdel = rtb.Document.Blocks.OfType<Table>().Single();
        rtb.CaretPosition = ((Paragraph)tdel.NextBlock).ContentStart;
        RichEdit.InsertDivider(rtb, DividerKind.Thin);                     // tabela / divisória / linha vazia
        var lastCell = new RichEdit.Grid(tdel).At(0, 1);
        rtb.CaretPosition = lastCell.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
        Check(Divs() == 1 && RichEdit.TryDeleteDivider(rtb, forward: true) && Divs() == 0, "Del no fim da tabela apaga a divisória logo abaixo");
        Check(rtb.Document.Blocks.LastBlock is Paragraph, "continua havendo uma linha para escrever depois da tabela");

        // ---------------- 4. Excel ----------------
        Console.WriteLine("4) Texto copiado do Excel");
        var rows = RichEdit.ParseTsv("Nome\tValor\r\nCafé\t10,50\r\n\"linha 1\nlinha 2\"\t\"com \"\"aspas\"\"\"\r\n");
        Check(rows.Count == 3 && rows[0].SequenceEqual(new[] { "Nome", "Valor" }) && rows[2][0] == "linha 1\nlinha 2" && rows[2][1] == "com \"aspas\"",
              $"lê linhas, colunas, quebras e aspas ({rows.Count} linhas)");
    }
}
