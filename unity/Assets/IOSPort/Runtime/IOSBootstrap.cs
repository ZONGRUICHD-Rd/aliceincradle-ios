using UnityEngine;
using UnityEngine.InputSystem;

namespace IOSPort
{
    /// <summary>Startup tweaks that make the desktop build behave on iOS.</summary>
    public static class IOSBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void BeforeSceneLoad()
        {
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
            Application.targetFrameRate = 60;
            Input.multiTouchEnabled = true;

            // The game expects a keyboard to exist (desktop assumption) and a few
            // code paths read Keyboard.current without null checks.  Register an
            // idle virtual keyboard when no hardware keyboard is attached.
            if (Keyboard.current == null)
                InputSystem.AddDevice<Keyboard>("IOSVirtualKeyboard");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AfterSceneLoad()
        {
            TouchGamepad.Create();
        }
    }
}
