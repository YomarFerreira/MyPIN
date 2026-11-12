using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace MyPIN.Services;

/// <summary>
/// Desenho do ícone do MyPIN. Tudo é desenhado num espaço de 256×256 e escalado,
/// e o .ico tem várias resoluções, então fica nítido em qualquer escala de tela.
/// </summary>
public static class IconArt
{
    /// <summary>1 = nota com canto dobrado · 2 = nota com alfinete · 3 = pilha de notas</summary>
    public const int Variant = 1;

    static readonly int[] IcoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

    public static Bitmap Render(int size, int variant = Variant)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 256f, size / 256f);
        bool small = size <= 24;

        switch (variant)
        {
            case 2: DrawPinned(g, small); break;
            case 3: DrawStack(g, small); break;
            default: DrawFolded(g, small, withText: size >= 32); break;
        }
        return bmp;
    }

    /// <summary>Arquivo .ico com várias resoluções (cada uma em PNG).</summary>
    public static byte[] BuildIco(int variant = Variant)
    {
        var pngs = IcoSizes.Select(s =>
        {
            using var bmp = Render(s, variant);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }).ToList();

        using var outMs = new MemoryStream();
        using var w = new BinaryWriter(outMs);
        w.Write((short)0); w.Write((short)1); w.Write((short)IcoSizes.Length); // cabeçalho ICO
        int offset = 6 + 16 * IcoSizes.Length;
        for (int i = 0; i < IcoSizes.Length; i++)
        {
            int s = IcoSizes[i];
            w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        w.Flush();
        return outMs.ToArray();
    }

    // ------------------------------------------------------------------

    static readonly Color YellowTop = Color.FromArgb(255, 255, 233, 110);
    static readonly Color YellowBottom = Color.FromArgb(255, 255, 200, 40);
    static readonly Color Outline = Color.FromArgb(255, 222, 160, 0);
    static readonly Color LineColor = Color.FromArgb(185, 120, 80, 0);

    /// <summary>
    /// Opção 1: nota adesiva amarelo-clara, com o canto de baixo dobrado e "MyPIN"
    /// escrito bem clarinho, quase da cor do papel, como uma marca d'água. Em ícones muito pequenos
    /// (16/20/24 px) o texto viraria borrão, então ele só aparece de 32 px para cima.
    /// </summary>
    static void DrawFolded(Graphics g, bool small, bool withText)
    {
        const float x = 22, y = 22, size = 212, r = 30, fold = 66;
        float right = x + size, bottom = y + size;

        using var body = new GraphicsPath();
        body.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        body.AddArc(right - 2 * r, y, 2 * r, 2 * r, 270, 90);
        body.AddLine(right, y + r, right, bottom - fold);
        body.AddLine(right, bottom - fold, right - fold, bottom);
        body.AddArc(x, bottom - 2 * r, 2 * r, 2 * r, 90, 90);
        body.CloseFigure();

        if (!small) Shadow(g, body);

        // amarelinho claro de nota adesiva de papel, com leve degradê
        using (var fill = new LinearGradientBrush(new PointF(0, y), new PointF(0, bottom),
                   Color.FromArgb(255, 255, 249, 168), Color.FromArgb(255, 254, 238, 110)))
            g.FillPath(fill, body);

        // canto dobrado
        using (var flap = new GraphicsPath())
        {
            flap.AddLine(right, bottom - fold, right - fold + 10, bottom - fold + 10);
            flap.AddLine(right - fold + 10, bottom - fold + 10, right - fold, bottom);
            flap.CloseFigure();
            using var flapFill = new LinearGradientBrush(new PointF(right - fold, bottom - fold), new PointF(right, bottom),
                Color.FromArgb(255, 255, 252, 210), Color.FromArgb(255, 232, 212, 80));
            g.FillPath(flapFill, flap);
        }

        // "MyPIN" em marca d'água: um tom só um pouco mais escuro que o papel
        if (withText)
        {
            using var text = new GraphicsPath();
            using var family = new FontFamily("Segoe UI");
            text.AddString("MyPIN", family, (int)FontStyle.Bold, 74, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = text.GetBounds();
            // não pode passar da parte lisa do papel: se ficar largo demais, diminui
            const float maxW = 158;
            if (bounds.Width > maxW)
            {
                using var shrink = new Matrix();
                shrink.Scale(maxW / bounds.Width, maxW / bounds.Width);
                text.Transform(shrink);
                bounds = text.GetBounds();
            }
            // centralizado na parte lisa do papel (um pouco acima do canto dobrado)
            float cx = x + size / 2 - 8, cy = y + size / 2 - 6;
            using var m = new Matrix();
            m.Translate(cx - (bounds.X + bounds.Width / 2), cy - (bounds.Y + bounds.Height / 2));
            text.Transform(m);
            using var ink = new SolidBrush(Color.FromArgb(255, 238, 220, 112));
            g.FillPath(ink, text);
        }

        // contorno discreto: dá contraste em fundo claro sem pesar no escuro
        using (var pen = new Pen(Color.FromArgb(255, 214, 190, 60), small ? 9 : 5) { LineJoin = LineJoin.Round })
            g.DrawPath(pen, body);
    }

    /// <summary>Opção 2: nota amarela presa por um alfinete vermelho.</summary>
    static void DrawPinned(Graphics g, bool small)
    {
        const float x = 26, y = 44, w = 204, h = 192, r = 28;
        using var body = RoundRect(x, y, w, h, r);
        if (!small) Shadow(g, body);
        using (var fill = new LinearGradientBrush(new PointF(0, y), new PointF(0, y + h), YellowTop, YellowBottom))
            g.FillPath(fill, body);
        using (var pen = new Pen(Outline, small ? 10 : 6))
            g.DrawPath(pen, body);

        Lines(g, small, x + 38, 126, small ? new[] { 128f, 96f } : new[] { 128f, 128f, 84f });

        // alfinete
        const float cx = 128, cy = 46, pr = 34;
        if (!small)
            using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
                g.FillEllipse(sh, cx - pr + 4, cy - pr + 8, 2 * pr, 2 * pr);
        using (var pin = new LinearGradientBrush(new PointF(cx, cy - pr), new PointF(cx, cy + pr),
                   Color.FromArgb(255, 255, 110, 110), Color.FromArgb(255, 200, 30, 45)))
            g.FillEllipse(pin, cx - pr, cy - pr, 2 * pr, 2 * pr);
        if (!small)
            using (var shine = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                g.FillEllipse(shine, cx - 18, cy - 24, 18, 13);
    }

    /// <summary>Opção 3: duas notas empilhadas (azul atrás, amarela na frente).</summary>
    static void DrawStack(Graphics g, bool small)
    {
        // nota de trás, girada
        var state = g.Save();
        g.TranslateTransform(150, 106);
        g.RotateTransform(12);
        using (var back = RoundRect(-90, -88, 176, 176, 24))
        {
            using var fill = new LinearGradientBrush(new PointF(0, -88), new PointF(0, 88),
                Color.FromArgb(255, 140, 205, 255), Color.FromArgb(255, 70, 150, 235));
            g.FillPath(fill, back);
            using var pen = new Pen(Color.FromArgb(255, 50, 120, 200), small ? 10 : 6);
            g.DrawPath(pen, back);
        }
        g.Restore(state);

        // nota da frente
        using var front = RoundRect(18, 64, 176, 176, 24);
        if (!small) Shadow(g, front);
        using (var fill = new LinearGradientBrush(new PointF(0, 64), new PointF(0, 240), YellowTop, YellowBottom))
            g.FillPath(fill, front);
        using (var pen = new Pen(Outline, small ? 10 : 6))
            g.DrawPath(pen, front);

        Lines(g, small, 50, 124, small ? new[] { 110f, 80f } : new[] { 112f, 112f, 70f });
    }

    // ------------------------------------------------------------------

    static void Lines(Graphics g, bool small, float x, float y, float[] lengths)
    {
        float gap = small ? 48 : 38;
        using var pen = new Pen(LineColor, small ? 20 : 14) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (int i = 0; i < lengths.Length; i++)
            g.DrawLine(pen, x, y + i * gap, x + lengths[i], y + i * gap);
    }

    static void Shadow(Graphics g, GraphicsPath shape)
    {
        var state = g.Save();
        g.TranslateTransform(0, 7);
        using (var sh = new SolidBrush(Color.FromArgb(45, 0, 0, 0)))
            g.FillPath(sh, shape);
        g.Restore(state);
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, 2 * r, 2 * r, 180, 90);
        p.AddArc(x + w - 2 * r, y, 2 * r, 2 * r, 270, 90);
        p.AddArc(x + w - 2 * r, y + h - 2 * r, 2 * r, 2 * r, 0, 90);
        p.AddArc(x, y + h - 2 * r, 2 * r, 2 * r, 90, 90);
        p.CloseFigure();
        return p;
    }
}
