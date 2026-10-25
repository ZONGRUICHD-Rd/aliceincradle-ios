using System;
using System.Collections.Generic;

namespace CriCompat.Acb
{
    public sealed class AcbWaveform
    {
        public int Index;
        public bool Streaming;
        public int AwbId;
        public int Channels;
        public int SampleRate;
        public int NumSamples;
        public bool LoopFlag;
        public int EncodeType;
    }

    /// <summary>One AISAC graph mapping a control value to a parameter.</summary>
    public sealed class AisacGraph
    {
        public int Type;      // 1 volume, 2 pitch, 15 pan angle (others ignored)
        public float[] X;
        public float[] Y;

        public float Evaluate(float x)
        {
            if (X == null || X.Length == 0) return 1f;
            if (x <= X[0]) return Y[0];
            for (int i = 1; i < X.Length; i++)
            {
                if (x <= X[i])
                {
                    float t = (x - X[i - 1]) / Math.Max(1e-6f, X[i] - X[i - 1]);
                    return Y[i - 1] + (Y[i] - Y[i - 1]) * t;
                }
            }
            return Y[Y.Length - 1];
        }
    }

    public sealed class Aisac
    {
        public string ControlName;
        public int ControlId;
        public float DefaultValue;
        public bool HasDefault;
        public readonly List<AisacGraph> Graphs = new List<AisacGraph>();
    }

    /// <summary>A waveform scheduled inside a track.</summary>
    public struct SoundEvent
    {
        public double TimeMs;
        public int Waveform;
        public float Volume;
        public float PitchCents;
        public float PitchRandomCents;
    }

    public sealed class TrackPlan
    {
        public readonly List<SoundEvent> Events = new List<SoundEvent>();
        public readonly List<Aisac> Aisacs = new List<Aisac>();
        public float Volume = 1f;
    }

    public sealed class BlockPlan
    {
        public readonly List<TrackPlan> Tracks = new List<TrackPlan>();
        public double LengthMs;
        public int PlaybackType;     // 0 next, 1 loop, 2 jump to destination
        public int[] Destinations = Array.Empty<int>();
    }

    public sealed class CuePlan
    {
        public int SelectType;          // sequence type (0 = all tracks)
        public int[] TrackWeights = Array.Empty<int>();
        public readonly List<TrackPlan> Tracks = new List<TrackPlan>();
        public readonly List<BlockPlan> Blocks = new List<BlockPlan>();
        public readonly List<List<Aisac>> LayerAisacs = new List<List<Aisac>>();
        public float Volume = 1f;
        public float Bpm;
        public int BeatsPerBar = 4;
        public bool IsBlockSequence => Blocks.Count > 0;
    }

    public sealed class AcbCue
    {
        public int Id;
        public string Name;
        public long LengthMs;
        public int ReferenceType;
        public int ReferenceIndex;
        public ushort NumBlocks;
        public ushort[] Categories = Array.Empty<ushort>();
        public CuePlan Plan;
    }

    /// <summary>Parsed ACB cue sheet with the subset of features the game uses.</summary>
    public sealed class AcbFile
    {
        public string Name = "";
        public readonly List<AcbCue> Cues = new List<AcbCue>();
        public readonly Dictionary<string, AcbCue> CuesByName = new Dictionary<string, AcbCue>();
        public readonly Dictionary<int, AcbCue> CuesById = new Dictionary<int, AcbCue>();
        public readonly List<AcbWaveform> Waveforms = new List<AcbWaveform>();
        public Afs2Archive MemoryAwb;
        public Afs2Archive StreamAwb;
        public readonly List<string> Warnings = new List<string>();

        UtfTable cueTable, synthTable, seqTable, trackTable, trackEventTable, seqCommandTable,
            synthCommandTable, trackCommandTable, aisacTable, graphTable, aisacNameTable,
            blockSeqTable, blockTable, beatSyncTable;
        readonly Dictionary<int, string> aisacNames = new Dictionary<int, string>();

        public static AcbFile Load(byte[] acbData, string awbPath)
        {
            var acb = new AcbFile();
            acb.Parse(acbData);
            if (!string.IsNullOrEmpty(awbPath)) acb.StreamAwb = Afs2Archive.FromFile(awbPath);
            return acb;
        }

