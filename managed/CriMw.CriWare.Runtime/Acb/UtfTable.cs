using System;
using System.Collections.Generic;
using System.Text;

namespace CriCompat.Acb
{
    /// <summary>A slice of a byte array (used for @UTF "data" columns).</summary>
    public struct ByteSlice
    {
        public readonly byte[] Array;
        public readonly int Offset;
        public readonly int Count;

        public ByteSlice(byte[] array, int offset, int count)
        {
            Array = array;
            Offset = offset;
            Count = count;
        }

        public bool IsEmpty => Array == null || Count == 0;

        public byte this[int i] => Array[Offset + i];

        public ushort U16BE(int i) => (ushort)(Array[Offset + i] << 8 | Array[Offset + i + 1]);

        public uint U32BE(int i) => (uint)(Array[Offset + i] << 24 | Array[Offset + i + 1] << 16 |
                                           Array[Offset + i + 2] << 8 | Array[Offset + i + 3]);

        public float F32BE(int i)
        {
            uint v = U32BE(i);
            return BitConverter.ToSingle(BitConverter.GetBytes(v), 0);
        }

        public ushort[] U16Array()
        {
            if (IsEmpty) return System.Array.Empty<ushort>();
            var r = new ushort[Count / 2];
            for (int i = 0; i < r.Length; i++) r[i] = U16BE(i * 2);
            return r;
        }

        public float[] F32Array()
        {
            if (IsEmpty) return System.Array.Empty<float>();
            var r = new float[Count / 4];
            for (int i = 0; i < r.Length; i++) r[i] = F32BE(i * 4);
            return r;
        }

        public byte[] ToArray()
        {
            var r = new byte[Count];
            if (Count > 0) Buffer.BlockCopy(Array, Offset, r, 0, Count);
            return r;
        }

        public bool StartsWith(string ascii)
        {
            if (Count < ascii.Length) return false;
            for (int i = 0; i < ascii.Length; i++)
                if (Array[Offset + i] != ascii[i]) return false;
            return true;
        }
    }

    /// <summary>Parser for CRI's "@UTF" binary tables (big endian).</summary>
    public sealed class UtfTable
    {
        public string Name { get; private set; }
        public readonly List<string> Columns = new List<string>();
        readonly List<object[]> rows = new List<object[]>();
        readonly Dictionary<string, int> columnIndex = new Dictionary<string, int>();

        public int RowCount => rows.Count;

        public static bool IsUtf(ByteSlice s) => s.StartsWith("@UTF");

        public static UtfTable Parse(ByteSlice s)
        {
            if (!IsUtf(s)) return null;
            return new UtfTable(s.Array, s.Offset);
        }

        public static UtfTable Parse(byte[] data, int offset = 0) => new UtfTable(data, offset);

        UtfTable(byte[] d, int off)
        {
            if (d[off] != '@' || d[off + 1] != 'U' || d[off + 2] != 'T' || d[off + 3] != 'F')
                throw new FormatException("not an @UTF table");
            int b = off + 8; // table body starts after magic + size
            int rowsOff = U16(d, b + 2);
            int strOff = (int)U32(d, b + 4);
            int dataOff = (int)U32(d, b + 8);
            int nameOff = (int)U32(d, b + 12);
            int ncols = U16(d, b + 16);
            int rowSize = U16(d, b + 18);
            int nrows = (int)U32(d, b + 20);
            Name = ReadString(d, b + strOff, nameOff);

            var storages = new int[ncols];
            var types = new int[ncols];
            var consts = new object[ncols];
            int p = b + 24;
            for (int c = 0; c < ncols; c++)
            {
                int flag = d[p++];
                string cname = ReadString(d, b + strOff, (int)U32(d, p));
                p += 4;
                storages[c] = flag & 0xF0;
                types[c] = flag & 0x0F;
                if (storages[c] == 0x30)
                    consts[c] = ReadValue(d, ref p, types[c], b + strOff, b + dataOff);
                if (!columnIndex.ContainsKey(cname)) columnIndex[cname] = c;
                Columns.Add(cname);
            }
            for (int r = 0; r < nrows; r++)
            {
                p = b + rowsOff + r * rowSize;
                var row = new object[ncols];
                for (int c = 0; c < ncols; c++)
                {
                    if (storages[c] == 0x50) row[c] = ReadValue(d, ref p, types[c], b + strOff, b + dataOff);
                    else if (storages[c] == 0x30) row[c] = consts[c];
                }
                rows.Add(row);
            }
        }

