// Minimal reader/writer for a Unity 2018 "SerializedFile" (format version 17, no type trees):
// finds the Font objects, reads their embedded TrueType data, replaces it and rewrites the file
// (object offsets/sizes and the file size are updated, every other byte is kept as is).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LostFrontierTranslation.FontGenerator
{
    public class UnityAssets
    {
        public class Obj
        {
            public long PathId; public uint Start, Size; public int ClassId;
            public int StartFieldPos, SizeFieldPos;   // where Start/Size are stored in the metadata
            public byte[] NewData;                    // replacement bytes, or null
        }

        readonly byte[] file;
        readonly uint dataOffset;
        public readonly List<Obj> Objects = new List<Obj>();
        const int FontClassId = 128;

        static uint BE32(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }

        public UnityAssets(byte[] data)
        {
            file = data;
            uint version = BE32(file, 8);
            dataOffset = BE32(file, 12);
            if (version != 17 || file[16] != 0) throw new NotSupportedException("unsupported asset format (version " + version + ")");
            var r = new BinaryReader(new MemoryStream(file)); r.BaseStream.Position = 20;
            while (r.ReadByte() != 0) { }                    // Unity version string
            r.ReadInt32();                                   // target platform
            bool typeTree = r.ReadBoolean();
            int typeCount = r.ReadInt32();
            var classIds = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                classIds[i] = r.ReadInt32();
                r.ReadByte();                                // isStrippedType
                r.ReadInt16();                               // scriptTypeIndex
                if (classIds[i] == 114) r.ReadBytes(16);     // script id (MonoBehaviour)
                r.ReadBytes(16);                             // old type hash
                if (typeTree)
                {
                    int nodes = r.ReadInt32(), strings = r.ReadInt32();
                    r.ReadBytes(nodes * 24 + strings);
                }
            }
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                while (r.BaseStream.Position % 4 != 0) r.ReadByte();
                var o = new Obj { PathId = r.ReadInt64() };
                o.StartFieldPos = (int)r.BaseStream.Position; o.Start = r.ReadUInt32();
                o.SizeFieldPos = (int)r.BaseStream.Position; o.Size = r.ReadUInt32();
                o.ClassId = classIds[r.ReadInt32()];
                Objects.Add(o);
            }
        }

        public byte[] Data(Obj o) { var b = new byte[o.Size]; Array.Copy(file, dataOffset + o.Start, b, 0, o.Size); return b; }

        public static string NameOf(byte[] objData)
        {
            int n = BitConverter.ToInt32(objData, 0);
            return Encoding.UTF8.GetString(objData, 4, n);
        }

        /// Fonts in this file: name -> (object, offset of the int32 length of the embedded TrueType data)
        public Dictionary<string, KeyValuePair<Obj, int>> Fonts()
        {
            var res = new Dictionary<string, KeyValuePair<Obj, int>>();
            foreach (var o in Objects)
            {
                if (o.ClassId != FontClassId) continue;
                var d = Data(o);
                for (int i = 4; i + 8 < d.Length; i++)
                {
                    int len = BitConverter.ToInt32(d, i);
                    if (len < 1000 || i + 4 + len > d.Length) continue;
                    uint tag = BE32(d, i + 4);
                    if (tag == 0x00010000 || tag == 0x74727565) { res[NameOf(d)] = new KeyValuePair<Obj, int>(o, i); break; }
                }
            }
            return res;
        }

        public byte[] FontData(KeyValuePair<Obj, int> font)
        {
            var d = Data(font.Key); int len = BitConverter.ToInt32(d, font.Value);
            var t = new byte[len]; Array.Copy(d, font.Value + 4, t, 0, len); return t;
        }

        public void ReplaceFontData(KeyValuePair<Obj, int> font, byte[] ttf)
        {
            var d = Data(font.Key); int at = font.Value; int oldLen = BitConverter.ToInt32(d, at);
            int oldEnd = Align4(at + 4 + oldLen);
            var m = new MemoryStream();
            m.Write(d, 0, at);
            m.Write(BitConverter.GetBytes(ttf.Length), 0, 4);
            m.Write(ttf, 0, ttf.Length);
            while (m.Length % 4 != 0) m.WriteByte(0);
            m.Write(d, oldEnd, d.Length - oldEnd);
            font.Key.NewData = m.ToArray();
        }

        static int Align4(int v) { return (v + 3) & ~3; }
        static long Align8(long v) { return (v + 7) & ~7L; }

        public byte[] Save()
        {
            var sorted = new List<Obj>(Objects); sorted.Sort((a, b) => a.Start.CompareTo(b.Start));
            var outp = new MemoryStream();
            outp.Write(file, 0, (int)dataOffset);
            long shift = 0; long oldPos = dataOffset;
            foreach (var o in sorted)
            {
                long oldStart = dataOffset + o.Start;
                // copy the padding/gap between objects unchanged
                if (oldStart > oldPos) outp.Write(file, (int)oldPos, (int)(oldStart - oldPos));
                var data = o.NewData ?? Data(o);
                uint newStart = (uint)(oldStart + shift - dataOffset);
                outp.Write(data, 0, data.Length);
                oldPos = oldStart + o.Size;
                if (o.NewData != null)
                {
                    // keep the following objects 8-byte aligned
                    long pad = Align8(o.Start + shift + data.Length) - (o.Start + shift + data.Length);
                    long oldPad = Align8(o.Start + o.Size) - (o.Start + o.Size);
                    for (long k = 0; k < pad; k++) outp.WriteByte(0);
                    oldPos += oldPad;
                    shift += data.Length + pad - (o.Size + oldPad);
                }
                o.Start = newStart; o.Size = (uint)data.Length;
            }
            if (oldPos < file.Length) outp.Write(file, (int)oldPos, (int)(file.Length - oldPos));
            var res = outp.ToArray();
            foreach (var o in Objects)
            {
                Array.Copy(BitConverter.GetBytes(o.Start), 0, res, o.StartFieldPos, 4);
                Array.Copy(BitConverter.GetBytes(o.Size), 0, res, o.SizeFieldPos, 4);
            }
            uint size = (uint)res.Length;     // header file size (big-endian)
            res[4] = (byte)(size >> 24); res[5] = (byte)(size >> 16); res[6] = (byte)(size >> 8); res[7] = (byte)size;
            return res;
        }
    }
}
