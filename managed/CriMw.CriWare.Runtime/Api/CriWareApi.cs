// Drop-in replacement for the subset of the CRIWARE Unity plug-in API used by
// AliceInCradle.  Type and member names mirror the original plug-in so the
// game's precompiled assemblies bind to this implementation unchanged; the
// implementation itself is a managed ACB/AWB/HCA player (see CriCompat.*).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CriCompat.Acb;
using CriCompat.Engine;
using UnityEngine;

public class CriFsBinder : IDisposable
{
    public void Dispose() { }
}

namespace CriWare
{
    public abstract class CriDisposable : IDisposable
    {
        public abstract void Dispose();
    }

    public static class CriAtomEx
    {
        public enum ResumeMode
        {
            AllPlayback,
            PausedPlayback,
            PreparedPlayback
        }

        public enum CueType
        {
            Polyphonic,
            Sequential,
            Shuffle,
            Random,
            RandomNoRepeat,
            SwitchGameVariable,
            ComboSequential,
            SwitchSelector,
            TrackTransitionBySelector
        }

        public enum PanType
        {
            Pan3d,
            Pos3d,
            Auto
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CueInfo
        {
            public int id;
            public CueType type;
            public readonly string name;
            public readonly string userData;
            public long length;
            public ushort[] categories;
            public short numLimits;
            public ushort numBlocks;
            public ushort numTracks;
            public ushort numRelatedWaveForms;
            public byte priority;
            public byte headerVisibility;
            public byte ignore_player_parameter;
            public byte probability;
            public PanType panType;
            public float volume;

            internal CueInfo(AcbCue cue)
            {
                id = cue.Id;
                type = CueType.Polyphonic;
                name = cue.Name;
                userData = "";
                length = cue.LengthMs;
                categories = (ushort[])cue.Categories.Clone();
                numLimits = 0;
                numBlocks = cue.NumBlocks;
                numTracks = (ushort)Math.Max(cue.Plan?.Tracks.Count ?? 0, cue.Plan?.LayerAisacs.Count ?? 0);
                numRelatedWaveForms = 0;
                priority = 64;
                headerVisibility = 1;
                ignore_player_parameter = 0;
                probability = 100;
                panType = PanType.Pan3d;
                volume = cue.Plan?.Volume ?? 1f;
            }
        }
    }

    public static class CriAtomExBeatSync
    {
        public struct Info
        {
            public IntPtr playerHn;
            public uint playbackId;
            public uint barCount;
            public uint beatCount;
            public float beatProgress;
            public float bpm;
            public int offset;
            public uint numBeats;
            public IntPtr labelPtr;

            public string label => "";
        }

        public delegate void CbFunc(ref Info info);
    }

    [Serializable]
    public class CriAtomConfig
    {
        public string acfFileName = "";
        public int maxVirtualVoices = 32;
        public int maxVoiceLimitGroups = 32;
        public int maxCategories = 32;
        public int maxAisacs = 8;
        public int maxBusSends = 8;
        public int maxSequenceEventsPerFrame = 2;
        public int maxBeatSyncCallbacksPerFrame = 1;
        public int maxCueLinkCallbacksPerFrame = 1;
        public int outputSamplingRate;
        public bool usesInGamePreview;
        public float serverFrequency = 60f;
        public int asrOutputChannels;
        public bool useRandomSeedWithTime = true;
        public int categoriesPerPlayback = 4;
        public int maxFaders = 4;
        public int maxBuses = 8;
        public float maxPitch = 2400f;
        public int maxParameterBlocks = 1024;
        public bool keepPlayingSoundOnPause = true;
        public bool enableSonicSync = true;
        public bool enableAtomSoundDisabledMode;
        public int pcBufferingTime;
        public bool useMicrosoftSpatialSound = true;
        public bool iosEnableSonicSync = true;
        public int iosBufferingTime = 50;
        public bool iosOverrideIPodMusic;
        public bool iosEnableOSNotificationHandling = true;
    }

    public enum CriAtomExAcbLoaderStatus
    {
        Stop,
        Loading,
        Complete,
        Error
    }

