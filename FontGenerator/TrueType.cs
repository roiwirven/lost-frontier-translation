// Minimal TrueType (glyf-based) reader/writer: enough to read glyph outlines, add new glyphs and
// write a valid font back. Tables that do not depend on the glyph count are copied unchanged.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LostFrontierTranslation.FontGenerator
{
    public struct Pt { public double X, Y; public bool On; public Pt(double x, double y, bool on) { X = x; Y = y; On = on; } }

    /// A glyph outline: list of closed contours. Original glyphs also keep their raw bytes (with hinting).
    public class Glyph
    {
        public List<List<Pt>> Contours = new List<List<Pt>>();
        public byte[] Raw;               // original bytes, written back untouched when not modified
        public int Advance, Lsb;

        public bool IsEmpty { get { return Contours.Count == 0; } }

        public Glyph Clone()
        {
            var g = new Glyph { Advance = Advance, Lsb = Lsb };
            foreach (var c in Contours) g.Contours.Add(new List<Pt>(c));
            return g;
        }

        public void Bounds(out double xMin, out double yMin, out double xMax, out double yMax)
        {
            xMin = yMin = double.MaxValue; xMax = yMax = double.MinValue;
            foreach (var c in Contours) foreach (var p in c)
            {
                if (p.X < xMin) xMin = p.X; if (p.X > xMax) xMax = p.X;
                if (p.Y < yMin) yMin = p.Y; if (p.Y > yMax) yMax = p.Y;
            }
            if (xMin == double.MaxValue) xMin = yMin = xMax = yMax = 0;
        }

        /// Appends the contours of another outline transformed by: scale s, then translation (dx, dy).
        public void Add(IEnumerable<List<Pt>> contours, double s, double dx, double dy)
        {
            foreach (var c in contours)
            {
                var n = new List<Pt>(c.Count);
                foreach (var p in c) n.Add(new Pt(p.X * s + dx, p.Y * s + dy, p.On));
                Contours.Add(n);
            }
            Raw = null;
        }
    }

    public class TrueTypeFont
    {
        public readonly Dictionary<string, byte[]> Tables = new Dictionary<string, byte[]>();
        public readonly List<Glyph> Glyphs = new List<Glyph>();
        public readonly Dictionary<int, int> Cmap = new Dictionary<int, int>();   // unicode -> glyph index

        // ------------------------------------------------------------------ big-endian helpers
        static int U16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        static int S16(byte[] b, int o) { return (short)((b[o] << 8) | b[o + 1]); }
        static uint U32(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }
        static void W16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static void W32(Stream s, uint v) { s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        static void P16(byte[] b, int o, int v) { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        static void P32(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

        public static TrueTypeFont Read(byte[] data)
        {
            var f = new TrueTypeFont();
            int numTables = U16(data, 4);
            for (int i = 0; i < numTables; i++)
            {
                int r = 12 + 16 * i;
                string tag = Encoding.ASCII.GetString(data, r, 4);
                int off = (int)U32(data, r + 8), len = (int)U32(data, r + 12);
                var t = new byte[len]; Array.Copy(data, off, t, 0, len);
                f.Tables[tag] = t;
            }
            byte[] head = f.Tables["head"], maxp = f.Tables["maxp"], hhea = f.Tables["hhea"], hmtx = f.Tables["hmtx"];
            byte[] loca = f.Tables["loca"], glyf = f.Tables["glyf"];
            int numGlyphs = U16(maxp, 4), longLoca = S16(head, 50), nHM = U16(hhea, 34);
            for (int g = 0; g < numGlyphs; g++)
            {
                int a = longLoca == 1 ? (int)U32(loca, g * 4) : U16(loca, g * 2) * 2;
                int b = longLoca == 1 ? (int)U32(loca, g * 4 + 4) : U16(loca, g * 2 + 2) * 2;
                var gl = ParseGlyph(glyf, a, b - a);
                int m = Math.Min(g, nHM - 1);
                gl.Advance = U16(hmtx, m * 4);
                gl.Lsb = g < nHM ? S16(hmtx, g * 4 + 2) : S16(hmtx, nHM * 4 + (g - nHM) * 2);
                f.Glyphs.Add(gl);
            }
            f.ReadCmap();
            return f;
        }

        static Glyph ParseGlyph(byte[] glyf, int off, int len)
        {
            var g = new Glyph();
            if (len <= 0) { g.Raw = new byte[0]; return g; }
            g.Raw = new byte[len]; Array.Copy(glyf, off, g.Raw, 0, len);
            int nc = S16(glyf, off);
            if (nc < 0) throw new NotSupportedException("composite glyphs are not supported");
            int p = off + 10;
            var ends = new int[nc];
            for (int i = 0; i < nc; i++) { ends[i] = U16(glyf, p); p += 2; }
            int nPts = nc == 0 ? 0 : ends[nc - 1] + 1;
            int ins = U16(glyf, p); p += 2 + ins;
            var flags = new byte[nPts];
            for (int i = 0; i < nPts;)
            {
                byte fl = glyf[p++]; flags[i++] = fl;
                if ((fl & 8) != 0) { int rep = glyf[p++]; while (rep-- > 0 && i < nPts) flags[i++] = fl; }
            }
            var xs = new int[nPts]; var ys = new int[nPts];
            int v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte fl = flags[i];
                if ((fl & 2) != 0) { int d = glyf[p++]; v += (fl & 16) != 0 ? d : -d; }
                else if ((fl & 16) == 0) { v += S16(glyf, p); p += 2; }
                xs[i] = v;
            }
            v = 0;
            for (int i = 0; i < nPts; i++)
            {
                byte fl = flags[i];
                if ((fl & 4) != 0) { int d = glyf[p++]; v += (fl & 32) != 0 ? d : -d; }
                else if ((fl & 32) == 0) { v += S16(glyf, p); p += 2; }
                ys[i] = v;
            }
            int start = 0;
            for (int c = 0; c < nc; c++)
            {
                var cont = new List<Pt>();
                for (int i = start; i <= ends[c]; i++) cont.Add(new Pt(xs[i], ys[i], (flags[i] & 1) != 0));
                g.Contours.Add(cont); start = ends[c] + 1;
            }
            return g;
        }

        void ReadCmap()
        {
            byte[] t = Tables["cmap"];
            int n = U16(t, 2), best = -1;
            for (int i = 0; i < n; i++)
            {
                int pid = U16(t, 4 + i * 8), eid = U16(t, 6 + i * 8); int off = (int)U32(t, 8 + i * 8);
                if (U16(t, off) == 4 && (pid == 3 && eid == 1 || pid == 0)) { best = off; if (pid == 3) break; }
            }
            if (best < 0) throw new InvalidDataException("no unicode cmap");
            int segX2 = U16(t, best + 6), segs = segX2 / 2;
            int endO = best + 14, startO = endO + segX2 + 2, deltaO = startO + segX2, rangeO = deltaO + segX2;
            for (int s = 0; s < segs; s++)
            {
                int end = U16(t, endO + s * 2), st = U16(t, startO + s * 2), delta = S16(t, deltaO + s * 2), ro = U16(t, rangeO + s * 2);
                for (int c = st; c <= end && c != 0xFFFF; c++)
                {
                    int gi;
                    if (ro == 0) gi = (c + delta) & 0xFFFF;
                    else { int a = rangeO + s * 2 + ro + (c - st) * 2; gi = U16(t, a); if (gi != 0) gi = (gi + delta) & 0xFFFF; }
                    if (gi != 0) Cmap[c] = gi;
                }
            }
        }

        public Glyph GlyphFor(char c) { int g; return Cmap.TryGetValue(c, out g) ? Glyphs[g] : null; }

        // ------------------------------------------------------------------ writing
        static byte[] EncodeGlyph(Glyph g)
        {
            if (g.Raw != null) return g.Raw;
            if (g.IsEmpty) return new byte[0];
            double x0, y0, x1, y1; g.Bounds(out x0, out y0, out x1, out y1);
            var s = new MemoryStream();
            W16(s, g.Contours.Count);
            W16(s, (int)Math.Round(x0)); W16(s, (int)Math.Round(y0)); W16(s, (int)Math.Round(x1)); W16(s, (int)Math.Round(y1));
            int idx = -1;
            foreach (var c in g.Contours) { idx += c.Count; W16(s, idx); }
            W16(s, 0);                                               // no instructions
            foreach (var c in g.Contours) foreach (var p in c) s.WriteByte((byte)(p.On ? 1 : 0));   // 16-bit deltas
            int last = 0;
            foreach (var c in g.Contours) foreach (var p in c) { int x = (int)Math.Round(p.X); W16(s, x - last); last = x; }
            last = 0;
            foreach (var c in g.Contours) foreach (var p in c) { int y = (int)Math.Round(p.Y); W16(s, y - last); last = y; }
            return s.ToArray();
        }

        public int AddGlyph(Glyph g) { Glyphs.Add(g); return Glyphs.Count - 1; }

        public byte[] Write()
        {
            // glyf + loca (long format)
            var glyf = new MemoryStream(); var offsets = new List<uint>();
            int maxPts = 0, maxCont = 0, advMax = 0;
            foreach (var g in Glyphs)
            {
                offsets.Add((uint)glyf.Length);
                var b = EncodeGlyph(g);
                glyf.Write(b, 0, b.Length);
                while (glyf.Length % 4 != 0) glyf.WriteByte(0);
                int pts = 0; foreach (var c in g.Contours) pts += c.Count;
                maxPts = Math.Max(maxPts, pts); maxCont = Math.Max(maxCont, g.Contours.Count); advMax = Math.Max(advMax, g.Advance);
                if (g.Raw == null && !g.IsEmpty) { double x0, y0, x1, y1; g.Bounds(out x0, out y0, out x1, out y1); g.Lsb = (int)Math.Round(x0); }
            }
            offsets.Add((uint)glyf.Length);
            var loca = new MemoryStream(); foreach (var o in offsets) W32(loca, o);
            var hmtx = new MemoryStream(); foreach (var g in Glyphs) { W16(hmtx, g.Advance); W16(hmtx, g.Lsb); }

            var head = (byte[])Tables["head"].Clone(); P16(head, 50, 1); P32(head, 8, 0);
            var hhea = (byte[])Tables["hhea"].Clone(); P16(hhea, 34, Glyphs.Count); P16(hhea, 10, Math.Max(U16(hhea, 10), advMax));
            var maxp = (byte[])Tables["maxp"].Clone(); P16(maxp, 4, Glyphs.Count);
            if (maxp.Length >= 32) { P16(maxp, 6, Math.Max(U16(maxp, 6), maxPts)); P16(maxp, 8, Math.Max(U16(maxp, 8), maxCont)); }
            var post = new byte[32]; Array.Copy(Tables["post"], post, 32); P32(post, 0, 0x00030000);   // format 3: no glyph names

            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var kv in Tables) if (kv.Key != "hdmx" && kv.Key != "LTSH" && kv.Key != "VDMX") tables[kv.Key] = kv.Value;
            tables["glyf"] = glyf.ToArray(); tables["loca"] = loca.ToArray(); tables["hmtx"] = hmtx.ToArray();
            tables["head"] = head; tables["hhea"] = hhea; tables["maxp"] = maxp; tables["post"] = post;
            tables["cmap"] = BuildCmap();

            int n = tables.Count, es = 0; while ((1 << (es + 1)) <= n) es++;
            var outp = new MemoryStream();
            W32(outp, 0x00010000); W16(outp, n); W16(outp, (1 << es) * 16); W16(outp, es); W16(outp, n * 16 - (1 << es) * 16);
            uint offset = (uint)(12 + 16 * n);
            var dir = new MemoryStream(); var body = new MemoryStream();
            foreach (var kv in tables)
            {
                dir.Write(Encoding.ASCII.GetBytes(kv.Key), 0, 4);
                W32(dir, Checksum(kv.Value)); W32(dir, offset + (uint)body.Length); W32(dir, (uint)kv.Value.Length);
                body.Write(kv.Value, 0, kv.Value.Length);
                while (body.Length % 4 != 0) body.WriteByte(0);
            }
            dir.WriteTo(outp); body.WriteTo(outp);
            var font = outp.ToArray();
            // head.checkSumAdjustment
            int headOff = -1;
            for (int i = 0; i < n; i++) if (Encoding.ASCII.GetString(font, 12 + 16 * i, 4) == "head") headOff = (int)U32(font, 12 + 16 * i + 8);
            P32(font, headOff + 8, 0xB1B0AFBA - Checksum(font));
            return font;
        }

        static uint Checksum(byte[] b)
        {
            uint sum = 0;
            for (int i = 0; i < b.Length; i += 4)
            {
                uint v = 0;
                for (int k = 0; k < 4; k++) v = (v << 8) | (i + k < b.Length ? b[i + k] : (byte)0);
                sum += v;
            }
            return sum;
        }

        byte[] BuildCmap()
        {
            // one format-4 subtable shared by the Unicode (0,3) and Windows (3,1) records
            var codes = new List<int>(); foreach (var k in Cmap.Keys) if (k < 0xFFFF) codes.Add(k);
            codes.Sort();
            var segs = new List<int[]>();   // start, end, first glyph index, contiguous?
            foreach (int c in codes)
            {
                var last = segs.Count > 0 ? segs[segs.Count - 1] : null;
                if (last != null && c == last[1] + 1 && Cmap[c] == Cmap[last[1]] + 1) last[1] = c;
                else segs.Add(new[] { c, c });
            }
            segs.Add(new[] { 0xFFFF, 0xFFFF });
            int sc = segs.Count, es = 0; while ((1 << (es + 1)) <= sc) es++;
            var t = new MemoryStream();
            W16(t, 4); W16(t, 0); W16(t, 0);  // format, length (patched), language
            W16(t, sc * 2); W16(t, (1 << es) * 2); W16(t, es); W16(t, sc * 2 - (1 << es) * 2);
            foreach (var s in segs) W16(t, s[1]);
            W16(t, 0);
            foreach (var s in segs) W16(t, s[0]);
            foreach (var s in segs) W16(t, s[0] == 0xFFFF ? 1 : (Cmap[s[0]] - s[0]) & 0xFFFF);
            foreach (var s in segs) W16(t, 0);
            var sub = t.ToArray(); P16(sub, 2, sub.Length);
            var o = new MemoryStream();
            W16(o, 0); W16(o, 2);
            W16(o, 0); W16(o, 3); W32(o, 4 + 8 * 2);
            W16(o, 3); W16(o, 1); W32(o, 4 + 8 * 2);
            o.Write(sub, 0, sub.Length);
            return o.ToArray();
        }
    }
}
