using System;
using System.Collections.Generic;
using CriCompat.Acb;
using CriCompat.Hca;

namespace CriCompat.Engine
{
    /// <summary>Loaded cue sheet (ACB + optional AWB) with a cache of waveform bytes.</summary>
    internal sealed class CueSheetData
    {
        public string Name;
        public AcbFile Acb;

        // Waveform bytes of all cue sheets share one budget, least recently used
        // first out; streamed BGM would otherwise stay in memory for good. Voices
        // keep their own reference, so eviction never cuts a playing sound.
        const long CacheBudget = 96L << 20;
        static readonly object cacheLock = new object();
        static readonly LinkedList<(CueSheetData sheet, int index)> lru = new LinkedList<(CueSheetData, int)>();
        static long cachedBytes;
        readonly Dictionary<int, (byte[] data, LinkedListNode<(CueSheetData, int)> node)> waveCache =
            new Dictionary<int, (byte[], LinkedListNode<(CueSheetData, int)>)>();

        public byte[] GetWaveBytes(int index)
        {
            lock (cacheLock)
            {
                if (waveCache.TryGetValue(index, out var hit))
                {
                    lru.Remove(hit.node);
                    lru.AddFirst(hit.node);
                    return hit.data;
                }
            }
            if (index < 0 || index >= Acb.Waveforms.Count) return null;
            byte[] data = Acb.ReadWaveformData(Acb.Waveforms[index]);
            if (data == null) return null;
            lock (cacheLock)
            {
                if (waveCache.TryGetValue(index, out var raced)) return raced.data;
                waveCache[index] = (data, lru.AddFirst((this, index)));
                cachedBytes += data.Length;
                while (cachedBytes > CacheBudget && lru.Count > 1)
                {
                    var (sheet, idx) = lru.Last.Value;
                    lru.RemoveLast();
                    if (sheet.waveCache.TryGetValue(idx, out var old))
                    {
                        sheet.waveCache.Remove(idx);
                        cachedBytes -= old.data.Length;
                    }
                }
            }
            return data;
        }

        public void Release()
        {
            lock (cacheLock)
            {
                foreach (var e in waveCache.Values)
                {
                    lru.Remove(e.node);
                    cachedBytes -= e.data.Length;
                }
                waveCache.Clear();
            }
            Acb.Close();
        }
    }

    /// <summary>Parameters set on a CriAtomExPlayer (snapshotted into playbacks).</summary>
    internal sealed class PlayerParams
    {
        public float Volume = 1f;
        public float PitchCents;
        public float PanAngle;
        public float ReleaseMs;
        public int Category = -1;
        public int FirstBlock;
        public readonly Dictionary<string, float> Aisac = new Dictionary<string, float>();

        public PlayerParams Clone()
        {
            var p = new PlayerParams
            {
                Volume = Volume, PitchCents = PitchCents, PanAngle = PanAngle, ReleaseMs = ReleaseMs,
                Category = Category, FirstBlock = FirstBlock
            };
            foreach (var kv in Aisac) p.Aisac[kv.Key] = kv.Value;
            return p;
        }

        public void Reset()
        {
            Volume = 1f;
            PitchCents = 0;
            PanAngle = 0;
            ReleaseMs = 0;
            Category = -1;
            FirstBlock = 0;
            Aisac.Clear();
        }
    }

    /// <summary>One waveform being rendered.</summary>
    internal sealed class Voice
    {
        public Playback Owner;
        public int WaveIndex;
        public byte[] Hca;
        HcaDecoder dec;
        public int Channels;
        public int SrcRate;
        public int SampleCount;
        public bool Loop;
        public int LoopStart;
        public int LoopEnd;
        public long StartAt;          // playback timeline (output samples)
        public long EndAt = long.MaxValue;
        public double Pos;            // source position in samples
        public float EventVolume = 1f;
        public float EventPitch;
        public List<Aisac> Aisacs;
        public float BaseVolume = 1f; // plan * track volume
        public float Gain;            // current smoothed gain
        public float TargetGain;
        public float PanL = 1f, PanR = 1f;
        public double Step = 1.0;
        public bool Ended;
        public int Block = -1;

