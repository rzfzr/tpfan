using System;
using System.Collections.Generic;

/// A straight-alpha ARGB colour.
readonly struct Rgba {
    public readonly byte A, R, G, B;
    public Rgba(byte a, int rgb) { A = a; R = (byte)(rgb >> 16); G = (byte)(rgb >> 8); B = (byte)rgb; }
    public static Rgba Of(int rgb) => new Rgba(255, rgb);
    public Rgba WithAlpha(byte a) => new Rgba(a, (R << 16) | (G << 8) | B);
}

/// Taskbar colours, dark taskbar first, light second.
static class Palette {
    static Rgba Pick(bool light, int dark, int lightRgb) => Rgba.Of(light ? lightRgb : dark);
    public static Rgba Text(bool l) => Pick(l, 0xFFFFFF, 0x1A1A1A);
    public static Rgba Grey(bool l) => Pick(l, 0x9A9A9A, 0x707070);
    public static Rgba Cpu(bool l) => Pick(l, 0x7CCBFF, 0x005A9E);
    public static Rgba Yellow(bool l) => Pick(l, 0xFFD54A, 0x8A5A00);
    public static Rgba Red(bool l) => Pick(l, 0xFF6A5C, 0xC42B1C);
    public static Rgba Green(bool l) => Pick(l, 0x74E27E, 0x107C10);
    public static Rgba Amber(bool l) => Pick(l, 0xFFAE42, 0xA65400);
    public static Rgba CpuBars(bool l) => Pick(l, 0x5FD068, 0x107C10);
    public static Rgba Disk(bool l) => Pick(l, 0xFFAE42, 0xA65400);
    public static Rgba Ram(bool l) => Pick(l, 0x3D9BFF, 0x005FB8);
    public static Rgba Send(bool l) => Pick(l, 0x5AB0FF, 0x005FB8);
    public static Rgba Recv(bool l) => Pick(l, 0x74E27E, 0x107C10);
    public static Rgba Track(bool l) => l ? new Rgba(46, 0x000000) : new Rgba(56, 0xFFFFFF);
    public static Rgba Dim(bool l) => l ? new Rgba(80, 0x1A1A1A) : new Rgba(70, 0xFFFFFF);
    public static Rgba Hover(bool l) => l ? new Rgba(28, 0x000000) : new Rgba(36, 0xFFFFFF);
    public static Rgba Mode(string name, bool l) => name switch {
        "Eco" => Green(l), "Performance" => Text(l), "Maximum" => Red(l), _ => Cpu(l),
    };
}

/// A pixel font: glyphs as rows of '#' and '.', drawn as scale x scale blocks (crisp at any whole scale).
sealed class PixelFont {
    public readonly int Height, Gap;
    readonly Dictionary<char, string[]> _g;
    public PixelFont(int height, int gap, Dictionary<char, string[]> glyphs) { Height = height; Gap = gap; _g = glyphs; }
    public bool Has(char c) => _g.ContainsKey(c);
    public int Width(string s) {
        int w = 0, n = 0;
        foreach (char c in s) if (_g.TryGetValue(c, out var g)) { w += g[0].Length; n++; }
        return n == 0 ? 0 : w + Gap * (n - 1);
    }
    public string[] Glyph(char c) => _g.TryGetValue(c, out var g) ? g : null;

