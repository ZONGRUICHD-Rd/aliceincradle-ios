// Stand-in for the StandaloneFileBrowser (SFB) plug-in.  The Windows build
// uses native dialogs (user32 / Windows Forms) which do not exist on iOS;
// the game only uses them for optional import/export of mini-game and editor
// files, so every dialog reports "cancelled".
using System;

namespace SFB
{
    public struct ExtensionFilter
    {
        public string Name;
        public string[] Extensions;

        public ExtensionFilter(string filterName, params string[] filterExtensions)
        {
            Name = filterName;
            Extensions = filterExtensions;
        }
    }

    public class StandaloneFileBrowser
    {
        public static string[] OpenFilePanel(string title, string directory, string extension, bool multiselect) =>
            Array.Empty<string>();

        public static string[] OpenFilePanel(string title, string directory, ExtensionFilter[] extensions, bool multiselect) =>
            Array.Empty<string>();

        public static void OpenFilePanelAsync(string title, string directory, string extension, bool multiselect, Action<string[]> cb) =>
            cb?.Invoke(Array.Empty<string>());

        public static void OpenFilePanelAsync(string title, string directory, ExtensionFilter[] extensions, bool multiselect, Action<string[]> cb) =>
            cb?.Invoke(Array.Empty<string>());

        public static string[] OpenFolderPanel(string title, string directory, bool multiselect) => Array.Empty<string>();

        public static void OpenFolderPanelAsync(string title, string directory, bool multiselect, Action<string[]> cb) =>
            cb?.Invoke(Array.Empty<string>());

        public static string SaveFilePanel(string title, string directory, string defaultName, string extension) => "";

        public static string SaveFilePanel(string title, string directory, string defaultName, ExtensionFilter[] extensions) => "";

        public static void SaveFilePanelAsync(string title, string directory, string defaultName, string extension, Action<string> cb) =>
            cb?.Invoke("");

        public static void SaveFilePanelAsync(string title, string directory, string defaultName, ExtensionFilter[] extensions, Action<string> cb) =>
            cb?.Invoke("");
    }
}
