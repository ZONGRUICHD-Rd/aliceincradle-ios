namespace PortTool;

/// <summary>Minimal MD4 (RFC 1320), needed to reproduce Unity's script fileIDs.</summary>
static class Md4
{
    public static byte[] Hash(byte[] input)
    {
        uint a = 0x67452301, b = 0xefcdab89, c = 0x98badcfe, d = 0x10325476;
        long bitLen = (long)input.Length * 8;
        int padLen = ((input.Length + 8) / 64 + 1) * 64;
        var msg = new byte[padLen];
        Array.Copy(input, msg, input.Length);
        msg[input.Length] = 0x80;
        for (int i = 0; i < 8; i++) msg[padLen - 8 + i] = (byte)(bitLen >> (8 * i));
        var x = new uint[16];
        for (int off = 0; off < padLen; off += 64)
        {
            for (int i = 0; i < 16; i++) x[i] = BitConverter.ToUInt32(msg, off + i * 4);
            uint aa = a, bb = b, cc = c, dd = d;
            static uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));
            static uint F(uint p, uint q, uint r) => (p & q) | (~p & r);
            static uint G(uint p, uint q, uint r) => (p & q) | (p & r) | (q & r);
            static uint H(uint p, uint q, uint r) => p ^ q ^ r;
            int[] s1 = { 3, 7, 11, 19 };
            for (int i = 0; i < 16; i++)
            {
                uint t = a + F(b, c, d) + x[i];
                a = d; d = c; c = b; b = Rol(t, s1[i % 4]);
            }
            int[] s2 = { 3, 5, 9, 13 };
            for (int i = 0; i < 16; i++)
            {
                int k = (i % 4) * 4 + i / 4;
                uint t = a + G(b, c, d) + x[k] + 0x5a827999;
                a = d; d = c; c = b; b = Rol(t, s2[i % 4]);
            }
            int[] s3 = { 3, 9, 11, 15 };
            int[] order = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };
            for (int i = 0; i < 16; i++)
            {
                uint t = a + H(b, c, d) + x[order[i]] + 0x6ed9eba1;
                a = d; d = c; c = b; b = Rol(t, s3[i % 4]);
            }
            a += aa; b += bb; c += cc; d += dd;
        }
        var r = new byte[16];
        BitConverter.GetBytes(a).CopyTo(r, 0);
        BitConverter.GetBytes(b).CopyTo(r, 4);
        BitConverter.GetBytes(c).CopyTo(r, 8);
        BitConverter.GetBytes(d).CopyTo(r, 12);
        return r;
    }
}