        void Parse(byte[] data)
        {
            var header = UtfTable.Parse(data, 0);
            Name = header.GetString(0, "Name");
            cueTable = header.GetTable(0, "CueTable");
            var cueNameTable = header.GetTable(0, "CueNameTable");
            var waveformTable = header.GetTable(0, "WaveformTable");
            synthTable = header.GetTable(0, "SynthTable");
            seqTable = header.GetTable(0, "SequenceTable");
            trackTable = header.GetTable(0, "TrackTable");
            trackEventTable = header.GetTable(0, "TrackEventTable") ?? header.GetTable(0, "CommandTable");
            seqCommandTable = header.GetTable(0, "SeqCommandTable");
            synthCommandTable = header.GetTable(0, "SynthCommandTable");
            trackCommandTable = header.GetTable(0, "TrackCommandTable");
            aisacTable = header.GetTable(0, "AisacTable");
            graphTable = header.GetTable(0, "GraphTable");
            aisacNameTable = header.GetTable(0, "AisacControlNameTable");
            blockSeqTable = header.GetTable(0, "BlockSequenceTable");
            blockTable = header.GetTable(0, "BlockTable");
            beatSyncTable = header.GetTable(0, "BeatSyncInfoTable");
            MemoryAwb = Afs2Archive.FromMemory(header.GetData(0, "AwbFile"));

            if (aisacNameTable != null)
                for (int i = 0; i < aisacNameTable.RowCount; i++)
                    aisacNames[(int)aisacNameTable.GetInt(i, "AisacControlId")] = aisacNameTable.GetString(i, "AisacControlName");

            if (waveformTable != null)
            {
                for (int i = 0; i < waveformTable.RowCount; i++)
                {
                    var w = new AcbWaveform
                    {
                        Index = i,
                        EncodeType = (int)waveformTable.GetInt(i, "EncodeType"),
                        Streaming = waveformTable.GetInt(i, "Streaming") != 0,
                        Channels = (int)waveformTable.GetInt(i, "NumChannels"),
                        SampleRate = (int)waveformTable.GetInt(i, "SamplingRate"),
                        NumSamples = (int)waveformTable.GetInt(i, "NumSamples"),
                        LoopFlag = waveformTable.GetInt(i, "LoopFlag") != 0,
                    };
                    if (waveformTable.Has("MemoryAwbId"))
                        w.AwbId = (int)(w.Streaming ? waveformTable.GetInt(i, "StreamAwbId") : waveformTable.GetInt(i, "MemoryAwbId"));
                    else
                        w.AwbId = (int)waveformTable.GetInt(i, "Id");
                    Waveforms.Add(w);
                }
            }

            if (cueTable == null) return;
            var nameByIndex = new Dictionary<int, string>();
            if (cueNameTable != null)
                for (int i = 0; i < cueNameTable.RowCount; i++)
                    nameByIndex[(int)cueNameTable.GetInt(i, "CueIndex")] = cueNameTable.GetString(i, "CueName");

            for (int i = 0; i < cueTable.RowCount; i++)
            {
                var cue = new AcbCue
                {
                    Id = (int)cueTable.GetInt(i, "CueId"),
                    ReferenceType = (int)cueTable.GetInt(i, "ReferenceType"),
                    ReferenceIndex = (int)cueTable.GetInt(i, "ReferenceIndex"),
                };
                long len = cueTable.GetInt(i, "Length");
                cue.LengthMs = (len == 0xFFFFFFFF || len < 0) ? -1 : len;
                cue.Name = nameByIndex.TryGetValue(i, out string n) ? n : ("cue" + cue.Id);
                try
                {
                    cue.Plan = BuildPlan(cue);
                }
                catch (Exception e)
                {
                    Warnings.Add("cue " + cue.Name + ": " + e.Message);
                    cue.Plan = new CuePlan();
                }
                cue.NumBlocks = (ushort)cue.Plan.Blocks.Count;
                Cues.Add(cue);
                CuesById[cue.Id] = cue;
                if (!CuesByName.ContainsKey(cue.Name)) CuesByName[cue.Name] = cue;
            }
        }

        // ------------------------------------------------------------------
        struct Commands
        {
            public float Volume;
            public float Pitch;
            public float PitchRandom;
            public int Category;
        }

