using UnityEngine;

namespace CriCompat.Engine
{
    /// <summary>
    /// Hosts the software mixer: an AudioSource playing silence whose filter
    /// callback is replaced by the engine output.
    /// </summary>
    [AddComponentMenu("")]
    internal sealed class AtomMixer : MonoBehaviour
    {
        static AtomMixer instance;
        AudioSource source;

        public static AtomMixer Ensure(GameObject host)
        {
            if (instance != null) return instance;
            instance = host.GetComponent<AtomMixer>();
            if (instance == null) instance = host.AddComponent<AtomMixer>();
            return instance;
        }

        void Awake()
        {
            AtomEngine.Instance.OutputRate = AudioSettings.outputSampleRate;
            source = gameObject.GetComponent<AudioSource>();
            if (source == null) source = gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true;
            source.spatialBlend = 0f;
            source.priority = 0;
            source.bypassEffects = true;
            source.bypassListenerEffects = true;
            source.bypassReverbZones = true;
            source.volume = 1f;
            int rate = AudioSettings.outputSampleRate;
            var clip = AudioClip.Create("CriCompatMixer", rate, 2, rate, false);
            source.clip = clip;
            source.Play();
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigChanged;
        }

        void OnDestroy()
        {
            AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigChanged;
            if (instance == this) instance = null;
        }

        void OnAudioConfigChanged(bool deviceWasChanged)
        {
            AtomEngine.Instance.OutputRate = AudioSettings.outputSampleRate;
            if (source != null && !source.isPlaying) source.Play();
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused && source != null && !source.isPlaying) source.Play();
        }

        void Update()
        {
            AtomEngine.Instance.MainThreadUpdate(CriWare.CriAtom.DispatchBeat);
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            System.Array.Clear(data, 0, data.Length);
            AtomEngine.Instance.Mix(data, channels);
        }
    }
}
