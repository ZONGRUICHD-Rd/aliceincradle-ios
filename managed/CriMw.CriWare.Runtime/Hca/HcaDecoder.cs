// C# port of vgmstream's clHCA decoder (src/coding/libs/clhca.c), supporting
// HCA v1.1 - v3.0. See HcaTables.cs for the original copyright notice (ISC).
//
// Original authors: nyaga (decompilation), kode54 (C port),
// bnnm (v3 re-reverse engineering), Thealexbarney (VGAudio reference).
using System;

namespace CriCompat.Hca
{
    internal sealed class HcaException : Exception
    {
        public HcaException(string msg) : base(msg) { }
    }

    /// <summary>Decodes one HCA stream frame by frame into float PCM.</summary>
    internal sealed class HcaDecoder
    {
        public const int SubFrames = 8;
        public const int SamplesPerSubFrame = 128;
        public const int SamplesPerFrame = SubFrames * SamplesPerSubFrame;
        const int MdctBits = 7;
        const uint Mask = 0x7F7F7F7F;

        const int V200 = 0x0200;

        enum ChannelType { Discrete = 0, StereoPrimary = 1, StereoSecondary = 2 }

        sealed class Channel
        {
            public ChannelType type;
            public int codedCount;
            public readonly byte[] intensity = new byte[SubFrames];
            public readonly byte[] scalefactors = new byte[SamplesPerSubFrame];
            public readonly byte[] resolution = new byte[SamplesPerSubFrame];
            public readonly byte[] noises = new byte[SamplesPerSubFrame];
            public int noiseCount;
            public int validCount;
            public readonly float[] gain = new float[SamplesPerSubFrame];
            public readonly float[][] spectra = NewJagged(SubFrames, SamplesPerSubFrame);
            public readonly float[] temp = new float[SamplesPerSubFrame];
            public readonly float[] imdctPrevious = new float[SamplesPerSubFrame];
            public readonly float[][] wave = NewJagged(SubFrames, SamplesPerSubFrame);
        }

        static float[][] NewJagged(int a, int b)
        {
            var r = new float[a][];
            for (int i = 0; i < a; i++) r[i] = new float[b];
            return r;
        }

        static readonly float[] ScalingTable = ToFloats(HcaTables.ScalingTableHex);
        static readonly float[] RangeTable = ToFloats(HcaTables.RangeTableHex);
        static readonly float[] ScaleConversionTable = ToFloats(HcaTables.ScaleConversionTableHex);
        static readonly float[] IntensityRatioTable = ToFloats(HcaTables.IntensityRatioTableHex);
        static readonly float[] SinTables = ToFloats(HcaTables.SinTablesHex);
        static readonly float[] CosTables = ToFloats(HcaTables.CosTablesHex);
        static readonly float[] ImdctWindow = ToFloats(HcaTables.ImdctWindowHex);

        static float[] ToFloats(uint[] hex)
        {
            var f = new float[hex.Length];
            for (int i = 0; i < hex.Length; i++)
                f[i] = BitConverter.ToSingle(BitConverter.GetBytes(hex[i]), 0);
            return f;
        }

        // header values
        public int Version { get; private set; }
        public int HeaderSize { get; private set; }
        public int Channels { get; private set; }
        public int SampleRate { get; private set; }
        public int FrameCount { get; private set; }
        public int EncoderDelay { get; private set; }
        public int EncoderPadding { get; private set; }
        public int FrameSize { get; private set; }
        public bool LoopFlag { get; private set; }
        public int LoopStartFrame { get; private set; }
        public int LoopEndFrame { get; private set; }
        public int LoopStartDelay { get; private set; }
        public int LoopEndPadding { get; private set; }
        public float RvaVolume { get; private set; } = 1f;

        public int SampleCount => FrameCount * SamplesPerFrame - EncoderDelay - EncoderPadding;
        public int LoopStartSample => LoopStartFrame * SamplesPerFrame - EncoderDelay + LoopStartDelay;
        public int LoopEndSample => LoopEndFrame * SamplesPerFrame - EncoderDelay + (SamplesPerFrame - LoopEndPadding);