        static IEnumerable<(int code, ByteSlice data)> ParseCommands(ByteSlice s)
        {
            int p = 0;
            while (p + 3 <= s.Count)
            {
                int code = s.U16BE(p);
                int size = s[p + 2];
                p += 3;
                if (p + size > s.Count) yield break;
                yield return (code, new ByteSlice(s.Array, s.Offset + p, size));
                p += size;
            }
        }

        static Commands ReadParameterCommands(UtfTable table, long index)
        {
            var c = new Commands { Volume = 1f, Category = -1 };
            if (table == null || index < 0 || index >= table.RowCount || index == 0xFFFF) return c;
            foreach (var (code, d) in ParseCommands(table.GetData((int)index, "Command")))
            {
                switch (code)
                {
                    case 0x0092:
                        if (d.Count >= 4) c.Volume *= d.F32BE(0);
                        break;
                    case 0x0005:
                        if (d.Count >= 2) c.Pitch += (short)d.U16BE(0);
                        break;
                    case 0x0079:
                        if (d.Count >= 2) c.PitchRandom = Math.Abs((short)d.U16BE(0));
                        break;
                    case 0x0041:
                        if (d.Count >= 4) c.Category = (int)d.U32BE(0);
                        break;
                }
            }
            return c;
        }

        List<Aisac> ReadAisacs(ByteSlice localAisacs)
        {
            var list = new List<Aisac>();
            if (aisacTable == null || localAisacs.IsEmpty) return list;
            foreach (ushort ai in localAisacs.U16Array())
            {
                if (ai >= aisacTable.RowCount) continue;
                int controlId = (int)aisacTable.GetInt(ai, "ControlId");
                var a = new Aisac
                {
                    ControlId = controlId,
                    ControlName = aisacNames.TryGetValue(controlId, out string nm) ? nm : ("aisac" + controlId),
                    HasDefault = aisacTable.GetInt(ai, "DefaultControlFlag") != 0,
                    DefaultValue = aisacTable.GetFloat(ai, "DefaultControl"),
                };
                if (graphTable != null)
                {
                    foreach (ushort gi in aisacTable.GetData(ai, "GraphIndexes").U16Array())
                    {
                        if (gi >= graphTable.RowCount) continue;
                        a.Graphs.Add(new AisacGraph
                        {
                            Type = (int)graphTable.GetInt(gi, "Type"),
                            X = graphTable.GetData(gi, "Controls").F32Array(),
                            Y = graphTable.GetData(gi, "Destinations").F32Array(),
                        });
                    }
                }
                list.Add(a);
            }
            return list;
        }

        CuePlan BuildPlan(AcbCue cue)
        {
            var plan = new CuePlan();
            var cats = new List<ushort>();
            switch (cue.ReferenceType)
            {
                case 1: // waveform
                {
                    var t = new TrackPlan();
                    t.Events.Add(new SoundEvent { Waveform = cue.ReferenceIndex, Volume = 1f });
                    plan.Tracks.Add(t);
                    break;
                }
                case 2: // synth
                {
                    var t = new TrackPlan();
                    AddSynth(t, cue.ReferenceIndex, 0, 1f, 0, 0, 0);
                    plan.Tracks.Add(t);
                    break;
                }
                case 3: // sequence
                    BuildSequence(plan, cue.ReferenceIndex, cats);
                    break;
                case 8: // block sequence
                    BuildBlockSequence(plan, cue.ReferenceIndex, cats);
                    break;
                default:
                    Warnings.Add("cue " + cue.Name + ": unsupported reference type " + cue.ReferenceType);
                    break;
            }
            var arr = new ushort[16];
            for (int i = 0; i < 16; i++) arr[i] = i < cats.Count ? cats[i] : (ushort)0xFFFF;
            cue.Categories = arr;
            return plan;
        }

