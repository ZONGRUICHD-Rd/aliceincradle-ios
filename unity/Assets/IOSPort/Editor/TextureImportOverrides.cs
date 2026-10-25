using UnityEditor;

namespace IOSPort.EditorTools
{
    /// <summary>
    /// The game is pixel art: keep every texture uncompressed on iOS (no ASTC
    /// artifacts, and importing hundreds of large textures stays fast in CI).
    /// </summary>
    sealed class TextureImportOverrides : AssetPostprocessor
    {
        public override uint GetVersion() => 2;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith("Assets/") || assetPath.StartsWith("Assets/IOSPort/")) return;
            var importer = (TextureImporter)assetImporter;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.crunchedCompression = false;
            var ios = importer.GetPlatformTextureSettings("iPhone");
            ios.overridden = true;
            ios.format = TextureImporterFormat.RGBA32;
            ios.textureCompression = TextureImporterCompression.Uncompressed;
            ios.maxTextureSize = 8192;
            importer.SetPlatformTextureSettings(ios);
        }
    }
}