        int minResolution, maxResolution, trackCount, channelConfig, totalBandCount,
            baseBandCount, stereoBandCount, bandsPerHfrGroup, msStereo, hfrGroupCount,
            athType, ciphType;
        readonly byte[] athCurve = new byte[SamplesPerSubFrame];
        readonly byte[] cipherTable = new byte[256];
        uint random;
        Channel[] channels;
        byte[] frameBuffer;

        // ------------------------------------------------------------------
        // bit reader
        byte[] brData;
        int brSize;
        int brBit;

        void BrInit(byte[] data, int sizeBytes)
        {
            brData = data;
            brSize = sizeBytes * 8;
            brBit = 0;
        }

        uint BrPeek(int bits)
        {
            int pos = brBit;
            if (bits == 0 || pos + bits > brSize) return 0;
            int bitRem = pos & 7;
            int byteIdx = pos >> 3;
            // read up to 4 bytes (bits <= 25 in practice during decode)
            uint v = 0;
            int need = (bitRem + bits + 7) >> 3;
            for (int i = 0; i < need; i++)
            {
                int bi = byteIdx + i;
                v = (v << 8) | (bi < brData.Length ? brData[bi] : 0u);
            }
            int totalBits = need * 8;
            v <<= (32 - totalBits); // left align
            v <<= bitRem;
            return v >> (32 - bits);
        }

        uint BrRead(int bits)
        {
            uint v = BrPeek(bits);
            brBit += bits;
            return v;
        }

        void BrSkip(int bits) { brBit += bits; }

        static ushort Crc16(byte[] data, int offset, int size)
        {
            ushort sum = 0;
            for (int i = 0; i < size; i++)
                sum = (ushort)((sum << 8) ^ HcaTables.CrcMaskTable[(sum >> 8) ^ data[offset + i]]);
            return sum;
        }