        // two-frame decode window
        float[] frameA, frameB;
        int frameIndexA = -10, frameIndexB = -10;
        int delay;
        bool broken;

        public bool Init(byte[] hca)
        {
            Hca = hca;
            try
            {
                dec = new HcaDecoder(hca, 0, hca.Length);
            }
            catch (Exception)
            {
                broken = true;
                Ended = true;
                return false;
            }
            Channels = dec.Channels;
            SrcRate = dec.SampleRate;
            SampleCount = dec.SampleCount;
            delay = dec.EncoderDelay;
            frameA = new float[HcaDecoder.SamplesPerFrame * Channels];
            frameB = new float[HcaDecoder.SamplesPerFrame * Channels];
            if (dec.LoopFlag && Loop)
            {
                LoopStart = Math.Max(0, dec.LoopStartSample);
                LoopEnd = Math.Min(SampleCount, dec.LoopEndSample);
                if (LoopEnd <= LoopStart) Loop = false;
            }
            else if (Loop)
            {
                LoopStart = 0;
                LoopEnd = SampleCount;
            }
            return true;
        }

        void DecodeInto(int frame, float[] buf)
        {
            if (frame < 0 || frame >= dec.FrameCount)
            {
                Array.Clear(buf, 0, buf.Length);
                return;
            }
            dec.DecodeFrame(Hca, dec.HeaderSize + frame * dec.FrameSize, buf, 0);
        }

        float[] GetFrame(int frame)
        {
            if (frame == frameIndexA) return frameA;
            if (frame == frameIndexB) return frameB;
            if (frame == frameIndexB + 1 && frameIndexB >= 0)
            {
                var t = frameA;
                frameA = frameB;
                frameIndexA = frameIndexB;
                frameB = t;
                DecodeInto(frame, frameB);
                frameIndexB = frame;
                return frameB;
            }
            // seek: reset decoder and prime the overlap with the previous frame
            dec.Reset();
            DecodeInto(frame - 1, frameA);
            frameIndexA = frame - 1;
            DecodeInto(frame, frameB);
            frameIndexB = frame;
            return frameB;
        }

        /// <summary>Sample at a source index (handles loop wrap and end).</summary>
        public float Sample(long idx, int ch)
        {
            if (Loop && idx >= LoopEnd) idx = LoopStart + (idx - LoopStart) % Math.Max(1, LoopEnd - LoopStart);
            if (idx < 0 || idx >= SampleCount) return 0f;
            long s = idx + delay;
            int frame = (int)(s / HcaDecoder.SamplesPerFrame);
            int off = (int)(s % HcaDecoder.SamplesPerFrame);
            float[] f = GetFrame(frame);
            return f[off * Channels + (ch < Channels ? ch : 0)];
        }

        /// <summary>Mixes this voice into the output. `t0` is the playback time of out[0].</summary>
        public void Mix(float[] outBuf, int outChannels, int frames, long t0, float extraGain)
        {
            if (broken || Ended) return;
            int start = 0;
            if (StartAt > t0)
            {
                long d = StartAt - t0;
                if (d >= frames) return;
                start = (int)d;
            }
            int end = frames;
            if (EndAt < t0 + frames)
            {
                end = (int)Math.Max(0, EndAt - t0);
                if (end <= start)
                {
                    Ended = true;
                    return;
                }
            }
            float g = Gain;
            float target = TargetGain * extraGain;
            float gStep = (target - g) / 256f;
            for (int i = start; i < end; i++)
            {
                if (Math.Abs(target - g) > 1e-5f) g += gStep; else g = target;
                long i0 = (long)Pos;
                float frac = (float)(Pos - i0);
                if (!Loop && i0 >= SampleCount)
                {
                    Ended = true;
                    break;
                }
                float l0 = Sample(i0, 0), l1 = Sample(i0 + 1, 0);
                float l = l0 + (l1 - l0) * frac;
                float r;
                if (Channels > 1)
                {
                    float r0 = Sample(i0, 1), r1 = Sample(i0 + 1, 1);
                    r = r0 + (r1 - r0) * frac;
                }
                else r = l;
                int o = i * outChannels;
                if (outChannels >= 2)
                {
                    outBuf[o] += l * g * PanL;
                    outBuf[o + 1] += r * g * PanR;
                }
                else outBuf[o] += (l + r) * 0.5f * g;
                Pos += Step;
                if (Loop && Pos >= LoopEnd) Pos -= (LoopEnd - LoopStart);
            }
            Gain = g;
            if (end < frames && EndAt <= t0 + frames) Ended = true;
        }
    }

