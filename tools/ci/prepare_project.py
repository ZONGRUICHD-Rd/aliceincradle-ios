#!/usr/bin/env python3
"""Merge an AssetRipper export of the Windows build into the iOS Unity project.

Usage:
  prepare_project.py --export <ExportedProject> --project <unity project>
                     --game-data <AliceInCradle_Data> --dlls <dir with replacement DLLs>
                     --shaders <dir produced by port_shaders.py> --work <scratch dir>

Outputs (in --work):
  manifests/       original bundle manifests (relative layout kept)
  shader_map.tsv   placeholder shader asset path -> built-in shader name
"""
import argparse
import json
import os
import re
import shutil
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "shaderport"))
from port_shaders import is_builtin  # noqa: E402

# Assemblies provided by UPM packages in the iOS project. Their exported DLLs
# are removed after script references have been remapped to package sources.
PACKAGE_ASSEMBLIES = [
    "UnityEngine.UI",
    "Unity.InputSystem",
    "Unity.InputSystem.ForUI",
    "Unity.RenderPipelines.Core.Runtime",
    "Unity.RenderPipelines.Core.ShaderLibrary",
    "Unity.RenderPipelines.Universal.Runtime",
    "Unity.RenderPipelines.Universal.Config.Runtime",
    "Unity.RenderPipelines.Universal.Shaders",
    "Unity.RenderPipelines.Universal.2D.Internal",
    "Unity.RenderPipeline.Universal.ShaderLibrary",
    "Unity.RenderPipelines.ShaderGraph.ShaderGraphLibrary",
    "Unity.Burst",
    "Unity.Burst.Unsafe",
    "Unity.Mathematics",
]

# Windows-only assemblies nothing needs once SFB is replaced.
REMOVE_ASSEMBLIES = ["Ookii.Dialogs", "System.Windows.Forms"]

# Our managed replacements (built from /managed).
REPLACED_ASSEMBLIES = ["CriMw.CriWare.Runtime", "SFB"]


def log(msg):
    print("[prepare] " + msg, flush=True)


def copytree_merge(src, dst, skip=()):
    for root, dirs, files in os.walk(src):
        rel = os.path.relpath(root, src)
        if rel != "." and any(rel == s or rel.startswith(s + os.sep) for s in skip):
            dirs[:] = []
            continue
        os.makedirs(os.path.join(dst, rel), exist_ok=True)
        for f in files:
            s = os.path.join(root, f)
            d = os.path.join(dst, rel, f)
            shutil.copy2(s, d)


def shader_name_of(path):
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            head = f.read(4096)
    except OSError:
        return None
    m = re.search(r'Shader\s+"([^"]+)"', head)
    return m.group(1) if m else None


def fix_plugin_metas(plugins_dir):
    n = 0
    for f in os.listdir(plugins_dir):
        if not f.endswith(".dll.meta"):
            continue
        p = os.path.join(plugins_dir, f)
        with open(p, encoding="utf-8") as fh:
            text = fh.read()
        new = re.sub(r"validateReferences:\s*1", "validateReferences: 0", text)
        if "validateReferences" not in new and "PluginImporter:" in new:
            new = new.replace("PluginImporter:", "PluginImporter:\n  validateReferences: 0", 1)
        if new != text:
            with open(p, "w", encoding="utf-8") as fh:
                fh.write(new)
            n += 1
    log("plugin metas updated: %d" % n)


RENAMABLE = {".bytes", ".txt", ".json", ".csv", ".xml", ".html", ".htm", ".yaml", ".fnt", ".ttf", ".otf"}