    [Serializable]
    public class CriAtomCueSheet
    {
        public string name = "";
        public string acbFile = "";
        public string awbFile = "";
        public CriAtomExAcb acb;

        public bool IsLoading => false;
        public bool IsError => acb == null;
    }

    public class CriAtomExAcb : CriDisposable
    {
        internal readonly CueSheetData Sheet;

        internal CriAtomExAcb(CueSheetData sheet)
        {
            Sheet = sheet;
        }

        public CriAtomEx.CueInfo[] GetCueInfoList()
        {
            var cues = Sheet.Acb.Cues;
            var r = new CriAtomEx.CueInfo[cues.Count];
            for (int i = 0; i < cues.Count; i++) r[i] = new CriAtomEx.CueInfo(cues[i]);
            return r;
        }

        public bool GetCueInfo(string cueName, out CriAtomEx.CueInfo info)
        {
            if (cueName != null && Sheet.Acb.CuesByName.TryGetValue(cueName, out AcbCue cue))
            {
                info = new CriAtomEx.CueInfo(cue);
                return true;
            }
            info = default;
            return false;
        }

        public bool GetCueInfo(int cueId, out CriAtomEx.CueInfo info)
        {
            if (Sheet.Acb.CuesById.TryGetValue(cueId, out AcbCue cue))
            {
                info = new CriAtomEx.CueInfo(cue);
                return true;
            }
            info = default;
            return false;
        }

        public bool Exists(string cueName) => cueName != null && Sheet.Acb.CuesByName.ContainsKey(cueName);

        public override void Dispose() { }
    }

    public struct CriAtomExPlayback
    {
        public enum Status
        {
            Prep = 1,
            Playing,
            Removed
        }

        public uint id;

        public CriAtomExPlayback(uint id)
        {
            this.id = id;
        }

        public Status GetStatus()
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb == null || pb.State == PlaybackState.Removed) return Status.Removed;
            return pb.State == PlaybackState.Prep ? Status.Prep : Status.Playing;
        }