    internal enum PlaybackState { Prep, Playing, Removed }

    internal sealed class Playback
    {
        public uint Id;
        public PlayerCore Player;
        public CueSheetData Sheet;
        public AcbCue Cue;
        public PlayerParams Params;
        public PlaybackState State = PlaybackState.Prep;
        public bool WaitingForResume;    // created by Prepare()
        public bool Paused;
        public bool Stopping;
        public float StopGain = 1f;
        public float StopStep;
        public long Time;
        public readonly List<Voice> Voices = new List<Voice>();
        public float RandomPitch;

        // block sequence state
        public int CurBlock = -1;
        public int BlockSerial;          // increments on every block transition
        public long CurBlockStart;
        public long CurBlockEnd = long.MaxValue;
        public int RequestedNext = -1;
        public int PreparedNext = -1;
        public int PreparedFor = -1;     // BlockSerial the preparation belongs to
        public bool BlockEnded;

        public int LastBeat = -1;
        public bool Started => State != PlaybackState.Prep;
    }

    /// <summary>Engine-side state of one CriAtomExPlayer.</summary>
    internal sealed class PlayerCore
    {
        public readonly PlayerParams Params = new PlayerParams();
        public CueSheetData Sheet;
        public AcbCue Cue;
        public bool Paused;
        public bool EverStarted;
        public readonly List<Playback> Playbacks = new List<Playback>();
        public Playback Latest;
        // PCM capture for CriAtomExOutputAnalyzer
        public float[][] Capture;
        public int CaptureLength;
        public bool Disposed;
    }

    /// <summary>
    /// The software mixer.  All public methods take the engine lock; Mix() is
    /// called from Unity's audio thread.
    /// </summary>
    internal sealed class AtomEngine
    {
        public static readonly AtomEngine Instance = new AtomEngine();

        public readonly object Lock = new object();
        public int OutputRate = 48000;
        public float MasterVolume = 1f;
        readonly List<PlayerCore> players = new List<PlayerCore>();
        readonly Dictionary<uint, Playback> playbacks = new Dictionary<uint, Playback>();
        uint nextId = 1;
        readonly Random rng = new Random();
        readonly List<(CueSheetData, AcbCue, int)> prefetchList = new List<(CueSheetData, AcbCue, int)>();
        float[] tmpL = new float[0];
        float[] mixScratch = new float[0];

        public PlayerCore CreatePlayer()
        {
            var p = new PlayerCore();
            lock (Lock) players.Add(p);
            return p;
        }

        public void DestroyPlayer(PlayerCore p)
        {
            lock (Lock)
            {
                foreach (var pb in p.Playbacks)
                {
                    pb.State = PlaybackState.Removed;
                    playbacks.Remove(pb.Id);
                }
                p.Playbacks.Clear();
                p.Disposed = true;
                players.Remove(p);
            }
        }

        /// <summary>Snapshot of all live playbacks (caller should hold Lock).</summary>
        public List<Playback> AllPlaybacks()
        {
            lock (Lock) return new List<Playback>(playbacks.Values);
        }

        public Playback Find(uint id)
        {
            lock (Lock)
            {
                return playbacks.TryGetValue(id, out Playback p) ? p : null;
            }
        }

        // ------------------------------------------------------------------
        // parameter evaluation
        static float AisacValue(PlayerParams prm, Aisac a)
        {
            if (prm.Aisac.TryGetValue(a.ControlName, out float v)) return v;
            return a.HasDefault ? a.DefaultValue : 0f;
        }

