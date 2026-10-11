# Erzeugt src\Assets\app.ico (16-256 px) für LibraryWarden, optional eine PNG-Vorschau und einen Variantenvergleich.
#
# Aufruf: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1 [-Variant S|B|M] [-Color Purple|Teal] [-PreviewPng x.png] [-SheetPng y.png] [-NoIco]
#   S = Schild mit Download-Pfeil
#   B = Buchrücken im Regal mit kleinem Schild
#   M = Schild mit „LW“-Monogramm
param(
    [ValidateSet('S', 'B', 'M')][string]$Variant = 'S',
    [ValidateSet('Purple', 'Teal')][string]$Color = 'Teal',
    [string]$OutIco = (Join-Path $PSScriptRoot '..\src\Assets\app.ico'),
    [string]$PreviewPng = '',
    [string]$SheetPng = '',
    [switch]$NoIco
)

Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

public static class WardenIcon
{
    static Color Top(string color) { return color == "Teal" ? Color.FromArgb(0x2D, 0xB3, 0xA6) : Color.FromArgb(0xA0, 0x4C, 0xE6); }
    static Color Bottom(string color) { return color == "Teal" ? Color.FromArgb(0x0B, 0x4F, 0x4A) : Color.FromArgb(0x3A, 0x14, 0x6E); }
    static Color Mid(string color) { return color == "Teal" ? Color.FromArgb(0x15, 0x7A, 0x72) : Color.FromArgb(0x6B, 0x2F, 0xB3); }