        public void Resume(CriAtomEx.ResumeMode mode)
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb != null) AtomEngine.Instance.ResumePlayback(pb, (int)mode);
        }

        public void Pause()
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb != null)
                lock (AtomEngine.Instance.Lock) pb.Paused = true;
        }

        public void Stop(bool ignoresReleaseTime = false)
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb != null) AtomEngine.Instance.StopPlayback(pb, ignoresReleaseTime, pb.Player.Params.ReleaseMs);
        }

        public int GetCurrentBlockIndex()
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb == null || !pb.Cue.Plan.IsBlockSequence) return -1;
            lock (AtomEngine.Instance.Lock) return pb.CurBlock;
        }

        public void SetNextBlockIndex(int index)
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb == null) return;
            lock (AtomEngine.Instance.Lock) pb.RequestedNext = index;
        }

        public long GetTime()
        {
            var pb = AtomEngine.Instance.Find(id);
            if (pb == null) return -1;
            lock (AtomEngine.Instance.Lock) return pb.Time * 1000L / Math.Max(1, AtomEngine.Instance.OutputRate);
        }
    }

    public class CriAtomExPlayer : CriDisposable
    {
        public enum Status
        {
            Stop,
            Prep,
            Playing,
            PlayEnd,
            Error
        }

        internal readonly PlayerCore Core;
        bool stoppedExplicitly;

        public CriAtomExPlayer()
        {
            CriAtomServer.EnsureRunning();
            Core = AtomEngine.Instance.CreatePlayer();
        }

        public CriAtomExPlayer(bool enableAudioSyncedTimer) : this() { }

        public override void Dispose()
        {
            if (!Core.Disposed) AtomEngine.Instance.DestroyPlayer(Core);
            GC.SuppressFinalize(this);
        }

        public void SetCue(CriAtomExAcb acb, int id)
        {
            Core.Sheet = acb?.Sheet;
            Core.Cue = null;
            if (acb != null) acb.Sheet.Acb.CuesById.TryGetValue(id, out Core.Cue);
        }

        public void SetCue(CriAtomExAcb acb, string name)
        {
            Core.Sheet = acb?.Sheet;
            Core.Cue = null;
            if (acb != null && name != null) acb.Sheet.Acb.CuesByName.TryGetValue(name, out Core.Cue);
        }

        public CriAtomExPlayback Start()
        {
            stoppedExplicitly = false;
            var pb = AtomEngine.Instance.Start(Core, false);
            return new CriAtomExPlayback(pb != null ? pb.Id : 0u);
        }

        public CriAtomExPlayback Prepare()
        {
            stoppedExplicitly = false;
            var pb = AtomEngine.Instance.Start(Core, true);
            return new CriAtomExPlayback(pb != null ? pb.Id : 0u);
        }

        public void Stop()
        {
            stoppedExplicitly = true;
            AtomEngine.Instance.StopPlayer(Core, false);
        }

        public void Stop(bool ignoresReleaseTime)
        {
            stoppedExplicitly = true;
            AtomEngine.Instance.StopPlayer(Core, ignoresReleaseTime);
        }

        public void StopWithoutReleaseTime()
        {
            stoppedExplicitly = true;
            AtomEngine.Instance.StopPlayer(Core, true);
        }

        public void Pause() => AtomEngine.Instance.SetPaused(Core, true);

        public void Pause(bool sw) => AtomEngine.Instance.SetPaused(Core, sw);

        public void Resume(CriAtomEx.ResumeMode mode) => AtomEngine.Instance.Resume(Core, (int)mode);

        public bool IsPaused()
        {
            lock (AtomEngine.Instance.Lock) return Core.Paused;
        }

        public Status GetStatus()
        {
            lock (AtomEngine.Instance.Lock)
            {
                if (Core.Disposed) return Status.Stop;
                bool prep = false;
                foreach (var pb in Core.Playbacks)
                {
                    if (pb.State == PlaybackState.Playing) return Status.Playing;
                    if (pb.State == PlaybackState.Prep) prep = true;
                }
                if (prep) return Status.Prep;
                if (!Core.EverStarted || stoppedExplicitly) return Status.Stop;
                return Status.PlayEnd;
            }
        }

        public long GetTime()
        {
            lock (AtomEngine.Instance.Lock)
            {
                var pb = Core.Latest;
                if (pb == null) return 0;
                return pb.Time * 1000L / Math.Max(1, AtomEngine.Instance.OutputRate);
            }
        }

        public void UpdateAll() => AtomEngine.Instance.UpdateParams(Core);

        public void Update(CriAtomExPlayback playback) => AtomEngine.Instance.UpdateParams(Core);

        public void ResetParameters()
        {
            lock (AtomEngine.Instance.Lock) Core.Params.Reset();
        }

        public void SetVolume(float volume)
        {
            lock (AtomEngine.Instance.Lock) Core.Params.Volume = volume;
        }

        public void SetPitch(float pitch)
        {
            lock (AtomEngine.Instance.Lock) Core.Params.PitchCents = pitch;
        }

        public void SetPan3dAngle(float angle)
        {
            lock (AtomEngine.Instance.Lock) Core.Params.PanAngle = angle;
        }

        public void SetAisacControl(string controlName, float value)
        {
            if (controlName == null) return;
            lock (AtomEngine.Instance.Lock) Core.Params.Aisac[controlName] = value;
        }

        public void SetEnvelopeReleaseTime(float time)
        {
            lock (AtomEngine.Instance.Lock) Core.Params.ReleaseMs = time;
        }

        public void SetFirstBlockIndex(int index)
        {
            lock (AtomEngine.Instance.Lock) Core.Params.FirstBlock = index;
        }

        public void SetCategory(int categoryId)
        {
            lock (AtomEngine.Instance.Lock) Core.Params.Category = categoryId;
        }

        public void SetCategory(string categoryName) { }

        public void UnsetCategory()
        {
            lock (AtomEngine.Instance.Lock) Core.Params.Category = -1;
        }

        public void SetVoicePriority(int priority) { }

        public void SetBusSendLevel(string busName, float level) { }

        public void SetBusSendLevelOffset(string busName, float levelOffset) { }

        ~CriAtomExPlayer()
        {
            // players are owned by the engine until disposed explicitly
        }
    }

    public class CriAtomExOutputAnalyzer : CriDisposable
    {
        public struct Config
        {
            public bool enableLevelmeter;
            public bool enableSpectrumAnalyzer;
            public bool enablePcmCapture;
            public bool enablePcmCaptureCallback;
            public int numSpectrumAnalyzerBands;
            public int numCapturedPcmSamples;

            public Config(bool enableLevelmeter = false, bool enableSpectrumAnalyzer = false,
                int numSpectrumAnalyzerBands = 8, bool enablePcmCapture = false,
                bool enablePcmCaptureCallback = false, int numCapturedPcmSamples = 4096)
            {
                this.enableLevelmeter = enableLevelmeter;
                this.enableSpectrumAnalyzer = enableSpectrumAnalyzer;
                this.numSpectrumAnalyzerBands = numSpectrumAnalyzerBands;
                this.enablePcmCapture = enablePcmCapture;
                this.enablePcmCaptureCallback = enablePcmCaptureCallback;
                this.numCapturedPcmSamples = numCapturedPcmSamples;
            }
        }

        readonly int samples;
        PlayerCore attached;

        public CriAtomExOutputAnalyzer(Config config)
        {
            samples = Math.Max(1, config.numCapturedPcmSamples);
        }

        public bool AttachExPlayer(CriAtomExPlayer player)
        {
            if (player == null) return false;
            DetachExPlayer();
            lock (AtomEngine.Instance.Lock)
            {
                attached = player.Core;
                attached.CaptureLength = samples;
                attached.Capture = new[] { new float[samples], new float[samples] };
            }
            return true;
        }

        public void DetachExPlayer()
        {
            lock (AtomEngine.Instance.Lock)
            {
                if (attached != null) attached.Capture = null;
                attached = null;
            }
        }

        public void GetPcmData(ref float[] data, int ch)
        {
            if (data == null || data.Length < samples) data = new float[samples];
            lock (AtomEngine.Instance.Lock)
            {
                if (attached?.Capture == null || ch < 0 || ch >= attached.Capture.Length)
                {
                    Array.Clear(data, 0, data.Length);
                    return;
                }
                Array.Copy(attached.Capture[ch], data, Math.Min(samples, data.Length));
            }
        }

        public override void Dispose()
        {
            DetachExPlayer();
        }
    }

    public class CriAtomServer : MonoBehaviour
    {
        static CriAtomServer _instance;

        public Action<bool> onApplicationPausePreProcess;
        public Action<bool> onApplicationPausePostProcess;
        public static bool KeepPlayingSoundOnPause = true;

        public static CriAtomServer instance
        {
            get
            {
                EnsureRunning();
                return _instance;
            }
        }

        internal static void EnsureRunning()
        {
            if (_instance != null) return;
            var go = new GameObject("CRIWARE");
            _instance = go.AddComponent<CriAtomServer>();
        }

        void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this);
                return;
            }
            _instance = this;
            AtomMixer.Ensure(gameObject);
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }

    public class CriAtom : MonoBehaviour
    {
        public string acfFile = "";
        public CriAtomCueSheet[] cueSheets = new CriAtomCueSheet[0];
        public string dspBusSetting = "";
        public bool dontDestroyOnLoad;
        public bool dontRemoveExistsCueSheet;

        static readonly Dictionary<string, CriAtomCueSheet> sheets = new Dictionary<string, CriAtomCueSheet>();

        public static event CriAtomExBeatSync.CbFunc OnBeatSyncCallback;

        public static bool CueSheetsAreLoading => false;

        void Awake()
        {
            CriAtomServer.EnsureRunning();
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
            foreach (var s in cueSheets)
                if (s != null && !string.IsNullOrEmpty(s.name)) AddCueSheet(s.name, s.acbFile, s.awbFile);
        }

        internal static string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;
            return Path.Combine(Application.streamingAssetsPath, path);
        }

        public static CriAtomCueSheet AddCueSheet(string name, string acbFile, string awbFile, CriFsBinder binder = null)
        {
            CriAtomServer.EnsureRunning();
            if (name == null) return null;
            if (sheets.TryGetValue(name, out CriAtomCueSheet existing)) return existing;
            var sheet = new CriAtomCueSheet { name = name, acbFile = acbFile, awbFile = awbFile };
            try
            {
                string acbPath = ResolvePath(acbFile);
                string awbPath = string.IsNullOrEmpty(awbFile) ? null : ResolvePath(awbFile);
                byte[] data = File.ReadAllBytes(acbPath);
                var acb = AcbFile.Load(data, awbPath);
                foreach (var w in acb.Warnings) Debug.LogWarning("[CriCompat] " + name + ": " + w);
                sheet.acb = new CriAtomExAcb(new CueSheetData { Name = name, Acb = acb });
            }
            catch (Exception e)
            {
                CriWareErrorHandler.Report("Failed to load cue sheet " + name + ": " + e.Message);
            }
            sheets[name] = sheet;
            return sheet;
        }

        public static void RemoveCueSheet(string name)
        {
            if (name == null || !sheets.TryGetValue(name, out CriAtomCueSheet sheet)) return;
            sheets.Remove(name);
            if (sheet.acb == null) return;
            var data = sheet.acb.Sheet;
            var engine = AtomEngine.Instance;
            lock (engine.Lock)
            {
                // stop sounds that still use this sheet
                foreach (var pb in new List<Playback>(EnumeratePlaybacks(data))) engine.StopPlayback(pb, true, 0);
            }
            data.Release();
        }

        static IEnumerable<Playback> EnumeratePlaybacks(CueSheetData data)
        {
            var engine = AtomEngine.Instance;
            foreach (var kv in engine.AllPlaybacks())
                if (kv.Sheet == data) yield return kv;
        }

        public static CriAtomCueSheet GetCueSheet(string name) =>
            name != null && sheets.TryGetValue(name, out CriAtomCueSheet s) ? s : null;

        public static CriAtomExAcb GetAcb(string cueSheetName) => GetCueSheet(cueSheetName)?.acb;

        internal static void DispatchBeat(Playback pb, int beat, int beatsPerBar)
        {
            var cb = OnBeatSyncCallback;
            if (cb == null) return;
            var info = new CriAtomExBeatSync.Info
            {
                playerHn = IntPtr.Zero,
                playbackId = pb.Id,
                barCount = (uint)(beat / Math.Max(1, beatsPerBar)),
                beatCount = (uint)(beat % Math.Max(1, beatsPerBar)),
                beatProgress = 0f,
                bpm = pb.Cue.Plan.Bpm,
                offset = 0,
                numBeats = (uint)beatsPerBar,
            };
            try
            {
                cb(ref info);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }

    public class CriWareErrorHandler : MonoBehaviour
    {
        public delegate void Callback(string message);

        public bool enableDebugPrintOnTerminal;
        public bool enableForceCrashOnError;
        public bool dontDestroyOnLoad = true;
        public static readonly string logPrefix = "[CRIWARE]";
        public static Callback callback;
        public uint messageBufferCounts = 8u;

        public static event Callback OnCallback;

        void Awake()
        {
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
        }

        internal static void Report(string message)
        {
            Debug.LogWarning(logPrefix + " " + message);
            try
            {
                OnCallback?.Invoke(message);
                callback?.Invoke(message);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }

    public class CriWareInitializer : MonoBehaviour
    {
        public bool initializesFileSystem = true;
        public bool initializesAtom = true;
        public CriAtomConfig atomConfig = new CriAtomConfig();
        public bool initializesMana = true;
        public bool dontInitializeOnAwake;
        public bool dontDestroyOnLoad;

        static bool initialized;

        void Awake()
        {
            if (!dontInitializeOnAwake) Initialize();
            if (dontDestroyOnLoad) DontDestroyOnLoad(gameObject);
        }

        public void Initialize()
        {
            CriAtomServer.EnsureRunning();
            initialized = true;
        }

        public void Shutdown()
        {
            initialized = false;
        }

        public static bool IsInitialized() => initialized;
    }
}