        void BuildSequence(CuePlan plan, int seqIndex, List<ushort> cats)
        {
            if (seqTable == null || seqIndex >= seqTable.RowCount) return;
            var cmd = ReadParameterCommands(seqCommandTable, seqTable.GetInt(seqIndex, "CommandIndex", 0xFFFF));
            plan.Volume *= cmd.Volume;
            if (cmd.Category >= 0) cats.Add((ushort)cmd.Category);
            plan.SelectType = (int)seqTable.GetInt(seqIndex, "Type");
            var tracks = seqTable.GetData(seqIndex, "TrackIndex").U16Array();
            var weights = seqTable.GetData(seqIndex, "TrackValues").U16Array();
            plan.TrackWeights = new int[tracks.Length];
            var seqAisacs = ReadAisacs(seqTable.GetData(seqIndex, "LocalAisacs"));
            for (int i = 0; i < tracks.Length; i++)
            {
                plan.TrackWeights[i] = i < weights.Length ? weights[i] : 100;
                var t = BuildTrack(tracks[i], cmd.Pitch, cmd.PitchRandom, 0);
                t.Aisacs.AddRange(seqAisacs);
                plan.Tracks.Add(t);
            }
        }

        void BuildBlockSequence(CuePlan plan, int index, List<ushort> cats)
        {
            if (blockSeqTable == null || blockTable == null || index >= blockSeqTable.RowCount) return;
            var cmd = ReadParameterCommands(seqCommandTable, blockSeqTable.GetInt(index, "CommandIndex", 0xFFFF));
            plan.Volume *= cmd.Volume;
            if (cmd.Category >= 0) cats.Add((ushort)cmd.Category);
            var layerTracks = blockSeqTable.GetData(index, "TrackIndex").U16Array();
            var seqAisacs = ReadAisacs(blockSeqTable.GetData(index, "LocalAisacs"));
            foreach (ushort lt in layerTracks)
            {
                var a = trackTable != null && lt < trackTable.RowCount
                    ? ReadAisacs(trackTable.GetData(lt, "LocalAisacs"))
                    : new List<Aisac>();
                a.AddRange(seqAisacs);
                plan.LayerAisacs.Add(a);
            }
            var blocks = blockSeqTable.GetData(index, "BlockIndex").U16Array();
            int first = blocks.Length > 0 ? blocks[0] : 0;
            foreach (ushort bi in blocks)
            {
                if (bi >= blockTable.RowCount) continue;
                var b = new BlockPlan
                {
                    LengthMs = blockTable.GetInt(bi, "Length") + blockTable.GetInt(bi, "LengthUs") / 1000.0,
                    PlaybackType = (int)blockTable.GetInt(bi, "PlaybackType"),
                };
                var dests = blockTable.GetData(bi, "DestinationBlocks").U16Array();
                b.Destinations = new int[dests.Length];
                for (int i = 0; i < dests.Length; i++) b.Destinations[i] = dests[i];
                foreach (ushort ti in blockTable.GetData(bi, "TrackIndex").U16Array())
                    b.Tracks.Add(BuildTrack(ti, cmd.Pitch, cmd.PitchRandom, 0));
                plan.Blocks.Add(b);
            }
            if (beatSyncTable != null && beatSyncTable.RowCount > 0)
            {
                plan.Bpm = beatSyncTable.GetFloat(0, "Bpm");
                plan.BeatsPerBar = (int)Math.Max(1, beatSyncTable.GetInt(0, "NumBeats", 4));
            }
        }

        TrackPlan BuildTrack(int trackIndex, float pitch, float pitchRandom, int depth)
        {
            var t = new TrackPlan();
            if (trackTable == null || trackIndex >= trackTable.RowCount) return t;
            t.Aisacs.AddRange(ReadAisacs(trackTable.GetData(trackIndex, "LocalAisacs")));
            var tcmd = ReadParameterCommands(trackCommandTable, trackTable.GetInt(trackIndex, "CommandIndex", 0xFFFF));
            t.Volume *= tcmd.Volume;
            long ev = trackTable.GetInt(trackIndex, "EventIndex", 0xFFFF);
            if (trackEventTable == null || ev == 0xFFFF || ev >= trackEventTable.RowCount) return t;
            double time = 0;
            foreach (var (code, d) in ParseCommands(trackEventTable.GetData((int)ev, "Command")))
            {
                switch (code)
                {
                    case 2000: // noteOn
                    case 2003: // noteOnWithNo
                        if (d.Count >= 4)
                        {
                            int type = d.U16BE(0);
                            int idx = d.U16BE(2);
                            AddReference(t, type, idx, time, 1f, pitch + tcmd.Pitch,
                                Math.Max(pitchRandom, tcmd.PitchRandom), depth);
                        }
                        break;
                    case 2001: // wait (ms)
                        if (d.Count >= 4) time += d.U32BE(0);
                        break;
                    case 2005: // wait remainder (us)
                        if (d.Count >= 2) time += d.U16BE(0) / 1000.0;
                        break;
                }
            }
            return t;
        }