        // ------------------------------------------------------------------
        public static bool IsHca(byte[] data, int offset)
        {
            if (data == null || data.Length < offset + 8) return false;
            uint magic = (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
            return (magic & Mask) == 0x48434100;
        }

        /// <summary>Parses the HCA header found at the start of <paramref name="data"/>.</summary>
        public HcaDecoder(byte[] data, int offset, int length, ulong keycode = 0)
        {
            if (offset != 0 || length != data.Length)
            {
                var copy = new byte[length];
                Buffer.BlockCopy(data, offset, copy, 0, length);
                data = copy;
            }
            int size = length;
            BrInit(data, size);
            if ((BrPeek(32) & Mask) != 0x48434100) throw new HcaException("not an HCA stream");
            BrSkip(32);
            Version = (int)BrRead(16);
            HeaderSize = (int)BrRead(16);
            if (Version != 0x0101 && Version != 0x0102 && Version != 0x0103 && Version != V200 && Version != 0x0300)
                throw new HcaException("unsupported HCA version " + Version.ToString("X"));
            if (size < HeaderSize) throw new HcaException("truncated header");
            if (Crc16(data, 0, HeaderSize) != 0) throw new HcaException("header checksum");
            size -= 8;

            if (size >= 0x10 && (BrPeek(32) & Mask) == 0x666D7400)
            {
                BrSkip(32);
                Channels = (int)BrRead(8);
                SampleRate = (int)BrRead(24);
                FrameCount = (int)BrRead(32);
                EncoderDelay = (int)BrRead(16);
                EncoderPadding = (int)BrRead(16);
                if (Channels < 1 || Channels > 16 || FrameCount == 0 || SampleRate < 1)
                    throw new HcaException("bad fmt");
                size -= 0x10;
            }
            else throw new HcaException("missing fmt");

            if (size >= 0x10 && (BrPeek(32) & Mask) == 0x636F6D70)
            {
                BrSkip(32);
                FrameSize = (int)BrRead(16);
                minResolution = (int)BrRead(8);
                maxResolution = (int)BrRead(8);
                trackCount = (int)BrRead(8);
                channelConfig = (int)BrRead(8);
                totalBandCount = (int)BrRead(8);
                baseBandCount = (int)BrRead(8);
                stereoBandCount = (int)BrRead(8);
                bandsPerHfrGroup = (int)BrRead(8);
                msStereo = (int)BrRead(8);
                BrRead(8);
                size -= 0x10;
            }
            else if (size >= 0x0c && (BrPeek(32) & Mask) == 0x64656300)
            {
                BrSkip(32);
                FrameSize = (int)BrRead(16);
                minResolution = (int)BrRead(8);
                maxResolution = (int)BrRead(8);
                totalBandCount = (int)BrRead(8) + 1;
                baseBandCount = (int)BrRead(8) + 1;
                trackCount = (int)BrRead(4);
                channelConfig = (int)BrRead(4);
                int stereoType = (int)BrRead(8);
                if (stereoType == 0) baseBandCount = totalBandCount;
                stereoBandCount = totalBandCount - baseBandCount;
                bandsPerHfrGroup = 0;
                size -= 0x0c;
            }
            else throw new HcaException("missing comp/dec");

            if (size >= 0x08 && (BrPeek(32) & Mask) == 0x76627200)
            {
                BrSkip(32);
                BrRead(16);
                BrRead(16);
                size -= 0x08;
            }

            if (size >= 0x06 && (BrPeek(32) & Mask) == 0x61746800)
            {
                BrSkip(32);
                athType = (int)BrRead(16);
            }
            else athType = Version < V200 ? 1 : 0;

            if (size >= 0x10 && (BrPeek(32) & Mask) == 0x6C6F6F70)
            {
                BrSkip(32);
                LoopStartFrame = (int)BrRead(32);
                LoopEndFrame = (int)BrRead(32);
                LoopStartDelay = (int)BrRead(16);
                LoopEndPadding = (int)BrRead(16);
                LoopFlag = true;
                if (!(LoopStartFrame <= LoopEndFrame && LoopEndFrame < FrameCount))
                    throw new HcaException("bad loop");
                size -= 0x10;
            }

            if (size >= 0x06 && (BrPeek(32) & Mask) == 0x63697068)
            {
                BrSkip(32);
                ciphType = (int)BrRead(16);
                if (ciphType != 0 && ciphType != 1 && ciphType != 56) throw new HcaException("bad ciph");
                size -= 0x06;
            }

            if (size >= 0x08 && (BrPeek(32) & Mask) == 0x72766100)
            {
                BrSkip(32);
                uint bits = BrRead(32);
                RvaVolume = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
                size -= 0x08;
            }

            if (FrameSize < 8 || FrameSize > 0xFFFF) throw new HcaException("bad frame size");
            if (Version <= V200)
            {
                if (minResolution != 1 || maxResolution != 15) throw new HcaException("bad resolution");
            }
            else if (minResolution > maxResolution || maxResolution > 15)
                throw new HcaException("bad resolution");

            if (trackCount == 0) trackCount = 1;
            if (trackCount > Channels) throw new HcaException("bad track count");
            if (totalBandCount > SamplesPerSubFrame || totalBandCount == 0 ||
                baseBandCount + stereoBandCount > totalBandCount ||
                baseBandCount + stereoBandCount == 0 ||
                stereoBandCount > baseBandCount ||
                bandsPerHfrGroup > SamplesPerSubFrame)
                throw new HcaException("bad bands");

            int hfrBands = totalBandCount - baseBandCount - stereoBandCount;
            hfrGroupCount = bandsPerHfrGroup > 0 ? (hfrBands + bandsPerHfrGroup - 1) / bandsPerHfrGroup : 0;

            var types = new ChannelType[16];
            SetupChannelTypes(Channels, trackCount, channelConfig, stereoBandCount, types);
            channels = new Channel[Channels];
            for (int i = 0; i < Channels; i++)
            {
                channels[i] = new Channel
                {
                    type = types[i],
                    codedCount = baseBandCount + (types[i] == ChannelType.StereoSecondary ? 0 : stereoBandCount)
                };
            }
            random = 1;

            if (athType == 1) AthInit1(SampleRate);
            else if (athType != 0) throw new HcaException("bad ath type");
            CipherInit(ciphType, keycode);
            if (msStereo != 0) throw new HcaException("ms stereo unsupported");
            frameBuffer = new byte[FrameSize];
        }

        static void SetupChannelTypes(int channels, int trackCount, int channelConfig, int stereoBandCount, ChannelType[] ct)
        {
            int cpt = channels / trackCount;
            if (stereoBandCount <= 0 || cpt <= 1) return;
            for (int t = 0; t < trackCount; t++)
            {
                int o = t * cpt;
                switch (cpt)
                {
                    case 2:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary; break;
                    case 3:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary; ct[o + 2] = ChannelType.Discrete; break;
                    case 4:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary;
                        if (channelConfig == 0) { ct[o + 2] = ChannelType.StereoPrimary; ct[o + 3] = ChannelType.StereoSecondary; }
                        else { ct[o + 2] = ChannelType.Discrete; ct[o + 3] = ChannelType.Discrete; }
                        break;
                    case 5:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary; ct[o + 2] = ChannelType.Discrete;
                        if (channelConfig <= 2) { ct[o + 3] = ChannelType.StereoPrimary; ct[o + 4] = ChannelType.StereoSecondary; }
                        else { ct[o + 3] = ChannelType.Discrete; ct[o + 4] = ChannelType.Discrete; }
                        break;
                    case 6:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary; ct[o + 2] = ChannelType.Discrete;
                        ct[o + 3] = ChannelType.Discrete; ct[o + 4] = ChannelType.StereoPrimary; ct[o + 5] = ChannelType.StereoSecondary;
                        break;
                    case 7:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary; ct[o + 2] = ChannelType.Discrete;
                        ct[o + 3] = ChannelType.Discrete; ct[o + 4] = ChannelType.StereoPrimary; ct[o + 5] = ChannelType.StereoSecondary;
                        ct[o + 6] = ChannelType.Discrete;
                        break;
                    case 8:
                        ct[o] = ChannelType.StereoPrimary; ct[o + 1] = ChannelType.StereoSecondary; ct[o + 2] = ChannelType.Discrete;
                        ct[o + 3] = ChannelType.Discrete; ct[o + 4] = ChannelType.StereoPrimary; ct[o + 5] = ChannelType.StereoSecondary;
                        ct[o + 6] = ChannelType.StereoPrimary; ct[o + 7] = ChannelType.StereoSecondary;
                        break;
                }
            }
        }

        void AthInit1(int sampleRate)
        {
            uint acc = 0;
            for (int i = 0; i < SamplesPerSubFrame; i++)
            {
                acc += (uint)sampleRate;
                uint index = acc >> 13;
                if (index >= 654)
                {
                    for (int j = i; j < SamplesPerSubFrame; j++) athCurve[j] = 0xFF;
                    break;
                }
                athCurve[i] = HcaTables.AthBaseCurve[index];
            }
        }

        void CipherInit(int type, ulong keycode)
        {
            if (type == 56 && keycode == 0) type = 0;
            switch (type)
            {
                case 0:
                    for (int i = 0; i < 256; i++) cipherTable[i] = (byte)i;
                    break;
                case 1:
                {
                    int v = 0;
                    for (int i = 1; i < 255; i++)
                    {
                        v = (v * 13 + 11) & 0xFF;
                        if (v == 0 || v == 0xFF) v = (v * 13 + 11) & 0xFF;
                        cipherTable[i] = (byte)v;
                    }
                    cipherTable[0] = 0;
                    cipherTable[0xFF] = 0xFF;
                    break;
                }
                case 56:
                    CipherInit56(keycode);
                    break;
            }
        }

        static void Cipher56Table(byte[] r, byte key)
        {
            int mul = ((key & 1) << 3) | 5;
            int add = (key & 0xE) | 1;
            key >>= 4;
            for (int i = 0; i < 16; i++)
            {
                key = (byte)((key * mul + add) & 0xF);
                r[i] = key;
            }
        }

        void CipherInit56(ulong keycode)
        {
            var kc = new byte[8];
            var seed = new byte[16];
            var bas = new byte[256];
            var baseR = new byte[16];
            var baseC = new byte[16];
            if (keycode != 0) keycode--;
            for (int r = 0; r < 7; r++) { kc[r] = (byte)(keycode & 0xFF); keycode >>= 8; }
            seed[0x00] = kc[1]; seed[0x01] = (byte)(kc[1] ^ kc[6]); seed[0x02] = (byte)(kc[2] ^ kc[3]);
            seed[0x03] = kc[2]; seed[0x04] = (byte)(kc[2] ^ kc[1]); seed[0x05] = (byte)(kc[3] ^ kc[4]);
            seed[0x06] = kc[3]; seed[0x07] = (byte)(kc[3] ^ kc[2]); seed[0x08] = (byte)(kc[4] ^ kc[5]);
            seed[0x09] = kc[4]; seed[0x0A] = (byte)(kc[4] ^ kc[3]); seed[0x0B] = (byte)(kc[5] ^ kc[6]);
            seed[0x0C] = kc[5]; seed[0x0D] = (byte)(kc[5] ^ kc[4]); seed[0x0E] = (byte)(kc[6] ^ kc[1]);
            seed[0x0F] = kc[6];
            Cipher56Table(baseR, kc[0]);
            for (int r = 0; r < 16; r++)
            {
                Cipher56Table(baseC, seed[r]);
                byte nb = (byte)(baseR[r] << 4);
                for (int c = 0; c < 16; c++) bas[r * 16 + c] = (byte)(nb | baseC[c]);
            }
            uint x = 0;
            int pos = 1;
            for (int i = 0; i < 256; i++)
            {
                x = (x + 17) & 0xFF;
                if (bas[x] != 0 && bas[x] != 0xFF) cipherTable[pos++] = bas[x];
            }
            cipherTable[0] = 0;
            cipherTable[0xFF] = 0xFF;
        }

        /// <summary>Resets the decoder state (call before decoding a non-consecutive frame).</summary>
        public void Reset()
        {
            random = 1;
            foreach (var ch in channels) Array.Clear(ch.imdctPrevious, 0, SamplesPerSubFrame);
        }

        /// <summary>
        /// Decodes one frame from src[offset..offset+FrameSize] and writes
        /// SamplesPerFrame interleaved float samples into dst starting at dstOffset.
        /// Returns false if the frame is corrupt (output is silence then).
        /// </summary>
        public bool DecodeFrame(byte[] src, int offset, float[] dst, int dstOffset)
        {
            Buffer.BlockCopy(src, offset, frameBuffer, 0, FrameSize);
            bool ok = Unpack(frameBuffer);
            if (ok)
            {
                Transform();
                int nch = Channels;
                for (int sf = 0; sf < SubFrames; sf++)
                {
                    int baseIdx = dstOffset + sf * SamplesPerSubFrame * nch;
                    for (int j = 0; j < SamplesPerSubFrame; j++)
                    {
                        int o = baseIdx + j * nch;
                        for (int c = 0; c < nch; c++) dst[o + c] = channels[c].wave[sf][j];
                    }
                }
            }
            else
            {
                Array.Clear(dst, dstOffset, SamplesPerFrame * Channels);
            }
            return ok;
        }

        bool Unpack(byte[] data)
        {
            BrInit(data, FrameSize);
            if (BrRead(16) != 0xFFFF) return false;
            if (Crc16(data, 0, FrameSize) != 0) return false;
            for (int i = 0; i < FrameSize; i++) data[i] = cipherTable[data[i]];

            uint noiseLevel = BrRead(9);
            uint boundary = BrRead(7);
            uint packedNoiseLevel = (noiseLevel << 8) - boundary;
            foreach (var ch in channels)
            {
                if (!UnpackScalefactors(ch)) return false;
                if (!UnpackIntensity(ch)) return false;
                CalculateResolution(ch, packedNoiseLevel);
                CalculateGain(ch);
            }
            for (int sf = 0; sf < SubFrames; sf++)
                foreach (var ch in channels) DequantizeCoefficients(ch, sf);
            return true;
        }

        bool UnpackScalefactors(Channel ch)
        {
            int csCount = ch.codedCount;
            int hsCount = (ch.type == ChannelType.StereoSecondary || hfrGroupCount == 0 || Version <= V200) ? 0 : hfrGroupCount;
            int sfCount = csCount + hsCount;
            int deltaBits = (int)BrRead(3);
            var sfs = ch.scalefactors;
            if (deltaBits >= 6)
            {
                for (int i = 0; i < sfCount; i++) sfs[i] = (byte)BrRead(6);
            }
            else if (deltaBits > 0)
            {
                int expected = (1 << deltaBits) - 1;
                int value = (int)BrRead(6);
                sfs[0] = (byte)value;
                for (int i = 1; i < sfCount; i++)
                {
                    int delta = (int)BrRead(deltaBits);
                    if (delta == expected)
                    {
                        value = (int)BrRead(6);
                    }
                    else
                    {
                        int test = value + (delta - (expected >> 1));
                        if (test < 0 || test >= 64) return false;
                        value = (value - (expected >> 1) + delta) & 0x3F;
                    }
                    sfs[i] = (byte)value;
                }
            }
            else
            {
                Array.Clear(sfs, 0, SamplesPerSubFrame);
            }
            for (int i = hsCount; i > 0; i--)
                sfs[SamplesPerSubFrame - 1 - hsCount + i] = sfs[csCount - 1 + i];
            return true;
        }

        bool UnpackIntensity(Channel ch)
        {
            if (ch.type == ChannelType.StereoSecondary)
            {
                if (Version <= V200)
                {
                    byte value = (byte)BrPeek(4);
                    ch.intensity[0] = value;
                    if (value < 15)
                    {
                        BrSkip(4);
                        for (int i = 1; i < SubFrames; i++) ch.intensity[i] = (byte)BrRead(4);
                    }
                }
                else
                {
                    int value = (int)BrPeek(4);
                    if (value < 15)
                    {
                        BrSkip(4);
                        int deltaBits = (int)BrRead(2);
                        ch.intensity[0] = (byte)value;
                        if (deltaBits == 3)
                        {
                            for (int i = 1; i < SubFrames; i++) ch.intensity[i] = (byte)BrRead(4);
                        }
                        else
                        {
                            int bmax = (2 << deltaBits) - 1;
                            int bits = deltaBits + 1;
                            for (int i = 1; i < SubFrames; i++)
                            {
                                int delta = (int)BrRead(bits);
                                if (delta == bmax)
                                {
                                    value = (int)BrRead(4);
                                }
                                else
                                {
                                    value = (value - (bmax >> 1) + delta) & 0xFF;
                                    if (value > 15) return false;
                                }
                                ch.intensity[i] = (byte)value;
                            }
                        }
                    }
                    else
                    {
                        BrSkip(4);
                        for (int i = 0; i < SubFrames; i++) ch.intensity[i] = 7;
                    }
                }
            }
            else if (Version <= V200)
            {
                int start = SamplesPerSubFrame - hfrGroupCount;
                for (int i = 0; i < hfrGroupCount; i++) ch.scalefactors[start + i] = (byte)BrRead(6);
            }
            return true;
        }

        void CalculateResolution(Channel ch, uint packedNoiseLevel)
        {
            int crCount = ch.codedCount;
            int noiseCount = 0, validCount = 0;
            for (int i = 0; i < crCount; i++)
            {
                int newRes = 0;
                int sf = ch.scalefactors[i];
                if (sf > 0)
                {
                    int noise = athCurve[i] + (int)((packedNoiseLevel + (uint)i) >> 8);
                    int curvePos = noise + 1 - ((5 * sf) >> 1);
                    if (curvePos < 0) newRes = 15;
                    else if (curvePos <= 65) newRes = HcaTables.InvertTable[curvePos];
                    else newRes = 0;
                    if (newRes > maxResolution) newRes = maxResolution;
                    else if (newRes < minResolution) newRes = minResolution;
                    if (newRes < 1) ch.noises[noiseCount++] = (byte)i;
                    else ch.noises[SamplesPerSubFrame - 1 - validCount++] = (byte)i;
                }
                ch.resolution[i] = (byte)newRes;
            }
            ch.noiseCount = noiseCount;
            ch.validCount = validCount;
            Array.Clear(ch.resolution, crCount, SamplesPerSubFrame - crCount);
        }

        static void CalculateGain(Channel ch)
        {
            for (int i = 0; i < ch.codedCount; i++)
                ch.gain[i] = ScalingTable[ch.scalefactors[i]] * RangeTable[ch.resolution[i]];
        }

        void DequantizeCoefficients(Channel ch, int sf)
        {
            int cc = ch.codedCount;
            var spectra = ch.spectra[sf];
            for (int i = 0; i < cc; i++)
            {
                float qc;
                int res = ch.resolution[i];
                int bits = HcaTables.MaxBitTable[res];
                uint code = BrRead(bits);
                if (res > 7)
                {
                    int signedCode = (1 - (int)((code & 1) << 1)) * (int)(code >> 1);
                    if (signedCode == 0) BrSkip(-1);
                    qc = signedCode;
                }
                else
                {
                    int index = (res << 4) + (int)code;
                    int skip = HcaTables.ReadBitTable[index] - bits;
                    BrSkip(skip);
                    qc = HcaTables.ReadValTable[index];
                }
                spectra[i] = ch.gain[i] * qc;
            }
            Array.Clear(spectra, cc, SamplesPerSubFrame - cc);
        }

        void Transform()
        {
            for (int sf = 0; sf < SubFrames; sf++)
            {
                int hfrChannels = (channelConfig & 0x80) != 0 ? 1 : Channels;
                for (int c = 0; c < hfrChannels; c++)
                {
                    ReconstructNoise(channels[c], sf);
                    ReconstructHighFrequency(channels[c], sf);
                }
                if (stereoBandCount > 0)
                {
                    for (int c = 0; c < Channels - 1; c++)
                        ApplyIntensityStereo(channels[c], channels[c + 1], sf);
                }
                for (int c = 0; c < Channels; c++) ImdctTransform(channels[c], sf);
            }
        }

        void ReconstructNoise(Channel ch, int sf)
        {
            if (minResolution > 0) return;
            if (ch.validCount == 0 || ch.noiseCount == 0) return;
            if (!(msStereo == 0 || ch.type == ChannelType.StereoPrimary)) return;
            uint rnd = random;
            var spectra = ch.spectra[sf];
            for (int i = 0; i < ch.noiseCount; i++)
            {
                rnd = 0x343FD * rnd + 0x269EC3;
                uint rout = (rnd >> 16) & 0x7FFF;
                int randomIndex = SamplesPerSubFrame - ch.validCount + (int)((rout * (uint)ch.validCount) >> 15);
                int target = ch.noises[i];
                int source = ch.noises[randomIndex];
                int d = ch.scalefactors[target] - ch.scalefactors[source] + 62;
                int scIndex = d & ~(d >> 31);
                spectra[target] = ScaleConversionTable[scIndex] * spectra[source];
            }
            random = rnd;
        }

        void ReconstructHighFrequency(Channel ch, int sf)
        {
            if (bandsPerHfrGroup == 0) return;
            if (ch.type == ChannelType.StereoSecondary) return;
            int startBand = stereoBandCount + baseBandCount;
            int highband = startBand;
            int lowband = startBand - 1;
            int hfrBase = 128 - hfrGroupCount;
            int groupLimit = Version <= V200 ? hfrGroupCount : (hfrGroupCount >> 1);
            var spectra = ch.spectra[sf];
            for (int group = 0; group < hfrGroupCount; group++)
            {
                int adjust = group < groupLimit ? -1 : 1;
                if (highband >= totalBandCount || lowband < 0) break;
                for (int i = 0; i < bandsPerHfrGroup; i++)
                {
                    if (highband >= totalBandCount || lowband < 0) break;
                    int sc = ch.scalefactors[hfrBase + group] - ch.scalefactors[lowband] + 63;
                    sc = sc & ~(sc >> 31);
                    spectra[highband] = ScaleConversionTable[sc] * spectra[lowband];
                    highband++;
                    lowband += adjust;
                }
            }
            spectra[highband - 1] = 0f;
        }

        void ApplyIntensityStereo(Channel l, Channel r, int sf)
        {
            if (l.type != ChannelType.StereoPrimary) return;
            float ratioL = IntensityRatioTable[r.intensity[sf]];
            float ratioR = 2.0f - ratioL;
            var spL = l.spectra[sf];
            var spR = r.spectra[sf];
            for (int band = baseBandCount; band < totalBandCount; band++)
            {
                float cl = spL[band] * ratioL;
                float cr = spL[band] * ratioR;
                spL[band] = cl;
                spR[band] = cr;
            }
        }

        static void ImdctTransform(Channel ch, int sf)
        {
            const int size = SamplesPerSubFrame;
            const int half = size / 2;
            float[] spectra = ch.spectra[sf];
            float[] tmp = ch.temp;

            // pre-rotation butterflies
            {
                int count1 = 1, count2 = half;
                float[] t1 = spectra, t2 = tmp;
                for (int i = 0; i < MdctBits; i++)
                {
                    int s = 0, d1 = 0, d2 = count2;
                    for (int j = 0; j < count1; j++)
                    {
                        for (int k = 0; k < count2; k++)
                        {
                            float a = t1[s++];
                            float b = t1[s++];
                            t2[d1++] = a + b;
                            t2[d2++] = a - b;
                        }
                        d1 += count2;
                        d2 += count2;
                    }
                    var sw = t1; t1 = t2; t2 = sw;
                    count1 <<= 1;
                    count2 >>= 1;
                }
                // after 7 (odd) swaps the data lives in tmp
            }

            // DCT-IV twiddles
            {
                int count1 = half, count2 = 1;
                float[] t1 = tmp, t2 = spectra;
                for (int i = 0; i < MdctBits; i++)
                {
                    int tbl = i * 64;
                    int d1 = 0, d2 = count2 * 2 - 1, s1 = 0, s2 = count2;
                    for (int j = 0; j < count1; j++)
                    {
                        for (int k = 0; k < count2; k++)
                        {
                            float a = t1[s1++];
                            float b = t1[s2++];
                            float sin = SinTables[tbl];
                            float cos = CosTables[tbl];
                            tbl++;
                            t2[d1++] = a * sin - b * cos;
                            t2[d2--] = a * cos + b * sin;
                        }
                        s1 += count2;
                        s2 += count2;
                        d1 += count2;
                        d2 += count2 * 3;
                    }
                    var sw = t1; t1 = t2; t2 = sw;
                    count1 >>= 1;
                    count2 <<= 1;
                }
                // 7 swaps: result in spectra (t1 after loop)
            }

            float[] dct = spectra;
            float[] prev = ch.imdctPrevious;
            float[] wave = ch.wave[sf];
            for (int i = 0; i < half; i++)
            {
                wave[i] = ImdctWindow[i] * dct[i + half] + prev[i];
                wave[i + half] = ImdctWindow[i + half] * dct[size - 1 - i] - prev[i + half];
                prev[i] = ImdctWindow[size - 1 - i] * dct[half - i - 1];
                prev[i + half] = ImdctWindow[half - i - 1] * dct[i];
            }
        }
    }
}