        static void EvaluateAisacs(PlayerParams prm, List<Aisac> list, ref float vol, ref float pitch, ref float pan)
        {
            if (list == null) return;
            foreach (var a in list)
            {
                float x = AisacValue(prm, a);
                foreach (var g in a.Graphs)
                {
                    switch (g.Type)
                    {
                        case 1:
                        case 54:
                            vol *= Math.Max(0f, g.Evaluate(x));
                            break;
                        case 2:
                            pitch += g.Evaluate(x);
                            break;
                        case 15:
                            pan += g.Evaluate(x);
                            break;
                    }
                }
            }
        }

        void ApplyParams(Voice v)
        {
            var pb = v.Owner;
            var prm = pb.Params;
            float vol = prm.Volume * v.BaseVolume * v.EventVolume;
            float pitch = prm.PitchCents + v.EventPitch;
            float pan = prm.PanAngle;
            EvaluateAisacs(prm, v.Aisacs, ref vol, ref pitch, ref pan);
            v.TargetGain = Math.Max(0f, vol);
            v.Step = v.SrcRate / (double)OutputRate * Math.Pow(2.0, pitch / 1200.0);
            float p = Math.Max(-1f, Math.Min(1f, pan / 90f));
            if (v.Channels == 1)
            {
                double ang = (p + 1) * Math.PI / 4; // equal power, unity at centre
                v.PanL = (float)Math.Min(1.0, Math.Cos(ang) * Math.Sqrt(2));
                v.PanR = (float)Math.Min(1.0, Math.Sin(ang) * Math.Sqrt(2));
            }
            else
            {
                v.PanL = p > 0 ? 1f - p : 1f;
                v.PanR = p < 0 ? 1f + p : 1f;
            }
        }

        Voice MakeVoice(Playback pb, SoundEvent ev, long startAt, List<Aisac> aisacs, float baseVolume, int block)
        {
            byte[] bytes = pb.Sheet.GetWaveBytes(ev.Waveform);
            if (bytes == null) return null;
            var w = pb.Sheet.Acb.Waveforms[ev.Waveform];
            var v = new Voice
            {
                Owner = pb,
                WaveIndex = ev.Waveform,
                Loop = w.LoopFlag && !pb.Cue.Plan.IsBlockSequence,
                StartAt = startAt,
                EventVolume = ev.Volume,
                EventPitch = ev.PitchCents + (ev.PitchRandomCents > 0
                    ? (float)((rng.NextDouble() - 0.5) * ev.PitchRandomCents) : 0f),
                Aisacs = aisacs,
                BaseVolume = baseVolume,
                Block = block,
            };
            if (!v.Init(bytes)) return null;
            ApplyParams(v);
            v.Gain = v.TargetGain;
            return v;
        }

        long MsToSamples(double ms) => (long)Math.Round(ms * OutputRate / 1000.0);

        int PickTrack(CuePlan plan)
        {
            int n = plan.Tracks.Count;
            if (n == 0) return -1;
            int total = 0;
            for (int i = 0; i < n; i++) total += Math.Max(0, i < plan.TrackWeights.Length ? plan.TrackWeights[i] : 100);
            if (total <= 0) return rng.Next(n);
            int r = rng.Next(total);
            for (int i = 0; i < n; i++)
            {
                r -= Math.Max(0, i < plan.TrackWeights.Length ? plan.TrackWeights[i] : 100);
                if (r < 0) return i;
            }
            return n - 1;
        }

        void SpawnBlock(Playback pb, int blockIdx, long startAt)
        {
            var plan = pb.Cue.Plan;
            if (blockIdx < 0 || blockIdx >= plan.Blocks.Count) return;
            var block = plan.Blocks[blockIdx];
            for (int ti = 0; ti < block.Tracks.Count; ti++)
            {
                var track = block.Tracks[ti];
                var aisacs = new List<Aisac>(track.Aisacs);
                if (ti < plan.LayerAisacs.Count) aisacs.AddRange(plan.LayerAisacs[ti]);
                foreach (var ev in track.Events)
                {
                    var v = MakeVoice(pb, ev, startAt + MsToSamples(ev.TimeMs), aisacs, plan.Volume * track.Volume, blockIdx);
                    if (v != null) pb.Voices.Add(v);
                }
            }
        }