    /// 7x14 digits and mode letters (the "1" and "." are narrower).
    public static readonly PixelFont Big = new PixelFont(14, 1, new Dictionary<char, string[]> {
        ['0'] = new[] { ".#####.", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", ".#####." },
        ['1'] = new[] { ".#.", "##.", ".#.", ".#.", ".#.", ".#.", ".#.", ".#.", ".#.", ".#.", ".#.", ".#.", ".#.", "###" },
        ['2'] = new[] { ".#####.", "#.....#", "......#", "......#", "......#", ".....#.", "....#..", "...#...", "..#....", ".#.....", "#......", "#......", "#......", "#######" },
        ['3'] = new[] { ".#####.", "#.....#", "......#", "......#", "......#", "......#", "..####.", "......#", "......#", "......#", "......#", "......#", "#.....#", ".#####." },
        ['4'] = new[] { ".....#.", "....##.", "....##.", "...#.#.", "...#.#.", "..#..#.", "..#..#.", ".#...#.", "#....#.", "#######", ".....#.", ".....#.", ".....#.", ".....#." },
        ['5'] = new[] { "#######", "#......", "#......", "#......", "#......", "######.", "......#", "......#", "......#", "......#", "......#", "......#", "#.....#", ".#####." },
        ['6'] = new[] { ".#####.", "#.....#", "#......", "#......", "#......", "#......", "######.", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", ".#####." },
        ['7'] = new[] { "#######", "......#", "......#", ".....#.", ".....#.", "....#..", "....#..", "....#..", "...#...", "...#...", "...#...", "...#...", "...#...", "...#..." },
        ['8'] = new[] { ".#####.", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", ".#####.", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", ".#####." },
        ['9'] = new[] { ".#####.", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", ".######", "......#", "......#", "......#", "......#", "#.....#", ".#####." },
        ['.'] = new[] { "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "..", "##", "##" },
        ['-'] = new[] { "...", "...", "...", "...", "...", "...", "...", "###", "...", "...", "...", "...", "...", "..." },
        ['E'] = new[] { "#######", "#......", "#......", "#......", "#......", "#......", "######.", "#......", "#......", "#......", "#......", "#......", "#......", "#######" },
        ['P'] = new[] { "######.", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "######.", "#......", "#......", "#......", "#......", "#......", "#......", "#......" },
        ['M'] = new[] { "#.....#", "##...##", "#.#.#.#", "#..#..#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#", "#.....#" },
        ['?'] = new[] { ".#####.", "#.....#", "......#", "......#", ".....#.", "....#..", "...#...", "...#...", "...#...", "...#...", ".......", ".......", "...#...", "...#..." },
    });

    /// 5x7 for units and the network lines, plus an 8th row for the descender of "p". Baseline = row 6.
    /// '^' = send arrow, 'v' = receive arrow, 'o' = degree sign.
    public static readonly PixelFont Small = new PixelFont(8, 1, new Dictionary<char, string[]> {
        ['0'] = new[] { ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.", "....." },
        ['1'] = new[] { ".#.", "##.", ".#.", ".#.", ".#.", ".#.", "###", "..." },
        ['2'] = new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####", "....." },
        ['3'] = new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####.", "....." },
        ['4'] = new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.", "....." },
        ['5'] = new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###.", "....." },
        ['6'] = new[] { ".###.", "#....", "#....", "####.", "#...#", "#...#", ".###.", "....." },
        ['7'] = new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#...", "....." },
        ['8'] = new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###.", "....." },
        ['9'] = new[] { ".###.", "#...#", "#...#", ".####", "....#", "....#", ".###.", "....." },
        ['.'] = new[] { ".", ".", ".", ".", ".", ".", "#", "." },
        [' '] = new[] { "..", "..", "..", "..", "..", "..", "..", ".." },
        ['-'] = new[] { "...", "...", "...", "###", "...", "...", "...", "..." },
        ['K'] = new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#", "....." },
        ['M'] = new[] { "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#", "....." },
        ['G'] = new[] { ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####", "....." },
        ['b'] = new[] { "#....", "#....", "#.##.", "##..#", "#...#", "#...#", "####.", "....." },
        ['p'] = new[] { ".....", ".....", "####.", "#...#", "#...#", "####.", "#....", "#...." },
        ['s'] = new[] { ".....", ".....", ".####", "#....", ".###.", "....#", "####.", "....." },
        ['W'] = new[] { "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "##.##", "#...#", "....." },
        ['%'] = new[] { "##..#", "##..#", "...#.", "..#..", ".#...", "#..##", "#..##", "....." },
        ['o'] = new[] { ".##.", "#..#", "#..#", ".##.", "....", "....", "....", "...." },
        ['^'] = new[] { ".....", "..#..", ".###.", "#####", ".....", ".....", ".....", "....." },
        ['v'] = new[] { ".....", "#####", ".###.", "..#..", ".....", ".....", ".....", "....." },
    });
}

/// A premultiplied BGRA pixel buffer (the layout UpdateLayeredWindow wants), with the bar's drawing primitives.
sealed class Canvas {
    public readonly int W, H;
    public readonly uint[] Px;
    public Canvas(int w, int h) { W = w; H = h; Px = new uint[w * h]; }

    /// Fills everything with a nearly transparent colour: alpha 1 keeps mouse clicks on the bar.
    public void Clear() => Array.Fill(Px, 0x01000000u);

    /// Source-over blend of a colour with extra coverage (0..1).
    public void Blend(int x, int y, Rgba c, double coverage = 1) {
        if ((uint)x >= (uint)W || (uint)y >= (uint)H || coverage <= 0) return;
        double a = c.A / 255.0 * Math.Min(coverage, 1);
        uint d = Px[y * W + x];
        double da = (d >> 24) / 255.0, dr = (d >> 16 & 0xFF), dg = (d >> 8 & 0xFF), db = (d & 0xFF);
        double oa = a + da * (1 - a);
        double or = c.R * a + dr * (1 - a), og = c.G * a + dg * (1 - a), ob = c.B * a + db * (1 - a);   // premultiplied
        Px[y * W + x] = ((uint)Math.Round(oa * 255) << 24) | ((uint)Math.Round(or) << 16) | ((uint)Math.Round(og) << 8) | (uint)Math.Round(ob);
    }

    public void Fill(int x, int y, int w, int h, Rgba c) {
        for (int j = Math.Max(0, y); j < Math.Min(H, y + h); j++)
            for (int i = Math.Max(0, x); i < Math.Min(W, x + w); i++) Blend(i, j, c);
    }

    /// Draws text at (x, y) = top-left, returns its width in pixels.
    public int Text(PixelFont f, string s, int x, int y, int scale, Rgba c) {
        int x0 = x, n = 0;
        foreach (char ch in s) {
            var g = f.Glyph(ch); if (g == null) continue;
            if (n++ > 0) x += f.Gap * scale;
            for (int r = 0; r < g.Length; r++)
                for (int col = 0; col < g[r].Length; col++)
                    if (g[r][col] == '#') Fill(x + col * scale, y + r * scale, scale, scale, c);
            x += g[0].Length * scale;
        }
        return x - x0;
    }

    /// Vertical bars, one per value (0..100), each with a faint track.
    public void Bars(double[] values, int x, int y, int barW, int gap, int h, Rgba fill, Rgba track) {
        for (int i = 0; i < values.Length; i++) {
            int bx = x + i * (barW + gap);
            int level = double.IsNaN(values[i]) ? 0 : (int)Math.Round(Math.Clamp(values[i], 0, 100) / 100 * h);
            Fill(bx, y, barW, h - level, track);
            Fill(bx, y + h - level, barW, level, fill);
        }
    }

    /// A circle of diameter d at (x, y) filling clockwise from 12 o'clock; antialiased by 4x4 supersampling.
    public void Pie(double pct, int x, int y, int d, double ring, Rgba fill, Rgba ringColor) {
        double r = d / 2.0, cx = x + r, cy = y + r, sweep = double.IsNaN(pct) ? -1 : Math.Clamp(pct, 0, 100) / 100 * 2 * Math.PI;
        for (int j = y; j < y + d; j++)
            for (int i = x; i < x + d; i++) {
                int inFill = 0, inRing = 0;
                for (int sy = 0; sy < 4; sy++)
                    for (int sx = 0; sx < 4; sx++) {
                        double px = i + (sx + 0.5) / 4 - cx, py = j + (sy + 0.5) / 4 - cy, dist = Math.Sqrt(px * px + py * py);
                        if (dist > r) continue;
                        double ang = Math.Atan2(px, -py); if (ang < 0) ang += 2 * Math.PI;   // 0 at 12 o'clock, clockwise
                        if (ang < sweep) inFill++;
                        else if (dist >= r - ring) inRing++;
                    }
                if (inRing > 0) Blend(i, j, ringColor, inRing / 16.0);
                if (inFill > 0) Blend(i, j, fill, inFill / 16.0);
            }
    }
}
