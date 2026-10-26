using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace PortTool;

/// <summary>
/// Helpers used by the CI pipeline to turn an AssetRipper export of the
/// Windows build into an iOS-buildable Unity project.
///
///   md4-selftest
///   script-fileid  &lt;namespace&gt; &lt;class&gt;
///   remap-scripts  &lt;assetsDir&gt; &lt;packageDir[;packageDir...] (path separator)&gt; &lt;assemblyName&gt;...
///       Rewrites YAML references to scripts living in exported package DLLs
///       (Assets/Plugins/X.dll) so they point to the package sources instead.
///   check-refs     &lt;dir&gt; &lt;targetAssembly&gt; &lt;asm&gt;...
///       Verifies every member reference into targetAssembly resolves.
///   rename-assembly &lt;dll&gt; &lt;newName&gt; [searchDir...]
///       Renames a plugin assembly (file, assembly and module name), keeps its
///       .meta (GUID) and updates references from the other DLLs next to it.
///       searchDirs help resolve referenced assemblies (e.g. the game's Managed).
///   fix-generic-comparers &lt;dll&gt;
///       Rewrites IEqualityComparer&lt;T&gt; methods that compare an unconstrained T
///       with raw IL (ldarg; ldarg; ceq), which IL2CPP rejects, to call
///       EqualityComparer&lt;T&gt;.Default instead.
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: PortTool <command> ...");
            return 2;
        }
        switch (args[0])
        {
            case "md4-selftest": return Md4SelfTest();
            case "script-fileid":
                Console.WriteLine(ScriptFileId(args[1], args[2]));
                return 0;
            case "assembly-guid":
                Console.WriteLine(AssemblyGuid(args[1]));
                return 0;
            case "remap-scripts": return RemapScripts(args[1], args[2], args.Skip(3).ToArray());
            case "check-refs": return CheckRefs(args[1], args[2], args.Skip(3).ToArray());
            case "fix-generic-comparers": return FixGenericComparers(args[1]);
            case "rename-assembly": return RenameAssembly(args[1], args[2], args.Skip(3).ToArray());
            default:
                Console.Error.WriteLine("unknown command " + args[0]);
                return 2;
        }
    }

    static int Md4SelfTest()
    {
        string h1 = Convert.ToHexString(Md4.Hash(Array.Empty<byte>())).ToLowerInvariant();
        string h2 = Convert.ToHexString(Md4.Hash(Encoding.ASCII.GetBytes("abc"))).ToLowerInvariant();
        bool ok = h1 == "31d6cfe0d16ae931b73c59d7e0c089c0" && h2 == "a448017aaf21d8525fc10ae87aa6729d";
        Console.WriteLine($"{h1} {h2} {(ok ? "OK" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// <summary>Unity's fileID for a script class inside a compiled assembly.</summary>
    public static int ScriptFileId(string ns, string name)
    {
        var bytes = new List<byte> { (byte)'s', 0, 0, 0 };
        bytes.AddRange(Encoding.UTF8.GetBytes(ns ?? ""));
        bytes.AddRange(Encoding.UTF8.GetBytes(name));
        return BitConverter.ToInt32(Md4.Hash(bytes.ToArray()), 0);
    }

    /// <summary>AssetRipper's deterministic GUID for an exported assembly (UnityGuid.Md5Hash).</summary>
    public static string AssemblyGuid(string assemblyName)
    {
        // UnityGuid.Md5Hash: MD5 of the UTF8 name, then each byte's nibbles are swapped
        // when formatted.  We do not need the exact algorithm: the CI reads the GUID
        // from the exported .dll.meta file instead.  Kept for diagnostics.
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(assemblyName));
        return Convert.ToHexString(md5).ToLowerInvariant();
    }

    static string ReadMetaGuid(string metaPath)
    {
        foreach (var line in File.ReadLines(metaPath))
        {
            var m = Regex.Match(line, @"^guid:\s*([0-9a-fA-F]{32})");
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    static readonly Regex NamespaceRx = new(@"^\s*namespace\s+([A-Za-z0-9_.]+)", RegexOptions.Multiline);

    /// <summary>Map "Namespace.Class" to the GUID of the package source file.</summary>
    static Dictionary<string, string> IndexPackageScripts(string packageDirs)
    {
        var map = new Dictionary<string, string>();
        var files = packageDirs.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories));
        foreach (var cs in files)
        {
            string meta = cs + ".meta";
            if (!File.Exists(meta)) continue;
            string guid = ReadMetaGuid(meta);
            if (guid == null) continue;
            string cls = Path.GetFileNameWithoutExtension(cs);
            string text;
            try { text = File.ReadAllText(cs); }
            catch { continue; }
            var nsMatches = NamespaceRx.Matches(text);
            var namespaces = nsMatches.Count > 0 ? nsMatches.Select(m => m.Groups[1].Value).Distinct().ToList() : new List<string> { "" };
            foreach (var ns in namespaces)
            {
                string key = ns.Length > 0 ? ns + "." + cls : cls;
                map.TryAdd(key, guid);
            }
        }
        return map;
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        foreach (var t in m.Types)
            foreach (var x in Nested(t)) yield return x;
        static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
        {
            yield return t;
            foreach (var n in t.NestedTypes)
                foreach (var x in Nested(n)) yield return x;
        }
    }

    static int RemapScripts(string assetsDir, string packageCache, string[] assemblies)
    {
        var pkg = IndexPackageScripts(packageCache);
        Console.WriteLine($"indexed {pkg.Count} package scripts");
        // (oldGuid, fileId) -> newGuid
        var remap = new Dictionary<(string, long), string>();
        int unresolved = 0;
        foreach (var asm in assemblies)
        {
            string dll = Path.Combine(assetsDir, "Plugins", asm + ".dll");
            if (!File.Exists(dll) || !File.Exists(dll + ".meta"))
            {
                Console.WriteLine($"  {asm}: not exported, skipped");
                continue;
            }
            string guid = ReadMetaGuid(dll + ".meta");
            var module = ModuleDefinition.ReadModule(dll);
            int n = 0;
            foreach (var t in AllTypes(module))
            {
                if (t.DeclaringType != null) continue; // nested classes cannot be scripts
                string full = t.Namespace.Length > 0 ? t.Namespace + "." + t.Name : t.Name;
                int fid = ScriptFileId(t.Namespace, t.Name);
                if (pkg.TryGetValue(full, out string newGuid))
                {
                    remap[(guid, fid)] = newGuid;
                    n++;
                }
            }
            Console.WriteLine($"  {asm} ({guid}): {n} classes mapped");
        }

        var refRx = new Regex(@"\{fileID:\s*(-?\d+),\s*guid:\s*([0-9a-fA-F]{32}),\s*type:\s*3\}");
        var oldGuids = new HashSet<string>(remap.Keys.Select(k => k.Item1));
        string[] exts = { ".asset", ".prefab", ".unity", ".mat", ".controller", ".overrideController",
            ".anim", ".playable", ".mask", ".signal", ".preset", ".lighting", ".giparams", ".terrainlayer",
            ".brush", ".spriteatlas", ".fontsettings", ".guiskin", ".renderTexture", ".cubemap", ".flare",
            ".physicMaterial", ".physicsMaterial2D", ".shadervariants", ".mixer", ".inputactions" };
        int filesChanged = 0, refsChanged = 0;
        var root = Path.GetDirectoryName(Path.GetFullPath(assetsDir).TrimEnd('/'));
        var dirs = new[] { assetsDir, Path.Combine(root, "ProjectSettings") }.Where(Directory.Exists);
        foreach (var dir in dirs)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                string text;
                try { text = File.ReadAllText(file); }
                catch { continue; }
                if (!text.StartsWith("%YAML")) continue;
                bool any = false;
                foreach (var g in oldGuids)
                    if (text.Contains(g, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                if (!any) continue;
                int local = 0;
                string replaced = refRx.Replace(text, m =>
                {
                    long fid = long.Parse(m.Groups[1].Value);
                    string g = m.Groups[2].Value.ToLowerInvariant();
                    if (remap.TryGetValue((g, fid), out string ng))
                    {
                        local++;
                        return $"{{fileID: 11500000, guid: {ng}, type: 3}}";
                    }
                    if (oldGuids.Contains(g))
                    {
                        unresolved++;
                        Console.WriteLine($"  unresolved script ref {fid} in {g} ({file})");
                    }
                    return m.Value;
                });
                if (local > 0)
                {
                    File.WriteAllText(file, replaced);
                    filesChanged++;
                    refsChanged += local;
                }
            }
        }
        Console.WriteLine($"remapped {refsChanged} references in {filesChanged} files, {unresolved} unresolved");
        return 0;
    }

    static int CheckRefs(string dir, string target, string[] assemblies)
    {
        var res = new DefaultAssemblyResolver();
        res.AddSearchDirectory(dir);
        int bad = 0, ok = 0;
        bool IsTarget(TypeReference t)
        {
            if (t == null) return false;
            t = t.GetElementType();
            while (t.DeclaringType != null) t = t.DeclaringType;
            var n = t.Scope?.Name;
            return n == target || n == target + ".dll";
        }
        foreach (var a in assemblies)
        {
            string path = Path.Combine(dir, a + ".dll");
            if (!File.Exists(path)) continue;
            var m = ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = res });
            foreach (var tr in m.GetTypeReferences())
                if (IsTarget(tr))
                {
                    if (tr.Resolve() == null) { bad++; Console.WriteLine($"{a}: missing type {tr.FullName}"); }
                    else ok++;
                }
            foreach (var mr in m.GetMemberReferences())
                if (IsTarget(mr.DeclaringType))
                {
                    IMemberDefinition d = null;
                    try { d = mr.Resolve(); } catch { }
                    if (d == null) { bad++; Console.WriteLine($"{a}: missing {mr.FullName}"); }
                    else ok++;
                }
        }
        Console.WriteLine($"{target}: {ok} references resolved, {bad} missing");
        return bad == 0 ? 0 : 1;
    }

    static bool UsesRawGenericValue(MethodDefinition m, GenericParameter t)
    {
        // a parameter of type T loaded onto the stack and used directly by
        // ceq / conv.*, i.e. T treated as its underlying primitive
        if (!m.HasBody) return false;
        var ins = m.Body.Instructions;
        for (int i = 0; i + 1 < ins.Count; i++)
        {
            var op = ins[i].OpCode.Code;
            if (op != Mono.Cecil.Cil.Code.Ldarg_1 && op != Mono.Cecil.Cil.Code.Ldarg_2) continue;
            var next = ins[i + 1].OpCode.Code;
            if (next == Mono.Cecil.Cil.Code.Ceq || ins[i + 1].OpCode.Name.StartsWith("conv.")
                || (next == Mono.Cecil.Cil.Code.Ldarg_2 && i + 2 < ins.Count && ins[i + 2].OpCode.Code == Mono.Cecil.Cil.Code.Ceq))
                return m.Parameters.Any(p => p.ParameterType == t);
        }
        return false;
    }

    static int FixGenericComparers(string dll)
    {
        var bytes = File.ReadAllBytes(dll);
        var module = ModuleDefinition.ReadModule(new MemoryStream(bytes));
        var corlib = module.TypeSystem.CoreLibrary;
        int fixedCount = 0;
        foreach (var type in AllTypes(module))
        {
            if (type.GenericParameters.Count != 1) continue;
            var t = type.GenericParameters[0];
            foreach (var m in type.Methods)
            {
                bool isEquals = m.Name == "Equals" && m.Parameters.Count == 2 && m.ReturnType.MetadataType == MetadataType.Boolean;
                bool isHash = m.Name == "GetHashCode" && m.Parameters.Count == 1 && m.ReturnType.MetadataType == MetadataType.Int32;
                if (!(isEquals || isHash) || !m.Parameters.All(p => p.ParameterType == t) || !UsesRawGenericValue(m, t)) continue;

                // System.Collections.Generic.EqualityComparer<T>, scoped to the module's corlib
                var open = new TypeReference("System.Collections.Generic", "EqualityComparer`1", module, corlib);
                open.GenericParameters.Add(new GenericParameter("T", open));
                var openSelf = new GenericInstanceType(open);
                openSelf.GenericArguments.Add(open.GenericParameters[0]);
                var inst = new GenericInstanceType(open);
                inst.GenericArguments.Add(t);
                var getDefault = new MethodReference("get_Default", openSelf, inst) { HasThis = false };
                var call = new MethodReference(m.Name, isEquals ? module.TypeSystem.Boolean : module.TypeSystem.Int32, inst) { HasThis = true };
                for (int i = 0; i < m.Parameters.Count; i++)
                    call.Parameters.Add(new ParameterDefinition(open.GenericParameters[0]));

                var body = m.Body;
                body.Instructions.Clear();
                body.Variables.Clear();
                body.ExceptionHandlers.Clear();
                var il = body.GetILProcessor();
                il.Emit(Mono.Cecil.Cil.OpCodes.Call, getDefault);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_1);
                if (isEquals) il.Emit(Mono.Cecil.Cil.OpCodes.Ldarg_2);
                il.Emit(Mono.Cecil.Cil.OpCodes.Callvirt, call);
                il.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                body.MaxStackSize = 3;
                Console.WriteLine($"  rewrote {m.FullName}");
                fixedCount++;
            }
        }
        if (fixedCount > 0) module.Write(dll);
        Console.WriteLine($"{Path.GetFileName(dll)}: {fixedCount} methods rewritten");
        return 0;
    }

    // Unity reserves "Assembly-CSharp" for its own compiled scripts: a
    // precompiled plugin with that name is shadowed in the editor, its
    // MonoBehaviours are written as missing scripts (no fields) and the player
    // then reads past their data while loading the first scene.
    static int RenameAssembly(string dll, string newName, string[] searchDirs)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(dll));
        string target = Path.Combine(dir, newName + ".dll");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(dir);
        foreach (var d in searchDirs) resolver.AddSearchDirectory(d);
        var rp = new ReaderParameters { AssemblyResolver = resolver };
        var module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll)), rp);
        string oldName = module.Assembly.Name.Name;
        module.Assembly.Name.Name = newName;
        module.Name = newName + ".dll";
        module.Write(target);
        if (File.Exists(dll + ".meta")) File.Move(dll + ".meta", target + ".meta", true);
        if (!string.Equals(Path.GetFullPath(dll), target, StringComparison.OrdinalIgnoreCase)) File.Delete(dll);
        Console.WriteLine($"renamed {oldName} -> {newName}");

        foreach (var other in Directory.GetFiles(dir, "*.dll"))
        {
            if (string.Equals(other, target, StringComparison.OrdinalIgnoreCase)) continue;
            ModuleDefinition m;
            try { m = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(other)), rp); }
            catch { continue; }
            var refs = m.AssemblyReferences.Where(r => r.Name == oldName).ToList();
            if (refs.Count == 0) continue;
            foreach (var r in refs) r.Name = newName;
            m.Write(other);
            Console.WriteLine($"  updated reference in {Path.GetFileName(other)}");
        }
        return 0;
    }
}