    static GraphicsPath RR(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = Math.Max(0.01f, r * 2);
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static Pen MkPen(Color c, float width)
    {
        var p = new Pen(c, width);
        p.StartCap = LineCap.Round;
        p.EndCap = LineCap.Round;
        p.LineJoin = LineJoin.Round;
        return p;
    }

    static void Background(Graphics g, int s, string color)
    {
        var rect = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
        using (var path = RR(rect.X, rect.Y, rect.Width, rect.Height, s * 0.22f))
        using (var brush = new LinearGradientBrush(rect, Top(color), Bottom(color), 90f))
            g.FillPath(brush, path);
    }

    // Wappenschild: oben gerade, Seiten senkrecht, unten spitz zulaufend
    static GraphicsPath Shield(float cx, float top, float w, float h)
    {
        float l = cx - w / 2, r = cx + w / 2, bottom = top + h, side = top + h * 0.48f;
        var p = new GraphicsPath();
        p.AddLine(l, top + h * 0.06f, cx, top);
        p.AddLine(cx, top, r, top + h * 0.06f);
        p.AddLine(r, top + h * 0.06f, r, side);
        p.AddBezier(r, side, r, top + h * 0.78f, cx + w * 0.22f, top + h * 0.90f, cx, bottom);
        p.AddBezier(cx, bottom, cx - w * 0.22f, top + h * 0.90f, l, top + h * 0.78f, l, side);
        p.CloseFigure();
        return p;
    }

    static void Arrow(Graphics g, Pen pen, float cx, float top, float bottom, float wing)
    {
        g.DrawLine(pen, cx, top, cx, bottom);
        g.DrawLines(pen, new[] { new PointF(cx - wing, bottom - wing), new PointF(cx, bottom), new PointF(cx + wing, bottom - wing) });
    }

    public static Bitmap Render(int s, string variant, string color)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            Background(g, s, color);
            bool small = s < 32;

            if (variant == "B")
            {
                // Drei Buchrücken (einer gekippt) auf einem Regalboden, groß zusätzlich ein Schild
                float baseY = s * (small ? 0.80f : 0.76f);
                using (var white = new SolidBrush(Color.White))
                {
                    float w = s * (small ? 0.15f : 0.12f), gap = s * (small ? 0.05f : 0.04f), x = s * (small ? 0.20f : 0.18f);
                    float[] heights = small ? new[] { 0.52f, 0.60f } : new[] { 0.46f, 0.54f, 0.40f };
                    foreach (var hh in heights)
                    {
                        using (var spine = RR(x, baseY - s * hh, w, s * hh, w * 0.25f)) g.FillPath(white, spine);
                        x += w + gap;
                    }
                    var state = g.Save();
                    g.TranslateTransform(x + w * 0.2f, baseY);
                    g.RotateTransform(18);
                    float th = s * (small ? 0.50f : 0.44f);
                    using (var tilted = RR(0, -th, w, th, w * 0.25f)) g.FillPath(white, tilted);
                    g.Restore(state);
                    if (!small)
                    {
                        using (var shield = Shield(s * 0.70f, s * 0.47f, s * 0.24f, s * 0.30f))
                        using (var fill = new SolidBrush(Mid(color)))
                        using (var pen = MkPen(Color.White, Math.Max(1.2f, s * 0.035f)))
                        {
                            g.FillPath(fill, shield);
                            g.DrawPath(pen, shield);
                        }
                    }
                }
                using (var pen = MkPen(Color.White, Math.Max(1.2f, s * 0.05f)))
                    g.DrawLine(pen, s * 0.16f, baseY + s * 0.035f, s * 0.84f, baseY + s * 0.035f);
            }
            else if (variant == "M")
            {
                using (var shield = Shield(s * 0.5f, s * 0.15f, s * 0.62f, s * 0.72f))
                {
                    if (small)
                    {
                        using (var white = new SolidBrush(Color.White)) g.FillPath(white, shield);
                        using (var pen = MkPen(Mid(color), Math.Max(1.3f, s * 0.085f)))
                            DrawLW(g, pen, s, 0.85f);
                    }
                    else
                    {
                        using (var pen = MkPen(Color.White, Math.Max(1.2f, s * 0.05f))) g.DrawPath(pen, shield);
                        using (var pen = MkPen(Color.White, Math.Max(1.4f, s * 0.06f))) DrawLW(g, pen, s, 0.80f);
                    }
                }
            }
            else
            {
                // S: Schild mit Download-Pfeil
                using (var shield = Shield(s * 0.5f, s * (small ? 0.10f : 0.14f), s * (small ? 0.72f : 0.62f), s * (small ? 0.80f : 0.72f)))
                {
                    if (small)
                    {
                        using (var white = new SolidBrush(Color.White)) g.FillPath(white, shield);
                        using (var pen = MkPen(Mid(color), Math.Max(1.5f, s * 0.11f)))
                            Arrow(g, pen, s * 0.5f, s * 0.25f, s * 0.62f, s * 0.15f);
                    }
                    else
                    {
                        using (var pen = MkPen(Color.White, Math.Max(1.2f, s * 0.055f))) g.DrawPath(pen, shield);
                        using (var pen = MkPen(Color.White, Math.Max(1.6f, s * 0.075f)))
                            Arrow(g, pen, s * 0.5f, s * 0.30f, s * 0.62f, s * 0.12f);
                        using (var pen = MkPen(Color.White, Math.Max(1.4f, s * 0.06f)))
                            g.DrawLine(pen, s * 0.39f, s * 0.71f, s * 0.61f, s * 0.71f);
                    }
                }
            }
        }
        return bmp;
    }

    // „LW“ als Linienzeichnung in der Schildmitte
    static void DrawLW(Graphics g, Pen pen, int s, float scale)
    {
        float cx = s * 0.5f, cy = s * 0.46f, h = s * 0.26f * scale, w = s * 0.40f * scale;
        float l = cx - w / 2, top = cy - h / 2, bottom = cy + h / 2;
        g.DrawLines(pen, new[] { new PointF(l, top), new PointF(l, bottom), new PointF(l + w * 0.28f, bottom) });
        float wx = l + w * 0.38f, ww = w * 0.62f;
        g.DrawLines(pen, new[] { new PointF(wx, top), new PointF(wx + ww * 0.25f, bottom), new PointF(wx + ww * 0.5f, top + h * 0.35f),
                                 new PointF(wx + ww * 0.75f, bottom), new PointF(wx + ww, top) });
    }

    static byte[] Png(Bitmap b)
    {
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); }
    }

    static byte[] Dib(Bitmap b)
    {
        int s = b.Width;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    var c = b.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            int maskRow = ((s + 31) / 32) * 4;
            for (int y = s - 1; y >= 0; y--)
            {
                var row = new byte[maskRow];
                for (int x = 0; x < s; x++)
                    if (b.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                w.Write(row);
            }
            return ms.ToArray();
        }
    }

    public static void WriteIco(string icoPath, string variant, string color)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var images = new List<byte[]>();
        foreach (var s in sizes)
            using (var bmp = Render(s, variant, color)) images.Add(s >= 64 ? Png(bmp) : Dib(bmp));

        using (var fs = File.Create(icoPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }

    public static void WritePreview(string path, string variant, string color)
    {
        using (var bmp = Render(256, variant, color)) bmp.Save(path, ImageFormat.Png);
    }

    // Vergleich: 256 px hell/dunkel, 64/32/16 px echt, 32/16 px vierfach vergrößert
    public static void WriteSheet(string path, string[] variants, string[] colors)
    {
        int rowH = 300, width = 1080;
        using (var sheet = new Bitmap(width, rowH * variants.Length, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(sheet))
        using (var font = new Font("Segoe UI", 16, FontStyle.Bold))
        using (var light = new SolidBrush(Color.FromArgb(0xF3, 0xF2, 0xF6)))
        using (var dark = new SolidBrush(Color.FromArgb(0x1C, 0x1B, 0x21)))
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.White);
            for (int i = 0; i < variants.Length; i++)
            {
                int top = i * rowH;
                g.FillRectangle(light, 0, top, width / 2, rowH);
                g.FillRectangle(dark, width / 2, top, width / 2, rowH);
                g.DrawString(variants[i] + (colors[i] == "Teal" ? " (Petrol)" : ""), font, Brushes.Black, 8, top + 8);
                using (var big = Render(256, variants[i], colors[i]))
                {
                    g.DrawImage(big, 50, top + 34, 240, 240);
                    g.DrawImage(big, width / 2 + 20, top + 34, 240, 240);
                }
                int x = 330;
                foreach (var s in new[] { 64, 32, 16 })
                    using (var small = Render(s, variants[i], colors[i]))
                    {
                        g.DrawImage(small, x, top + 150 - s / 2, s, s);
                        g.DrawImage(small, width / 2 + 290 + (x - 330), top + 150 - s / 2, s, s);
                        x += s + 24;
                    }
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (var s32 = Render(32, variants[i], colors[i])) g.DrawImage(s32, width / 2 + 290, top + 200, 64, 64);
                using (var s16 = Render(16, variants[i], colors[i])) g.DrawImage(s16, width / 2 + 370, top + 200, 64, 64);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            }
            sheet.Save(path, ImageFormat.Png);
        }
    }
}
'@

if ($SheetPng) {
    [WardenIcon]::WriteSheet([System.IO.Path]::GetFullPath($SheetPng), [string[]]@('S', 'B', 'M', 'S'), [string[]]@('Purple', 'Purple', 'Purple', 'Teal'))
    Write-Output "Vergleich geschrieben: $SheetPng"
}
if ($PreviewPng) { [WardenIcon]::WritePreview([System.IO.Path]::GetFullPath($PreviewPng), $Variant, $Color) }
if (-not $NoIco) {
    $ico = [System.IO.Path]::GetFullPath($OutIco)
    New-Item -ItemType Directory -Force (Split-Path $ico) | Out-Null
    [WardenIcon]::WriteIco($ico, $Variant, $Color)
    Write-Output "Icon (Variante $Variant, $Color) geschrieben: $ico"
}
