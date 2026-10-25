using System;
using System.Collections.Generic;
using System.IO;

namespace CriCompat.Acb
{
    /// <summary>
    /// CRI "AFS2" archive (.awb).  Can be backed by memory (embedded in an ACB)
    /// or by a file on disk that is read on demand (streamed waveforms).
    /// </summary>
    public sealed class Afs2Archive
    {
        struct Entry
        {
            public long Offset;
            public long Size;
        }

        readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        readonly byte[] memory;
        readonly int memoryOffset;
        readonly string path;
        readonly object fileLock = new object();
        FileStream stream;

        public static Afs2Archive FromMemory(ByteSlice s) =>
            s.IsEmpty || !s.StartsWith("AFS2") ? null : new Afs2Archive(s.Array, s.Offset, s.Count, null);

        public static Afs2Archive FromFile(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] head;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var first = new byte[16];
                ReadFully(fs, first, 16);
                if (first[0] != 'A' || first[1] != 'F' || first[2] != 'S' || first[3] != '2')
                    return null;
                int count = BitConverter.ToInt32(first, 8);
                int offSize = first[5];
                int headerLen = 16 + count * first[6] + (count + 1) * offSize;
                head = new byte[headerLen];
                fs.Position = 0;
                ReadFully(fs, head, headerLen);
            }
            return new Afs2Archive(head, 0, head.Length, path);
        }

        static void ReadFully(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(buf, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
        }

        Afs2Archive(byte[] data, int off, int len, string filePath)
        {
            memory = filePath == null ? data : null;
            memoryOffset = off;
            path = filePath;
            // header: "AFS2", u8 version, u8 offsetSize, u8 idSize, u8 pad, u32 count, u16 align, u16 subkey
            int offSize = data[off + 5];
            int idSize = data[off + 6];
            int count = BitConverter.ToInt32(data, off + 8);
            int align = BitConverter.ToUInt16(data, off + 12);
            int idsAt = off + 16;
            int offsAt = idsAt + count * idSize;
            var ids = new int[count];
            for (int i = 0; i < count; i++)
                ids[i] = idSize == 2 ? BitConverter.ToUInt16(data, idsAt + i * 2) : BitConverter.ToInt32(data, idsAt + i * 4);
            var offs = new long[count + 1];
            for (int i = 0; i <= count; i++)
            {
                int p = offsAt + i * offSize;
                offs[i] = offSize == 2 ? BitConverter.ToUInt16(data, p)
                    : offSize == 4 ? BitConverter.ToUInt32(data, p)
                    : (long)BitConverter.ToUInt64(data, p);
            }
            if (align == 0) align = 1;
            for (int i = 0; i < count; i++)
            {
                long start = offs[i];
                if (start % align != 0) start += align - start % align;
                long end = offs[i + 1];
                entries[ids[i]] = new Entry { Offset = start, Size = Math.Max(0, end - start) };
            }
        }

        public bool Contains(int id) => entries.ContainsKey(id);

        public IEnumerable<int> Ids => entries.Keys;

        /// <summary>Returns the bytes of an entry (copied for file-backed archives).</summary>
        public byte[] Read(int id)
        {
            if (!entries.TryGetValue(id, out Entry e)) return null;
            var buf = new byte[e.Size];
            if (memory != null)
            {
                Buffer.BlockCopy(memory, memoryOffset + (int)e.Offset, buf, 0, (int)e.Size);
                return buf;
            }
            lock (fileLock)
            {
                if (stream == null)
                    stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                stream.Position = e.Offset;
                ReadFully(stream, buf, (int)e.Size);
            }
            return buf;
        }

        public void Close()
        {
            lock (fileLock)
            {
                stream?.Dispose();
                stream = null;
            }
        }
    }
}