def fix_bundle_asset_names(project, game_sa):
    """Give exported bundle assets their original file names/extensions.

    The game loads some bundle assets by full path (original extension
    included); AssetRipper may have exported e.g. "foo.csv" as "foo.bytes".
    """
    index = {}
    assets_root = os.path.join(project, "Assets")
    for root, _dirs, files in os.walk(assets_root):
        rel = os.path.relpath(root, project).replace(os.sep, "/").lower()
        index[rel] = (root, files)
    renamed = missing = 0
    for root, _dirs, files in os.walk(game_sa):
        for fn in files:
            if not fn.endswith(".manifest"):
                continue
            in_assets = False
            with open(os.path.join(root, fn), encoding="utf-8", errors="replace") as f:
                for line in f:
                    if line.startswith("Assets:"):
                        in_assets = True
                        continue
                    if not in_assets:
                        continue
                    if not line.startswith("- "):
                        if line.strip() and not line.startswith(" "):
                            in_assets = False
                        continue
                    apath = line[2:].strip()
                    d, name = apath.rsplit("/", 1)
                    entry = index.get(d.lower())
                    if entry is None:
                        missing += 1
                        continue
                    real_dir, dfiles = entry
                    lower = {x.lower(): x for x in dfiles}
                    if name.lower() in lower:
                        continue
                    stem, ext = os.path.splitext(name)
                    if ext.lower() not in RENAMABLE:
                        continue
                    cands = [name.lower() + e for e in (".bytes", ".txt")] + [stem.lower() + e for e in RENAMABLE]
                    for c in cands:
                        if c in lower:
                            src = os.path.join(real_dir, lower[c])
                            dst = os.path.join(real_dir, name)
                            os.rename(src, dst)
                            if os.path.exists(src + ".meta"):
                                os.rename(src + ".meta", dst + ".meta")
                            dfiles.remove(lower[c])
                            dfiles.append(name)
                            renamed += 1
                            break
    log("bundle assets renamed to original names: %d (manifest dirs missing: %d)" % (renamed, missing))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--export", required=True)
    ap.add_argument("--project", required=True)
    ap.add_argument("--game-data", required=True)
    ap.add_argument("--dlls", required=True)
    ap.add_argument("--shaders", required=True)
    ap.add_argument("--work", required=True)
    a = ap.parse_args()

    assets_src = os.path.join(a.export, "Assets")
    assets = os.path.join(a.project, "Assets")
    os.makedirs(a.work, exist_ok=True)

    # 1. game assets ------------------------------------------------------
    log("copying exported assets")
    copytree_merge(assets_src, assets, skip=("IOSPort",))

    fix_bundle_asset_names(a.project, os.path.join(a.game_data, "StreamingAssets"))

    # 2. project settings (keep our ProjectVersion / package manifest) -----
    ps_src = os.path.join(a.export, "ProjectSettings")
    ps_dst = os.path.join(a.project, "ProjectSettings")
    if os.path.isdir(ps_src):
        for f in os.listdir(ps_src):
            if f in ("ProjectVersion.txt",):
                continue
            shutil.copy2(os.path.join(ps_src, f), os.path.join(ps_dst, f))
    log("project settings: %s" % ", ".join(sorted(os.listdir(ps_dst))))

    # 3. plugins -------------------------------------------------------------
    plugins = os.path.join(assets, "Plugins")
    os.makedirs(plugins, exist_ok=True)
    for name in REPLACED_ASSEMBLIES:
        src = os.path.join(a.dlls, name + ".dll")
        if not os.path.exists(src):
            sys.exit("missing replacement assembly " + src)
        shutil.copy2(src, os.path.join(plugins, name + ".dll"))
        log("replaced %s.dll" % name)
    for name in REMOVE_ASSEMBLIES:
        for ext in (".dll", ".dll.meta"):
            p = os.path.join(plugins, name + ext)
            if os.path.exists(p):
                os.remove(p)
                log("removed " + name + ext)
    # native Windows plugins are useless on iOS
    for sub in ("x86_64", "x86", "Windows"):
        p = os.path.join(plugins, sub)
        if os.path.isdir(p):
            shutil.rmtree(p)
            if os.path.exists(p + ".meta"):
                os.remove(p + ".meta")
    fix_plugin_metas(plugins)
    with open(os.path.join(a.work, "package_assemblies.txt"), "w") as f:
        f.write("\n".join(PACKAGE_ASSEMBLIES) + "\n")

    # 4. shaders ---------------------------------------------------------------
    with open(os.path.join(a.shaders, "index.json")) as f:
        generated = json.load(f)["shaders"]
    replaced = 0
    placeholders = []
    seen_generated = set()
    for root, _dirs, files in os.walk(assets):
        if os.sep + "IOSPort" in root:
            continue
        for fn in files:
            if not fn.endswith(".shader"):
                continue
            p = os.path.join(root, fn)
            name = shader_name_of(p)
            if not name:
                continue
            if name in generated:
                shutil.copyfile(os.path.join(a.shaders, generated[name]), p)
                replaced += 1
                seen_generated.add(name)
            elif is_builtin(name):
                placeholders.append((os.path.relpath(p, a.project).replace(os.sep, "/"), name))
    # generated shaders that had no exported counterpart (only found via Shader.Find)
    extra_dir = os.path.join(assets, "IOSPort", "Shaders")
    os.makedirs(extra_dir, exist_ok=True)
    extra = 0
    for name, fn in generated.items():
        if name not in seen_generated:
            shutil.copyfile(os.path.join(a.shaders, fn), os.path.join(extra_dir, fn))
            extra += 1
    with open(os.path.join(a.work, "shader_map.tsv"), "w", encoding="utf-8") as f:
        for path, name in placeholders:
            f.write("%s\t%s\n" % (path, name))
    log("shaders: %d replaced in place, %d added, %d built-in placeholders" % (replaced, extra, len(placeholders)))

    # 5. streaming assets --------------------------------------------------
    sa = os.path.join(assets, "StreamingAssets")
    game_sa = os.path.join(a.game_data, "StreamingAssets")
    if not os.path.isdir(sa):
        log("export has no StreamingAssets, copying from game data")
        shutil.copytree(game_sa, sa)
    manifests = os.path.join(a.work, "manifests")
    nb = 0
    for root, _dirs, files in os.walk(sa):
        for fn in files:
            if not fn.endswith(".manifest"):
                continue
            mp = os.path.join(root, fn)
            rel = os.path.relpath(mp, sa)
            os.makedirs(os.path.join(manifests, os.path.dirname(rel)), exist_ok=True)
            shutil.move(mp, os.path.join(manifests, rel))
            if os.path.exists(mp + ".meta"):
                os.remove(mp + ".meta")
            bundle = mp[: -len(".manifest")]
            for ext in ("", ".meta"):
                if os.path.exists(bundle + ext):
                    os.remove(bundle + ext)
            nb += 1
    log("moved %d bundle manifests, removed Windows bundles" % nb)
    debug = os.path.join(sa, "_debug.txt")
    if os.path.exists(debug):
        with open(debug, encoding="utf-8") as f:
            text = f.read()
        # iOS: use the game's pixel shader fallback instead of compute shaders
        text = re.sub(r"(?m)^nocomputeshader\s+\d+", "nocomputeshader 1", text)
        if "nocomputeshader" not in text:
            text += "\nnocomputeshader 1\n"
        with open(debug, "w", encoding="utf-8") as f:
            f.write(text)
    sha = os.path.join(sa, "_sha.txt")
    if os.path.exists(sha):
        # file hashes of the Windows executable files, meaningless on iOS
        open(sha, "w").close()
    for junk in ("osx_reveal.scpt",):
        p = os.path.join(sa, junk)
        if os.path.exists(p):
            os.remove(p)
    log("done")


if __name__ == "__main__":
    main()
