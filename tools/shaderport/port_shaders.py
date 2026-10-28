#!/usr/bin/env python3
"""Regenerate the game's custom shaders as ShaderLab/HLSL sources.

Usage: port_shaders.py <AliceInCradle_Data> <out_dir> [--check]

For every Shader found in the game's asset bundles and data files whose name
is not provided by Unity / URP itself, a `.shader` file is written into
<out_dir>.  `index.json` maps shader names to the generated files so the
Unity-side importer can replace AssetRipper's dummy shaders.
"""
import glob
import json
import os
import re
import subprocess
import sys
import tempfile
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import UnityPy  # noqa: E402

from shaderlab import convert_shader  # noqa: E402

# Shaders shipped with Unity / URP / packages: the real sources are used.
BUILTIN_PREFIXES = (
    "Hidden/Universal Render Pipeline/", "Hidden/Universal/", "Universal Render Pipeline/",
    "Hidden/Core/", "Hidden/TerrainEngine/", "Legacy Shaders/", "Sprites/",
    "UI/", "Hidden/Internal", "Skybox/", "Particles/", "Mobile/", "Unlit/",
    "Hidden/Shadow2D", "Hidden/ShadowProjected2D",
    "Hidden/Light2D", "Hidden/Light2d", "Hidden/InternalErrorShader",
    "Hidden/BlitCopy", "Hidden/VideoDecode", "Hidden/Compositing",
    "Hidden/BlitToDepth", "Hidden/CubeBlur", "Hidden/CubeCopy",
    "Hidden/ConvertTexture", "Hidden/FrameDebuggerRenderTargetDisplay",
)
BUILTIN_NAMES = {"Standard", "Standard (Specular setup)", "Autodesk Interactive",
                 "GUI/Text Shader", "VR/SpatialMapping/Wireframe", "Nature/SpeedTree"}


def is_builtin(name):
    return name in BUILTIN_NAMES or name.startswith(BUILTIN_PREFIXES)


def safe_name(name):
    return re.sub(r"[^A-Za-z0-9_.-]+", "_", name).strip("_")


def iter_files(data_dir):
    names = ["globalgamemanagers.assets", "resources.assets"]
    names += ["sharedassets%d.assets" % i for i in range(64)]
    for n in names:
        p = os.path.join(data_dir, n)
        if os.path.exists(p):
            yield p
    sa = os.path.join(data_dir, "StreamingAssets")
    for p in sorted(glob.glob(os.path.join(sa, "**", "*.dat"), recursive=True)):
        yield p


def hlsl_blocks(text):
    return re.findall(r"HLSLPROGRAM\n(.*?)ENDHLSL", text, re.S)


def check_hlsl(text, name):
    """Compile every program with glslang's HLSL front-end (syntax/type check)."""
    errors = []
    for bi, block in enumerate(hlsl_blocks(text)):
        kw = re.findall(r"#pragma multi_compile __ (.*)", block)
        src = "\n".join(l for l in block.splitlines() if not l.startswith("#pragma"))
        # check the default variant and one variant per keyword set member
        combos = [[]] + [[k] for line in kw for k in line.split()]
        for combo in combos[:6]:
            with tempfile.TemporaryDirectory() as td:
                fn = os.path.join(td, "s.hlsl")
                with open(fn, "w") as f:
                    f.write("".join("#define %s 1\n" % k for k in combo))
                    f.write(src)
                for stage, entry in (("vert", "sp_vert"), ("frag", "sp_frag")):
                    r = subprocess.run(["glslangValidator", "-D", "-S", stage, "-e", entry,
                                        "-V", "-o", os.path.join(td, "o.spv"), fn],
                                       capture_output=True, text=True)
                    if r.returncode != 0:
                        msg = "\n".join(l for l in r.stdout.splitlines() if "ERROR" in l)
                        errors.append("%s pass%d %s %s:\n%s" % (name, bi, stage, combo, msg))
    return errors


def main():
    data_dir, out_dir = sys.argv[1], sys.argv[2]
    check = "--check" in sys.argv
    os.makedirs(out_dir, exist_ok=True)
    index = {}
    failed = {}
    check_errors = []
    for path in iter_files(data_dir):
        try:
            env = UnityPy.load(path)
        except Exception as e:  # noqa: BLE001
            print("skip %s: %s" % (path, e))
            continue
        for obj in env.objects:
            if obj.type.name != "Shader":
                continue
            try:
                tree = obj.read_typetree()
            except Exception as e:  # noqa: BLE001
                print("cannot read shader in %s: %s" % (path, e))
                continue
            if "m_ParsedForm" not in tree:
                continue
            name = tree["m_ParsedForm"]["m_Name"]
            if is_builtin(name) or name in index:
                continue
            try:
                text, _warn = convert_shader(tree)
            except Exception as e:  # noqa: BLE001
                failed[name] = "%s: %s" % (type(e).__name__, e)
                if "-v" in sys.argv:
                    traceback.print_exc()
                continue
            fn = safe_name(name) + ".shader"
            with open(os.path.join(out_dir, fn), "w") as f:
                f.write(text)
            index[name] = fn
            if check:
                check_errors.extend(check_hlsl(text, name))
    with open(os.path.join(out_dir, "index.json"), "w") as f:
        json.dump({"shaders": index, "failed": failed}, f, indent=1, sort_keys=True)
    print("converted %d shaders, %d failed" % (len(index), len(failed)))
    for k, v in sorted(failed.items()):
        print("  FAILED %s: %s" % (k, v))
    if check:
        print("%d check errors" % len(check_errors))
        for e in check_errors[:40]:
            print(e)
    return 1 if failed or check_errors else 0


if __name__ == "__main__":
    sys.exit(main())
