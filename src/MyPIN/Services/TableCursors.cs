using System.IO;
using System.Windows.Input;
using Drawing = System.Drawing;
using Drawing2D = System.Drawing.Drawing2D;

namespace MyPIN.Services;

/// <summary>
/// Setinhas para selecionar linha (➜) e coluna (⬇) da tabela, como no Excel/Word. O Windows não
/// tem esses cursores prontos, então eles são desenhados aqui (pretos com contorno branco).
/// </summary>
public static class TableCursors
{
    public static readonly Cursor SelectRow = Make(rotate: 0);    // aponta para a direita
    public static readonly Cursor SelectColumn = Make(rotate: 90); // aponta para baixo

    static Cursor Make(float rotate)
    {
        var data = RenderPng(rotate);
        // ponta da seta (o ponto que "clica")
        short hotX = (short)(rotate == 0 ? 22 : 16), hotY = (short)(rotate == 0 ? 16 : 22);

        // arquivo .cur em memória (igual a um .ico, mas com o ponto de clique)
        using var cur = new MemoryStream();
        using var w = new BinaryWriter(cur);
        w.Write((short)0); w.Write((short)2); w.Write((short)1);       // cabeçalho: tipo 2 = cursor
        w.Write((byte)32); w.Write((byte)32); w.Write((byte)0); w.Write((byte)0);
        w.Write(hotX); w.Write(hotY);
        w.Write(data.Length); w.Write(6 + 16);
        w.Write(data);
        w.Flush();
        cur.Position = 0;
        return new Cursor(cur, scaleWithDpi: true);
    }

    /// <summary>Desenho da seta (PNG 32×32). Público para dar para ver/testar o desenho.</summary>
    public static byte[] RenderPng(float rotate)
    {
        const int size = 32;
        using var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);
            // seta pequena apontando para a direita (haste + ponta), ponta em (22,16)
            var arrow = new[]
            {
                new Drawing.PointF(10, 14.5f), new Drawing.PointF(16, 14.5f), new Drawing.PointF(16, 10.5f),
                new Drawing.PointF(22, 16), new Drawing.PointF(16, 21.5f), new Drawing.PointF(16, 17.5f),
                new Drawing.PointF(10, 17.5f),
            };
            g.TranslateTransform(16, 16);
            g.RotateTransform(rotate);
            g.TranslateTransform(-16, -16);
            using var path = new Drawing2D.GraphicsPath();
            path.AddPolygon(arrow);
            // contorno branco fino (para não sumir no fundo) e cinza no tom das linhas da tabela
            using (var white = new Drawing.Pen(Drawing.Color.FromArgb(220, 255, 255, 255), 1.6f) { LineJoin = Drawing2D.LineJoin.Round })
                g.DrawPath(white, path);
            using (var gray = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 110, 110, 100)))
                g.FillPath(gray, path);
        }

        using var png = new MemoryStream();
        bmp.Save(png, Drawing.Imaging.ImageFormat.Png);
        return png.ToArray();
    }
}