        static int U16(byte[] d, int p) => d[p] << 8 | d[p + 1];
        static uint U32(byte[] d, int p) => (uint)(d[p] << 24 | d[p + 1] << 16 | d[p + 2] << 8 | d[p + 3]);

        static string ReadString(byte[] d, int strBase, int o)
        {
            int s = strBase + o;
            int e = s;
            while (e < d.Length && d[e] != 0) e++;
            return Encoding.UTF8.GetString(d, s, e - s);
        }

        static object ReadValue(byte[] d, ref int p, int type, int strBase, int dataBase)
        {
            switch (type)
            {
                case 0x0: return d[p++];
                case 0x1: return (sbyte)d[p++];
                case 0x2: { var v = (ushort)U16(d, p); p += 2; return v; }
                case 0x3: { var v = (short)U16(d, p); p += 2; return v; }
                case 0x4: { var v = U32(d, p); p += 4; return v; }
                case 0x5: { var v = (int)U32(d, p); p += 4; return v; }
                case 0x6: { ulong v = (ulong)U32(d, p) << 32 | U32(d, p + 4); p += 8; return v; }
                case 0x7: { long v = (long)((ulong)U32(d, p) << 32 | U32(d, p + 4)); p += 8; return v; }
                case 0x8:
                {
                    var v = BitConverter.ToSingle(BitConverter.GetBytes(U32(d, p)), 0);
                    p += 4;
                    return v;
                }
                case 0x9:
                {
                    ulong bits = (ulong)U32(d, p) << 32 | U32(d, p + 4);
                    p += 8;
                    return BitConverter.Int64BitsToDouble((long)bits);
                }
                case 0xA: { var s = ReadString(d, strBase, (int)U32(d, p)); p += 4; return s; }
                case 0xB:
                {
                    int o = (int)U32(d, p);
                    int n = (int)U32(d, p + 4);
                    p += 8;
                    return new ByteSlice(d, dataBase + o, n);
                }
                default:
                    throw new FormatException("unknown @UTF column type " + type);
            }
        }

        public bool Has(string column) => columnIndex.ContainsKey(column);

        public object Get(int row, string column)
        {
            if (row < 0 || row >= rows.Count) return null;
            return columnIndex.TryGetValue(column, out int c) ? rows[row][c] : null;
        }

        public long GetInt(int row, string column, long def = 0)
        {
            object v = Get(row, column);
            switch (v)
            {
                case byte b: return b;
                case sbyte sb: return sb;
                case ushort us: return us;
                case short s: return s;
                case uint ui: return ui;
                case int i: return i;
                case ulong ul: return (long)ul;
                case long l: return l;
                case float f: return (long)f;
                default: return def;
            }
        }

        public float GetFloat(int row, string column, float def = 0f)
        {
            object v = Get(row, column);
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v == null) return def;
            return GetInt(row, column);
        }

        public string GetString(int row, string column) => Get(row, column) as string ?? "";

        public ByteSlice GetData(int row, string column)
        {
            object v = Get(row, column);
            return v is ByteSlice s ? s : default;
        }

        public UtfTable GetTable(int row, string column)
        {
            var s = GetData(row, column);
            return UtfTable.IsUtf(s) ? Parse(s) : null;
        }
    }
}