        void AddReference(TrackPlan t, int type, int idx, double time, float vol, float pitch, float pitchRandom, int depth)
        {
            if (depth > 6) return;
            switch (type)
            {
                case 1:
                    t.Events.Add(new SoundEvent { TimeMs = time, Waveform = idx, Volume = vol, PitchCents = pitch, PitchRandomCents = pitchRandom });
                    break;
                case 2:
                    AddSynth(t, idx, time, vol, pitch, pitchRandom, depth + 1);
                    break;
                case 3:
                case 7:
                    AddNestedSequence(t, idx, time, vol, pitch, pitchRandom, depth + 1);
                    break;
            }
        }

        void AddSynth(TrackPlan t, int synthIndex, double time, float vol, float pitch, float pitchRandom, int depth)
        {
            if (synthTable == null || synthIndex >= synthTable.RowCount) return;
            var cmd = ReadParameterCommands(synthCommandTable, synthTable.GetInt(synthIndex, "CommandIndex", 0xFFFF));
            var items = synthTable.GetData(synthIndex, "ReferenceItems");
            var sAisacs = ReadAisacs(synthTable.GetData(synthIndex, "LocalAisacs"));
            foreach (var a in sAisacs)
                if (!t.Aisacs.Exists(x => x.ControlId == a.ControlId)) t.Aisacs.Add(a);
            int synthType = (int)synthTable.GetInt(synthIndex, "Type");
            var refs = new List<(int type, int idx)>();
            for (int i = 0; i + 4 <= items.Count; i += 4) refs.Add((items.U16BE(i), items.U16BE(i + 2)));
            if (refs.Count == 0) return;
            if (synthType != 0 && refs.Count > 1)
            {
                // random-like synth: keep all as alternatives, the engine picks one per play
                var alt = new TrackPlan();
                foreach (var (ty, ix) in refs)
                    AddReference(alt, ty, ix, time, vol * cmd.Volume, pitch + cmd.Pitch, Math.Max(pitchRandom, cmd.PitchRandom), depth);
                if (alt.Events.Count > 0)
                {
                    var ev = alt.Events[0];
                    t.Events.Add(ev); // simple fallback: first alternative
                }
                return;
            }
            foreach (var (ty, ix) in refs)
                AddReference(t, ty, ix, time, vol * cmd.Volume, pitch + cmd.Pitch, Math.Max(pitchRandom, cmd.PitchRandom), depth);
        }

        void AddNestedSequence(TrackPlan t, int seqIndex, double time, float vol, float pitch, float pitchRandom, int depth)
        {
            if (seqTable == null || seqIndex >= seqTable.RowCount) return;
            var cmd = ReadParameterCommands(seqCommandTable, seqTable.GetInt(seqIndex, "CommandIndex", 0xFFFF));
            var tracks = seqTable.GetData(seqIndex, "TrackIndex").U16Array();
            int type = (int)seqTable.GetInt(seqIndex, "Type");
            if (tracks.Length == 0) return;
            IEnumerable<ushort> chosen = tracks;
            if (type != 0)
            {
                // nested random sequences are resolved once at load (rare in this game)
                chosen = new[] { tracks[0] };
            }
            foreach (ushort ti in chosen)
            {
                var sub = BuildTrack(ti, pitch + cmd.Pitch, Math.Max(pitchRandom, cmd.PitchRandom), depth);
                foreach (var ev in sub.Events)
                {
                    var e = ev;
                    e.TimeMs += time;
                    e.Volume *= vol * cmd.Volume * sub.Volume;
                    t.Events.Add(e);
                }
            }
        }

        public byte[] ReadWaveformData(AcbWaveform w)
        {
            var archive = w.Streaming ? StreamAwb : MemoryAwb;
            if (archive == null && w.Streaming) archive = MemoryAwb;
            if (archive == null) return null;
            return archive.Read(w.AwbId);
        }

        public void Close()
        {
            StreamAwb?.Close();
        }
    }
}
