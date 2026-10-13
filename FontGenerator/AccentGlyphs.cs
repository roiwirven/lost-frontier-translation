// Builds the accented letters missing from the game font "CF Old Milwaukee".
//  * base letters come from CF Old Milwaukee itself
//  * the diaeresis is made of two copies of CF Old Milwaukee's own i-dot
//  * acute, grave, circumflex, tilde, ring and cedilla are borrowed from the game's other font,
//    "Baar Zeitgeist", scaled to CF Old Milwaukee's x-height
//  * a few punctuation marks / letters (¿ ¡ « » – — … ß Æ æ) are borrowed whole from Baar Zeitgeist
using System;
using System.Collections.Generic;

namespace LostFrontierTranslation.FontGenerator
{
    public static class AccentGlyphs
    {
        const double XHeight = 478;          // CF Old Milwaukee x-height (font units)

        static List<List<Pt>> Normalize(Glyph g, double scale)
        {
            // scale, centre horizontally on x = 0, bottom on y = 0
            double x0, y0, x1, y1; g.Bounds(out x0, out y0, out x1, out y1);
            var t = new Glyph(); t.Add(g.Contours, scale, -scale * (x0 + x1) / 2, -scale * y0);
            return t.Contours;
        }

        static void B(List<List<Pt>> cs, out double x0, out double y0, out double x1, out double y1)
        {
            var g = new Glyph(); g.Contours.AddRange(cs); g.Bounds(out x0, out y0, out x1, out y1);
        }

        public static string Build(TrueTypeFont cf, TrueTypeFont donor)
        {
            var added = new System.Text.StringBuilder();

            // ----- accent marks
            var marks = new Dictionary<string, List<List<Pt>>>
            {
                { "acute", Normalize(donor.GlyphFor('´'), 0.8) }, { "grave", Normalize(donor.GlyphFor('`'), 0.8) },
                { "circ", Normalize(donor.GlyphFor('ˆ'), 0.8) },  { "tilde", Normalize(donor.GlyphFor('˜'), 0.8) },
                { "ring", Normalize(donor.GlyphFor('˚'), 0.8) },
            };
            var iGlyph = cf.GlyphFor('i');
            List<Pt> dot = null; double best = double.MinValue;
            foreach (var c in iGlyph.Contours)
            {
                double y = double.MaxValue; foreach (var p in c) y = Math.Min(y, p.Y);
                if (y > best) { best = y; dot = c; }
            }
            var stem = new List<List<Pt>>(); foreach (var c in iGlyph.Contours) if (c != dot) stem.Add(c);
            {
                double x0, y0, x1, y1; B(new List<List<Pt>> { dot }, out x0, out y0, out x1, out y1);
                double w = x1 - x0, gap = w * 0.9, total = 2 * w + gap;
                var d = new Glyph();
                d.Add(new[] { dot }, 1, -x0 - total / 2, -y0);
                d.Add(new[] { dot }, 1, -x0 - total / 2 + w + gap, -y0);
                marks["diae"] = d.Contours;
            }
            var cedilla = donor.GlyphFor('¸').Contours;

            Func<char, bool> missing = ch => { var g = cf.GlyphFor(ch); return g == null || g.IsEmpty; };

            Action<char, Glyph> put = (ch, g) =>
            {
                int gi;
                if (cf.Cmap.TryGetValue(ch, out gi) && cf.Glyphs[gi].IsEmpty) cf.Glyphs[gi] = g;   // reuse empty slot
                else cf.Cmap[ch] = cf.AddGlyph(g);
                added.Append(ch);
            };

            Action<char, char, string, bool, List<List<Pt>>> accented = (ch, baseCh, mark, ced, baseContours) =>
            {
                if (!missing(ch)) return;
                var bg = cf.GlyphFor(baseCh);
                var g = new Glyph { Advance = bg.Advance };
                var bc = baseContours ?? bg.Contours;
                g.Add(bc, 1, 0, 0);
                double x0, y0, x1, y1; B(bc, out x0, out y0, out x1, out y1);
                double cx = (x0 + x1) / 2;
                bool upper = char.IsUpper(baseCh);
                if (mark != null)
                {
                    double s = upper ? 0.85 : 1.0;
                    double top = baseContours != null ? XHeight : y1;
                    g.Add(marks[mark], s, cx, top + (upper ? 35 : 60));
                }
                if (ced)
                {
                    double a0, b0, a1, b1; B(cedilla, out a0, out b0, out a1, out b1);
                    const double s = 0.85;
                    g.Add(cedilla, s, cx - s * (a0 + a1) / 2 + 10, -s * b1 + 8);
                }
                put(ch, g);
            };

            string[] lower = {
                "à a grave", "á a acute", "â a circ", "ã a tilde", "ä a diae", "å a ring",
                "è e grave", "é e acute", "ê e circ", "ë e diae",
                "ì ı grave", "í ı acute", "î ı circ", "ï ı diae",
                "ò o grave", "ó o acute", "ô o circ", "õ o tilde", "ö o diae",
                "ù u grave", "ú u acute", "û u circ", "ü u diae",
                "ñ n tilde", "ý y acute", "ÿ y diae" };
            foreach (var e in lower)
            {
                var p = e.Split(' ');
                char ch = p[0][0], bc = p[1][0];
                if (bc == 'ı') { accented(ch, 'i', p[2], false, stem); continue; }
                accented(ch, bc, p[2], false, null);
                char up = char.ToUpperInvariant(ch);
                if (up != ch) accented(up, char.ToUpperInvariant(bc), p[2], false, null);
            }
            accented('Ì', 'I', "grave", false, null); accented('Í', 'I', "acute", false, null);
            accented('Î', 'I', "circ", false, null);  accented('Ï', 'I', "diae", false, null);
            accented('ç', 'c', null, true, null);     accented('Ç', 'C', null, true, null);

            // ----- apostrophe and quotes from CF Old Milwaukee's own right single quote
            var q = cf.GlyphFor('’');
            if (q != null && !q.IsEmpty)
            {
                double x0, y0, x1, y1; q.Bounds(out x0, out y0, out x1, out y1); double w = x1 - x0;
                foreach (char ch in "'‘") if (missing(ch)) { var g = new Glyph { Advance = q.Advance }; g.Add(q.Contours, 1, 0, 0); put(ch, g); }
                foreach (char ch in "\"“”")
                    if (missing(ch))
                    {
                        var g = new Glyph { Advance = (int)(q.Advance + w * 1.1) };
                        g.Add(q.Contours, 1, 0, 0); g.Add(q.Contours, 1, w * 1.1, 0); put(ch, g);
                    }
            }
            // ----- whole glyphs borrowed from Baar Zeitgeist
            foreach (char ch in "¿¡«»–—…ßÆæ")
            {
                var d = donor.GlyphFor(ch);
                if (d == null || !missing(ch)) continue;
                var g = new Glyph { Advance = (int)(d.Advance * 0.85) };
                g.Add(d.Contours, 0.85, 0, 0); put(ch, g);
            }
            return added.ToString();
        }
    }
}