        int NaturalNextBlock(Playback pb)
        {
            var plan = pb.Cue.Plan;
            if (pb.RequestedNext >= 0 && pb.RequestedNext < plan.Blocks.Count) return pb.RequestedNext;
            var b = plan.Blocks[pb.CurBlock];
            switch (b.PlaybackType)
            {
                case 1: return pb.CurBlock;
                case 2: return b.Destinations.Length > 0 ? b.Destinations[0] : pb.CurBlock + 1;
                default: return pb.CurBlock + 1 < plan.Blocks.Count ? pb.CurBlock + 1 : -1;
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Loads waveform bytes needed by a cue (outside the engine lock).</summary>
        static void Prefetch(CueSheetData sheet, AcbCue cue, int block)
        {
            if (sheet == null || cue == null || cue.Plan == null) return;
            var plan = cue.Plan;
            if (plan.IsBlockSequence)
            {
                if (block < 0 || block >= plan.Blocks.Count) return;
                foreach (var t in plan.Blocks[block].Tracks)
                    foreach (var ev in t.Events) sheet.GetWaveBytes(ev.Waveform);
                return;
            }
            foreach (var t in plan.Tracks)
                foreach (var ev in t.Events) sheet.GetWaveBytes(ev.Waveform);
        }

        public Playback Start(PlayerCore player, bool prepareOnly)
        {
            Prefetch(player.Sheet, player.Cue, Math.Max(0, player.Params.FirstBlock));
            lock (Lock)
            {
                if (player.Sheet == null || player.Cue == null) return null;
                var pb = new Playback
                {
                    Id = nextId++,
                    Player = player,
                    Sheet = player.Sheet,
                    Cue = player.Cue,
                    Params = player.Params.Clone(),
                    Paused = player.Paused,
                    WaitingForResume = prepareOnly,
                };
                var plan = pb.Cue.Plan;
                if (plan.IsBlockSequence)
                {
                    int first = Math.Max(0, Math.Min(plan.Blocks.Count - 1, pb.Params.FirstBlock));
                    pb.CurBlock = first;
                    pb.CurBlockStart = 0;
                    pb.CurBlockEnd = MsToSamples(plan.Blocks[first].LengthMs);
                    SpawnBlock(pb, first, 0);
                }
                else if (plan.SelectType == 0)
                {
                    foreach (var t in plan.Tracks)
                        foreach (var ev in t.Events)
                        {
                            var v = MakeVoice(pb, ev, MsToSamples(ev.TimeMs), t.Aisacs, plan.Volume * t.Volume, -1);
                            if (v != null) pb.Voices.Add(v);
                        }
                }
                else
                {
                    int ti = PickTrack(plan);
                    if (ti >= 0)
                    {
                        var t = plan.Tracks[ti];
                        foreach (var ev in t.Events)
                        {
                            var v = MakeVoice(pb, ev, MsToSamples(ev.TimeMs), t.Aisacs, plan.Volume * t.Volume, -1);
                            if (v != null) pb.Voices.Add(v);
                        }
                    }
                }
                pb.State = prepareOnly ? PlaybackState.Prep : PlaybackState.Playing;
                player.Playbacks.Add(pb);
                player.Latest = pb;
                player.EverStarted = true;
                playbacks[pb.Id] = pb;
                return pb;
            }
        }

        public void UpdateParams(PlayerCore player)
        {
            lock (Lock)
            {
                foreach (var pb in player.Playbacks)
                {
                    pb.Params = player.Params.Clone();
                    foreach (var v in pb.Voices) ApplyParams(v);
                }
            }
        }

        public void StopPlayer(PlayerCore player, bool immediate)
        {
            lock (Lock)
            {
                foreach (var pb in player.Playbacks) StopPlayback(pb, immediate, player.Params.ReleaseMs);
            }
        }

        public void StopPlayback(Playback pb, bool immediate, float releaseMs)
        {
            lock (Lock)
            {
                if (pb.State == PlaybackState.Removed) return;
                if (immediate || releaseMs <= 0 || !pb.Started || pb.Paused)
                {
                    pb.State = PlaybackState.Removed;
                    return;
                }
                pb.Stopping = true;
                pb.StopStep = 1f / Math.Max(1f, releaseMs * OutputRate / 1000f);
            }
        }

        public void SetPaused(PlayerCore player, bool paused)
        {
            lock (Lock)
            {
                player.Paused = paused;
                foreach (var pb in player.Playbacks) pb.Paused = paused;
            }
        }

        public void Resume(PlayerCore player, int mode)
        {
            // 0 all, 1 paused playbacks, 2 prepared playbacks
            lock (Lock)
            {
                if (mode == 0 || mode == 1)
                {
                    player.Paused = false;
                    foreach (var pb in player.Playbacks) pb.Paused = false;
                }
                if (mode == 0 || mode == 2)
                {
                    foreach (var pb in player.Playbacks)
                        if (pb.WaitingForResume)
                        {
                            pb.WaitingForResume = false;
                            pb.State = PlaybackState.Playing;
                        }
                }
            }
        }

        public void ResumePlayback(Playback pb, int mode)
        {
            lock (Lock)
            {
                if (pb.State == PlaybackState.Removed) return;
                if ((mode == 0 || mode == 1) && pb.Paused) pb.Paused = false;
                if ((mode == 0 || mode == 2) && pb.WaitingForResume)
                {
                    pb.WaitingForResume = false;
                    pb.State = PlaybackState.Playing;
                }
            }
        }

        // ------------------------------------------------------------------
        /// <summary>Main-thread housekeeping: block preparation and cleanup.</summary>
        public void MainThreadUpdate(Action<Playback, int, int> onBeat)
        {
            // prefetch the waveforms of upcoming blocks without holding the lock
            prefetchList.Clear();
            lock (Lock)
            {
                foreach (var player in players)
                    foreach (var pb in player.Playbacks)
                        if (pb.Started && pb.Cue.Plan.IsBlockSequence && !pb.BlockEnded && pb.CurBlock >= 0)
                            prefetchList.Add((pb.Sheet, pb.Cue, NaturalNextBlock(pb)));
            }
            foreach (var (sheet, cue, block) in prefetchList) Prefetch(sheet, cue, block);
            lock (Lock)
            {
                for (int pi = players.Count - 1; pi >= 0; pi--)
                {
                    var player = players[pi];
                    for (int i = player.Playbacks.Count - 1; i >= 0; i--)
                    {
                        var pb = player.Playbacks[i];
                        if (pb.State == PlaybackState.Removed)
                        {
                            player.Playbacks.RemoveAt(i);
                            playbacks.Remove(pb.Id);
                            continue;
                        }
                        var plan = pb.Cue.Plan;
                        if (plan.IsBlockSequence && pb.Started && !pb.BlockEnded) PrepareNextBlock(pb);
                        if (plan.Bpm > 0 && pb.State == PlaybackState.Playing && !pb.Paused && onBeat != null)
                        {
                            double beatLen = 60.0 / plan.Bpm * OutputRate;
                            int beat = (int)(pb.Time / beatLen);
                            if (beat != pb.LastBeat)
                            {
                                pb.LastBeat = beat;
                                onBeat(pb, beat, plan.BeatsPerBar);
                            }
                        }
                    }
                }
            }
        }

        void PrepareNextBlock(Playback pb)
        {
            int next = NaturalNextBlock(pb);
            if (pb.PreparedFor == pb.BlockSerial && next == pb.PreparedNext) return;
            // The audio thread only switches to a block prepared here. If this runs
            // late (a long frame, a scene load) the boundary has already passed:
            // switch now instead of leaving the playback without a next block.
            if (pb.Time >= pb.CurBlockEnd) pb.CurBlockEnd = pb.Time;
            // remove previously prepared voices for the next block
            if (pb.PreparedFor == pb.BlockSerial && pb.PreparedNext >= 0)
            {
                long end = pb.CurBlockEnd;
                pb.Voices.RemoveAll(v => v.StartAt >= end);
            }
            foreach (var v in pb.Voices)
                if (v.Block == pb.CurBlock && v.StartAt < pb.CurBlockEnd)
                    v.EndAt = next >= 0 ? pb.CurBlockEnd : long.MaxValue;
            pb.PreparedNext = next;
            pb.PreparedFor = pb.BlockSerial;
            if (next >= 0) SpawnBlock(pb, next, pb.CurBlockEnd);
        }

        // ------------------------------------------------------------------
        /// <summary>Audio thread: render all playbacks into `data` (interleaved).</summary>
        public void Mix(float[] data, int channels)
        {
            int frames = data.Length / channels;
            lock (Lock)
            {
                for (int pi = 0; pi < players.Count; pi++)
                {
                    var player = players[pi];
                    bool capture = player.Capture != null;
                    float[] target = data;
                    if (capture)
                    {
                        if (mixScratch.Length < data.Length) mixScratch = new float[data.Length];
                        Array.Clear(mixScratch, 0, data.Length);
                        target = mixScratch;
                    }
                    for (int bi = 0; bi < player.Playbacks.Count; bi++)
                    {
                        var pb = player.Playbacks[bi];
                        if (pb.State != PlaybackState.Playing || pb.Paused) continue;
                        RenderPlayback(pb, target, channels, frames);
                    }
                    if (capture)
                    {
                        for (int i = 0; i < data.Length; i++) data[i] += mixScratch[i];
                        StoreCapture(player, mixScratch, channels, frames);
                    }
                }
                if (MasterVolume != 1f)
                    for (int i = 0; i < data.Length; i++) data[i] *= MasterVolume;
            }
        }

        static void StoreCapture(PlayerCore player, float[] buf, int channels, int frames)
        {
            int n = player.CaptureLength;
            for (int ch = 0; ch < player.Capture.Length; ch++)
            {
                var dst = player.Capture[ch];
                int c = Math.Min(ch, channels - 1);
                for (int i = 0; i < n; i++)
                {
                    int src = frames - n + i;
                    dst[i] = src >= 0 ? buf[src * channels + c] : 0f;
                }
            }
        }

        void RenderPlayback(Playback pb, float[] data, int channels, int frames)
        {
            long t0 = pb.Time;
            float stopGain = pb.StopGain;
            if (pb.Stopping)
            {
                // apply fade per block of samples (coarse but click free with voice smoothing)
                stopGain = Math.Max(0f, pb.StopGain - pb.StopStep * frames);
            }
            // block boundary inside this buffer?
            var plan = pb.Cue.Plan;
            if (plan.IsBlockSequence && t0 + frames > pb.CurBlockEnd && !pb.BlockEnded)
            {
                // voices for the next block were scheduled by the main thread
                if (pb.PreparedFor == pb.BlockSerial && pb.PreparedNext >= 0)
                {
                    int nb = pb.PreparedNext;
                    pb.CurBlockStart = pb.CurBlockEnd;
                    pb.CurBlock = nb;
                    pb.BlockSerial++;
                    pb.CurBlockEnd = pb.CurBlockStart + MsToSamples(plan.Blocks[nb].LengthMs);
                    pb.RequestedNext = -1;
                    pb.PreparedNext = -1;
                }
                else if (pb.PreparedFor == pb.BlockSerial && pb.PreparedNext < 0)
                {
                    pb.BlockEnded = true;
                }
            }
            for (int i = 0; i < pb.Voices.Count; i++)
                pb.Voices[i].Mix(data, channels, frames, t0, stopGain);
            pb.Time = t0 + frames;
            pb.StopGain = stopGain;
            pb.Voices.RemoveAll(v => v.Ended);
            bool pendingBlocks = plan.IsBlockSequence && !pb.BlockEnded;
            if ((pb.Voices.Count == 0 && !pendingBlocks) || (pb.Stopping && stopGain <= 0f))
                pb.State = PlaybackState.Removed;
        }
    }
}
