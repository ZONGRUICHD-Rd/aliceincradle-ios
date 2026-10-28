using System.IO;
using UnityEngine;

namespace IOSPort.Compat
{
    /// <summary>
    /// Drop-in replacements for the file calls made by the game assemblies.
    /// tools/PortTool redirect-io rewrites the call sites to these methods; each
    /// takes the original arguments and returns what the original returns.
    /// </summary>
    public static class GameIO
    {
        public static bool File_Exists(string path) => File.Exists(CaseIO.Resolve(path));

        public static bool Directory_Exists(string path) => Directory.Exists(CaseIO.Resolve(path));

        public static FileStream File_OpenRead(string path) => File.OpenRead(CaseIO.Resolve(path));

        public static FileAttributes File_GetAttributes(string path) => File.GetAttributes(CaseIO.Resolve(path));

        public static string[] Directory_GetFiles(string path, string searchPattern) =>
            CaseIO.GetFiles(path, searchPattern, SearchOption.TopDirectoryOnly);

        public static string[] Directory_GetFiles(string path, string searchPattern, SearchOption searchOption) =>
            CaseIO.GetFiles(path, searchPattern, searchOption);

        public static FileStream FileStream_ctor(string path, FileMode mode, FileAccess access, FileShare share)
        {
            bool mustExist = mode == FileMode.Open || mode == FileMode.Truncate;
            return new FileStream(mustExist ? CaseIO.Resolve(path) : CaseIO.ResolveForCreate(path), mode, access, share);
        }

        public static StreamReader StreamReader_ctor(string path) => new StreamReader(CaseIO.Resolve(path));

        public static FileInfo FileInfo_ctor(string path) => new FileInfo(CaseIO.Resolve(path));

        public static AssetBundle AssetBundle_LoadFromFile(string path) => AssetBundle.LoadFromFile(CaseIO.Resolve(path));

        public static AssetBundleCreateRequest AssetBundle_LoadFromFileAsync(string path) =>
            AssetBundle.LoadFromFileAsync(CaseIO.Resolve(path));
    }
}
