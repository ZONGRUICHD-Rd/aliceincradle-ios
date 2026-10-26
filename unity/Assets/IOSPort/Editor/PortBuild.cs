using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace IOSPort.EditorTools
{
    /// <summary>
    /// Batch-mode entry point that turns the imported (AssetRipper exported)
    /// game project into an iOS Xcode project:
    ///   1. make every precompiled game assembly usable on iOS
    ///   2. point materials/settings that used dummy built-in shaders at the real ones
    ///   3. rebuild the game's AssetBundles for iOS with their original names
    ///   4. configure the iOS player (Metal, IL2CPP arm64) and build
    ///
    /// Command line arguments:
    ///   -portManifests &lt;dir&gt;  original *.dat.manifest files (relative layout kept)
    ///   -portShaderMap &lt;file&gt; TSV "assetPath\tshaderName" of dummy built-in shaders
    ///   -portOutput &lt;dir&gt;     Xcode project output directory
    ///   -portBundleId &lt;id&gt;    application identifier
    ///   -portVersion &lt;v&gt;      CFBundleShortVersionString
    /// </summary>
    public static class PortBuild
    {
        static string Arg(string name, string def = null)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return def;
        }

        static void Log(string msg) => Debug.Log("[IOSPort] " + msg);

        public static void Build()
        {
            try
            {
                Run();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        /// <summary>Only rebuilds the AssetBundles (used for quicker iteration).</summary>
        public static void BuildBundlesOnly()
        {
            try
            {
                FixPlugins();
                FixDummyShaders(Arg("-portShaderMap"));
                BuildBundles(Arg("-portManifests"));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
                return;
            }
            EditorApplication.Exit(0);
        }

        static void Run()
        {
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.iOS)
                EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);
            FixPlugins();
            WriteLinkXml();
            FixDummyShaders(Arg("-portShaderMap"));
            CheckRenderPipeline();
            RelinkRendererFeatures();
            BuildBundles(Arg("-portManifests"));
            ConfigurePlayer();
            BuildPlayer(Arg("-portOutput", "Build/iOS"));
        }

        // ------------------------------------------------------------------
        static void FixPlugins()
        {
            int n = 0;
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/") || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var importer = AssetImporter.GetAtPath(path) as PluginImporter;
                if (importer == null || importer.isNativePlugin) continue;
                importer.SetCompatibleWithAnyPlatform(true);
                importer.SetCompatibleWithEditor(true);
                importer.SetExcludeEditorFromAnyPlatform(false);
                importer.SetExcludeFromAnyPlatform(BuildTarget.iOS, false);
                importer.isPreloaded = false;
                importer.SaveAndReimport();
                n++;
            }
            Log($"configured {n} managed plugins for iOS");
        }

        static void WriteLinkXml()
        {
            // The game uses reflection (e.g. debug flags looked up by field name):
            // keep every game assembly intact when IL2CPP strips code.
            var sb = new StringBuilder();
            sb.AppendLine("<linker>");
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/Plugins/") || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                sb.AppendLine($"  <assembly fullname=\"{Path.GetFileNameWithoutExtension(path)}\" preserve=\"all\"/>");
            }
            foreach (var asm in new[] { "Unity.InputSystem", "UnityEngine.UI", "Unity.RenderPipelines.Universal.Runtime",
                         "Unity.RenderPipelines.Core.Runtime", "IOSPort.Runtime" })
                sb.AppendLine($"  <assembly fullname=\"{asm}\" preserve=\"all\"/>");
            sb.AppendLine("</linker>");
            File.WriteAllText("Assets/IOSPort/link.xml", sb.ToString());
            AssetDatabase.ImportAsset("Assets/IOSPort/link.xml");
        }

        // ------------------------------------------------------------------
        static void FixDummyShaders(string mapFile)
        {
            if (string.IsNullOrEmpty(mapFile) || !File.Exists(mapFile))
            {
                Log("no shader map, skipping dummy shader replacement");
                return;
            }
            var dummies = new Dictionary<Shader, Shader>();
            var dummyPaths = new List<string>();
            foreach (var line in File.ReadAllLines(mapFile))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                var dummy = AssetDatabase.LoadAssetAtPath<Shader>(parts[0]);
                var real = Shader.Find(parts[1]);
                if (dummy == null) continue;
                if (real == null || real == dummy)
                {
                    Log($"no real shader for '{parts[1]}' ({parts[0]}), keeping placeholder");
                    continue;
                }
                dummies[dummy] = real;
                dummyPaths.Add(parts[0]);
            }
            Log($"{dummies.Count} placeholder shaders map to built-in shaders");
            if (dummies.Count == 0) return;

            int changed = 0;
            foreach (var path in AssetDatabase.GetAllAssetPaths())
            {
                if (!path.StartsWith("Assets/") && !path.StartsWith("ProjectSettings/")) continue;
                if (path.EndsWith(".mat"))
                {
                    var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                    if (mat != null && mat.shader != null && dummies.TryGetValue(mat.shader, out var real))
                    {
                        int queue = mat.renderQueue;
                        mat.shader = real;
                        mat.renderQueue = queue;
                        EditorUtility.SetDirty(mat);
                        changed++;
                    }
                    continue;
                }
                if (!(path.EndsWith(".asset") || path.EndsWith(".prefab"))) continue;
                foreach (var obj in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (obj == null || obj is Shader) continue;
                    if (ReplaceReferences(obj, dummies)) changed++;
                }
            }
            // GraphicsSettings (always included shaders etc.)
            var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            foreach (var obj in gs)
                if (obj != null && ReplaceReferences(obj, dummies)) changed++;
            AssetDatabase.SaveAssets();
            foreach (var p in dummyPaths) AssetDatabase.DeleteAsset(p);
            Log($"replaced placeholder shader references in {changed} objects");
        }

        static bool ReplaceReferences(UnityEngine.Object obj, Dictionary<Shader, Shader> map)
        {
            var so = new SerializedObject(obj);
            var it = so.GetIterator();
            bool any = false;
            while (it.Next(true))
            {
                if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                if (it.objectReferenceValue is Shader s && map.TryGetValue(s, out var real))
                {
                    it.objectReferenceValue = real;
                    any = true;
                }
            }
            if (any)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(obj);
            }
            return any;
        }

        static void CheckRenderPipeline()
        {
            var rp = GraphicsSettings.defaultRenderPipeline;
            Log("render pipeline asset: " + (rp != null ? AssetDatabase.GetAssetPath(rp) + " (" + rp.GetType().Name + ")" : "NONE"));
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                var q = QualitySettings.GetRenderPipelineAssetAt(i);
                if (q != null) Log($"quality level {i} ({QualitySettings.names[i]}) pipeline: {AssetDatabase.GetAssetPath(q)}");
            }
        }

        // URP validates renderer features while importing, which can run before
        // the game's feature script (unsafeAssem.dll) is usable; entries can then
        // end up null. Re-link them from the references in the asset file.
        static readonly System.Text.RegularExpressions.Regex FeatureRefRx =
            new System.Text.RegularExpressions.Regex(@"^\s*-\s*\{fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]{32}))?");

        static void RelinkRendererFeatures()
        {
            foreach (var guid in AssetDatabase.FindAssets("t:ScriptableRendererData"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var data = AssetDatabase.LoadMainAssetAtPath(path);
                if (data == null) continue;
                var so = new SerializedObject(data);
                var list = so.FindProperty("m_RendererFeatures");
                if (list == null || !list.isArray || list.arraySize == 0) continue;

                // the YAML entries under m_RendererFeatures, in order
                var refs = new List<(long fileId, string guid)>();
                bool inList = false;
                foreach (var line in File.ReadAllLines(path))
                {
                    if (line.TrimStart().StartsWith("m_RendererFeatures:")) { inList = true; continue; }
                    if (!inList) continue;
                    var m = FeatureRefRx.Match(line);
                    if (!m.Success) break;
                    refs.Add((long.Parse(m.Groups[1].Value), m.Groups[2].Success ? m.Groups[2].Value : null));
                }

                bool changed = false;
                for (int i = 0; i < list.arraySize; i++)
                {
                    var el = list.GetArrayElementAtIndex(i);
                    if (el.objectReferenceValue == null && i < refs.Count)
                    {
                        var (fileId, g) = refs[i];
                        string target = g != null ? AssetDatabase.GUIDToAssetPath(g) : path;
                        UnityEngine.Object found = null;
                        if (!string.IsNullOrEmpty(target))
                        {
                            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(target))
                            {
                                if (o != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(o, out string _, out long local) && local == fileId)
                                {
                                    found = o;
                                    break;
                                }
                            }
                        }
                        if (found != null)
                        {
                            el.objectReferenceValue = found;
                            changed = true;
                        }
                    }
                    var v = el.objectReferenceValue;
                    Log($"renderer {data.name} feature {i}: " + (v != null ? v.name + " (" + v.GetType().FullName + ")" : "MISSING"));
                }
                if (changed)
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(data);
                    Log($"re-linked renderer features of {path}");
                }
            }
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------
        static void BuildBundles(string manifestDir)
        {
            if (string.IsNullOrEmpty(manifestDir) || !Directory.Exists(manifestDir))
                throw new Exception("bundle manifest directory missing: " + manifestDir);
            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var byStem = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/") || AssetDatabase.IsValidFolder(p)) continue;
                lookup[p] = p;
                byStem[StemKey(p)] = p;
            }

            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                if (!p.StartsWith("Assets/")) continue;
                var imp = AssetImporter.GetAtPath(p);
                if (imp != null && !string.IsNullOrEmpty(imp.assetBundleName)) imp.assetBundleName = null;
            }

            var bundles = new List<(string rel, string name)>();
            var builds = new List<AssetBundleBuild>();
            // Some bundles ship more than once under different paths (e.g. the
            // Korean font in localization/ko-kr and localization/__AdditionalFonts).
            // Unity allows an asset in one bundle only: build each asset set once
            // and copy the result to every path.
            var bySet = new Dictionary<string, string>();
            var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int missing = 0, shared = 0;
            foreach (var manifest in Directory.GetFiles(manifestDir, "*.manifest", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(manifestDir, manifest).Replace('\\', '/');
                rel = rel.Substring(0, rel.Length - ".manifest".Length); // e.g. EvImg/foo.dat
                var assets = new List<string>();
                bool inAssets = false;
                foreach (var raw in File.ReadAllLines(manifest))
                {
                    if (raw.StartsWith("Assets:")) { inAssets = true; continue; }
                    if (inAssets)
                    {
                        if (raw.StartsWith("- "))
                        {
                            string a = raw.Substring(2).Trim();
                            if (lookup.TryGetValue(a, out string actual) || byStem.TryGetValue(StemKey(a), out actual))
                                assets.Add(actual);
                            else
                            {
                                missing++;
                                if (missing < 200) Debug.LogWarning($"[IOSPort] bundle {rel}: asset not found {a}");
                            }
                        }
                        else if (raw.Length > 0 && !raw.StartsWith(" ")) inAssets = false;
                    }
                }
                if (assets.Count == 0)
                {
                    Debug.LogWarning($"[IOSPort] bundle {rel} has no assets, skipped");
                    continue;
                }
                assets = assets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                string setKey = string.Join("\n", assets.Select(a => a.ToLowerInvariant()).OrderBy(a => a, StringComparer.Ordinal));
                if (bySet.TryGetValue(setKey, out string existing))
                {
                    bundles.Add((rel, existing));
                    shared++;
                    continue;
                }
                string bundleName = rel.ToLowerInvariant();
                // an asset already owned by another bundle stays there; this one depends on it
                var own = new List<string>();
                foreach (var a in assets)
                {
                    if (owner.TryGetValue(a, out string other))
                        Debug.LogWarning($"[IOSPort] bundle {rel}: {a} already in {other}, left out");
                    else
                    {
                        owner[a] = bundleName;
                        own.Add(a);
                    }
                }
                if (own.Count == 0) continue;
                bySet[setKey] = bundleName;
                builds.Add(new AssetBundleBuild { assetBundleName = bundleName, assetNames = own.ToArray() });
                bundles.Add((rel, bundleName));
            }
            Log($"building {builds.Count} asset bundles for {bundles.Count} files ({shared} duplicates, {missing} manifest entries not found)");
            string outDir = "Temp/IOSBundles";
            if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            Directory.CreateDirectory(outDir);
            var result = BuildPipeline.BuildAssetBundles(outDir, builds.ToArray(),
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.DeterministicAssetBundle,
                BuildTarget.iOS);
            if (result == null) throw new Exception("BuildAssetBundles failed");
            int copied = 0;
            foreach (var (rel, name) in bundles)
            {
                string src = Path.Combine(outDir, name);
                if (!File.Exists(src))
                {
                    Debug.LogWarning("[IOSPort] bundle output missing: " + name);
                    continue;
                }
                string dst = Path.Combine("Assets/StreamingAssets", rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
                copied++;
            }
            Log($"copied {copied} bundles into StreamingAssets");
            AssetDatabase.Refresh();
        }

        static string StemKey(string path)
        {
            int slash = path.LastIndexOf('/');
            int dot = path.LastIndexOf('.');
            return dot > slash ? path.Substring(0, dot) : path;
        }

        // ------------------------------------------------------------------
        static void ConfigurePlayer()
        {
            var g = BuildTargetGroup.iOS;
            PlayerSettings.SetApplicationIdentifier(g, Arg("-portBundleId", "com.example.aliceincradle"));
            PlayerSettings.productName = "AliceInCradle";
            PlayerSettings.bundleVersion = Arg("-portVersion", "0.30.9");
            PlayerSettings.iOS.buildNumber = Arg("-portBuildNumber", "1");
            PlayerSettings.SetScriptingBackend(g, ScriptingImplementation.IL2CPP);
            PlayerSettings.SetArchitecture(g, 1); // arm64
            PlayerSettings.SetIl2CppCompilerConfiguration(g, Il2CppCompilerConfiguration.Release);
            PlayerSettings.SetManagedStrippingLevel(g, ManagedStrippingLevel.Minimal);
            PlayerSettings.stripEngineCode = false;
            PlayerSettings.SetApiCompatibilityLevel(g, ApiCompatibilityLevel.NET_Unity_4_8);
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
            PlayerSettings.iOS.targetOSVersionString = "15.0";
            PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
            PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
            PlayerSettings.iOS.appleEnableAutomaticSigning = false;
            PlayerSettings.iOS.appleDeveloperTeamID = "";
            PlayerSettings.iOS.requiresFullScreen = true;
            PlayerSettings.iOS.hideHomeButton = true;
            PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
            PlayerSettings.iOS.statusBarStyle = iOSStatusBarStyle.LightContent;
            PlayerSettings.statusBarHidden = true;
            PlayerSettings.muteOtherAudioSources = false;
            PlayerSettings.runInBackground = false;
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;
            PlayerSettings.useAnimatedAutorotation = true;
            // Files app access to saves (Documents) is handy for a port
            PlayerSettings.iOS.allowHTTPDownload = false;
            Log("player settings configured: " + PlayerSettings.GetApplicationIdentifier(g));
        }

        static void BuildPlayer(string output)
        {
            var scenes = EditorBuildSettings.scenes.Where(s => s.enabled && File.Exists(s.path)).Select(s => s.path).ToArray();
            if (scenes.Length == 0)
            {
                scenes = AssetDatabase.FindAssets("t:Scene", new[] { "Assets" })
                    .Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p).ToArray();
            }
            Log("scenes: " + string.Join(", ", scenes));
            CheckMissingScripts(scenes);
            if (Directory.Exists(output)) Directory.Delete(output, true);
            var opts = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = output,
                target = BuildTarget.iOS,
                targetGroup = BuildTargetGroup.iOS,
                options = BuildOptions.None,
            };
            var report = BuildPipeline.BuildPlayer(opts);
            Log($"build result: {report.summary.result}, {report.summary.totalErrors} errors, size {report.summary.totalSize}");
            foreach (var step in report.steps)
                foreach (var m in step.messages)
                    if (m.type == LogType.Error || m.type == LogType.Exception)
                        Debug.LogError("[IOSPort] " + step.name + ": " + m.content);
            if (report.summary.result != BuildResult.Succeeded) throw new Exception("player build failed");
            EnableFileSharing(Path.Combine(output, "Info.plist"));
        }

        /// <summary>
        /// A component whose script the editor cannot resolve is written without
        /// its fields, and the player then reads past the end of the data
        /// (fatal while loading the scene). Fail early instead.
        /// </summary>
        static void CheckMissingScripts(string[] scenes)
        {
            int sceneMissing = 0;
            foreach (var path in scenes)
            {
                var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Single);
                int n = 0;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        int c = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
                        if (c > 0) { n += c; Debug.LogError($"[IOSPort] missing script on '{t.name}' in {path}"); }
                    }
                Log($"scene {path}: {n} missing scripts");
                sceneMissing += n;
            }
            int prefabMissing = 0, prefabs = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }))
            {
                var go = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (go == null) continue;
                prefabs++;
                foreach (var t in go.GetComponentsInChildren<Transform>(true))
                    prefabMissing += GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(t.gameObject);
            }
            Log($"{prefabs} prefabs: {prefabMissing} missing scripts");
            if (sceneMissing > 0) throw new Exception($"{sceneMissing} missing scripts in scenes (see log)");
        }

        /// <summary>Show the app's Documents folder (saves, iosport-log.txt) in the Files app.</summary>
        static void EnableFileSharing(string plist)
        {
            if (!File.Exists(plist)) throw new Exception("Info.plist not found: " + plist);
            string text = File.ReadAllText(plist);
            var add = new StringBuilder();
            foreach (var key in new[] { "UIFileSharingEnabled", "LSSupportsOpeningDocumentsInPlace" })
                if (!text.Contains("<key>" + key + "</key>"))
                    add.Append("\t<key>").Append(key).Append("</key>\n\t<true/>\n");
            int end = text.LastIndexOf("</dict>", StringComparison.Ordinal);
            if (end < 0) throw new Exception("unexpected Info.plist format");
            File.WriteAllText(plist, text.Insert(end, add.ToString()));
            Log("Info.plist: file sharing enabled");
        }
    }
}
