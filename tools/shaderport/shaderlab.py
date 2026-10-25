"""Rebuild a ShaderLab source file from a compiled Unity Shader object."""
from dxbc import parse_dxbc
from hlsl import Decls, Translator, TranslateError, HELPERS
from unityshader import read_programs

BLEND = ["Zero", "One", "DstColor", "SrcColor", "OneMinusDstColor", "SrcAlpha",
         "OneMinusSrcColor", "DstAlpha", "OneMinusDstAlpha", "SrcAlphaSaturate",
         "OneMinusSrcAlpha"]
BLENDOP = ["Add", "Sub", "RevSub", "Min", "Max", "LogicalClear", "LogicalSet",
           "LogicalCopy", "LogicalCopyInverted", "LogicalNoop", "LogicalInvert",
           "LogicalAnd", "LogicalNand", "LogicalOr", "LogicalNor", "LogicalXor",
           "LogicalEquiv", "LogicalAndReverse", "LogicalAndInverted",
           "LogicalOrReverse", "LogicalOrInverted", "Multiply", "Screen", "Overlay",
           "Darken", "Lighten", "ColorDodge", "ColorBurn", "HardLight", "SoftLight",
           "Difference", "Exclusion", "HSLHue", "HSLSaturation", "HSLColor",
           "HSLLuminosity"]
CMP = ["Off", "Never", "Less", "Equal", "LEqual", "Greater", "NotEqual", "GEqual",
       "Always"]
STENCIL_OP = ["Keep", "Zero", "Replace", "IncrSat", "DecrSat", "Invert", "IncrWrap",
              "DecrWrap"]
CULL = ["Off", "Front", "Back"]

# Keywords that only matter for XR / editor builds.
IGNORED_KEYWORDS = {"STEREO_INSTANCING_ON", "UNITY_SINGLE_PASS_STEREO",
                    "STEREO_MULTIVIEW_ON", "STEREO_CUBEMAP_RENDER_ON"}


def q(s):
    return '"%s"' % s.replace("\\", "\\\\").replace('"', '\\"')


def state_val(v, table=None, fmt=None):
    name = v.get("name", "<noninit>")
    if name and name != "<noninit>":
        return "[%s]" % name
    val = v["val"]
    if table is not None:
        i = int(val)
        return table[i] if 0 <= i < len(table) else str(i)
    if fmt:
        return fmt(val)
    return ("%g" % val)


def color_mask(v):
    name = v.get("name", "<noninit>")
    if name and name != "<noninit>":
        return "[%s]" % name
    m = int(v["val"])
    if m == 0:
        return "0"
    s = ""
    if m & 8:
        s += "R"
    if m & 4:
        s += "G"
    if m & 2:
        s += "B"
    if m & 1:
        s += "A"
    return s


def is_default(v, default):
    name = v.get("name", "<noninit>")
    return (not name or name == "<noninit>") and v["val"] == default


def props_text(pf):
    out = ["Properties {"]
    for p in pf["m_PropInfo"]["m_Props"]:
        attrs = "".join("[%s] " % a for a in p["m_Attributes"])
        flags = p["m_Flags"]
        if flags & 1:
            attrs += "[HideInInspector] "
        if flags & 2:
            attrs += "[PerRendererData] "
        if flags & 4:
            attrs += "[NoScaleOffset] "
        if flags & 8:
            attrs += "[Normal] "
        if flags & 16:
            attrs += "[HDR] "
        if flags & 32:
            attrs += "[Gamma] "
        t = p["m_Type"]
        dv = [p["m_DefValue[%d]" % i] for i in range(4)]
        desc = q(p["m_Description"])
        if t == 0:
            s = "%s (%s, Color) = (%g,%g,%g,%g)" % (p["m_Name"], desc, *dv)
        elif t == 1:
            s = "%s (%s, Vector) = (%g,%g,%g,%g)" % (p["m_Name"], desc, *dv)
        elif t == 2:
            s = "%s (%s, Float) = %g" % (p["m_Name"], desc, dv[0])
        elif t == 3:
            s = "%s (%s, Range(%g, %g)) = %g" % (p["m_Name"], desc, dv[1], dv[2], dv[0])
        elif t == 4:
            dim = {2: "2D", 3: "3D", 4: "Cube", 5: "2DArray", 6: "CubeArray"}.get(
                p["m_DefTexture"]["m_TexDim"], "2D")
            dn = p["m_DefTexture"]["m_DefaultName"] or "white"
            s = "%s (%s, %s) = %s {}" % (p["m_Name"], desc, dim, q(dn))
        elif t == 5:
            s = "%s (%s, Int) = %d" % (p["m_Name"], desc, int(dv[0]))
        else:
            continue
        out.append("    " + attrs + s)
    out.append("}")
    return "\n".join(out)


def tags_text(tags, indent):
    if not tags:
        return ""
    items = " ".join("%s=%s" % (q(k), q(v)) for k, v in tags)
    return " " * indent + "Tags { %s }\n" % items


def state_text(st, indent):
    pad = " " * indent
    out = []
    rt0 = st["rtBlend0"]
    src, dst = rt0["srcBlend"], rt0["destBlend"]
    srca, dsta = rt0["srcBlendAlpha"], rt0["destBlendAlpha"]
    if not (is_default(src, 1) and is_default(dst, 0) and is_default(srca, 1)
            and is_default(dsta, 0)):
        if is_default(srca, src["val"]) and is_default(dsta, dst["val"]) and \
                srca.get("name") == src.get("name") and dsta.get("name") == dst.get("name"):
            out.append("Blend %s %s" % (state_val(src, BLEND), state_val(dst, BLEND)))
        else:
            out.append("Blend %s %s, %s %s" % (state_val(src, BLEND), state_val(dst, BLEND),
                                              state_val(srca, BLEND), state_val(dsta, BLEND)))
    bo, boa = rt0["blendOp"], rt0["blendOpAlpha"]
    if not (is_default(bo, 0) and is_default(boa, 0)):
        if bo == boa:
            out.append("BlendOp %s" % state_val(bo, BLENDOP))
        else:
            out.append("BlendOp %s, %s" % (state_val(bo, BLENDOP), state_val(boa, BLENDOP)))
    if not is_default(rt0["colMask"], 15):
        out.append("ColorMask %s" % color_mask(rt0["colMask"]))
    if st.get("rtSeparateBlend"):
        for i in range(1, 8):
            rt = st.get("rtBlend%d" % i)
            if rt is None:
                continue
            if not (is_default(rt["srcBlend"], 1) and is_default(rt["destBlend"], 0)):
                out.append("Blend %d %s %s, %s %s" % (
                    i, state_val(rt["srcBlend"], BLEND), state_val(rt["destBlend"], BLEND),
                    state_val(rt["srcBlendAlpha"], BLEND),
                    state_val(rt["destBlendAlpha"], BLEND)))
            if not is_default(rt["colMask"], 15):
                out.append("ColorMask %s %d" % (color_mask(rt["colMask"]), i))
    if not is_default(st["zTest"], 4):
        out.append("ZTest %s" % state_val(st["zTest"], CMP))
    if not is_default(st["zWrite"], 1):
        out.append("ZWrite %s" % state_val(st["zWrite"], ["Off", "On"]))
    if not is_default(st["culling"], 2):
        out.append("Cull %s" % state_val(st["culling"], CULL))
    if "zClip" in st and not is_default(st["zClip"], 1):
        out.append("ZClip %s" % state_val(st["zClip"], ["False", "True"]))
    if not (is_default(st["offsetFactor"], 0) and is_default(st["offsetUnits"], 0)):
        out.append("Offset %s, %s" % (state_val(st["offsetFactor"]),
                                      state_val(st["offsetUnits"])))
    if not is_default(st["alphaToMask"], 0):
        out.append("AlphaToMask %s" % state_val(st["alphaToMask"], ["Off", "On"]))
    # stencil
    sref, rmask, wmask = st["stencilRef"], st["stencilReadMask"], st["stencilWriteMask"]
    front, back, both = st["stencilOpFront"], st["stencilOpBack"], st["stencilOp"]

    def op_default(o):
        return (is_default(o["pass"], 0) and is_default(o["fail"], 0) and
                is_default(o["zFail"], 0) and is_default(o["comp"], 8))
    if not (is_default(sref, 0) and is_default(rmask, 255) and is_default(wmask, 255)
            and op_default(front) and op_default(back) and op_default(both)):
        s = ["Stencil {"]
        s.append("    Ref %s" % state_val(sref, fmt=lambda v: "%d" % v))
        s.append("    ReadMask %s" % state_val(rmask, fmt=lambda v: "%d" % v))
        s.append("    WriteMask %s" % state_val(wmask, fmt=lambda v: "%d" % v))
        if op_default(front) and op_default(back):
            o = both
            s.append("    Comp %s" % state_val(o["comp"], CMP))
            s.append("    Pass %s" % state_val(o["pass"], STENCIL_OP))
            s.append("    Fail %s" % state_val(o["fail"], STENCIL_OP))
            s.append("    ZFail %s" % state_val(o["zFail"], STENCIL_OP))
        else:
            for suf, o in (("Front", front), ("Back", back)):
                s.append("    Comp%s %s" % (suf, state_val(o["comp"], CMP)))
                s.append("    Pass%s %s" % (suf, state_val(o["pass"], STENCIL_OP)))
                s.append("    Fail%s %s" % (suf, state_val(o["fail"], STENCIL_OP)))
                s.append("    ZFail%s %s" % (suf, state_val(o["zFail"], STENCIL_OP)))
        s.append("}")
        out.extend(s)
    return "".join(pad + line + "\n" for line in out)


def keyword_sets(variant_kw_lists):
    """Group keywords into multi_compile sets (keywords never used together
    can share one set)."""
    kws = []
    for l in variant_kw_lists:
        for k in l:
            if k not in kws:
                kws.append(k)
    together = {k: set() for k in kws}
    for l in variant_kw_lists:
        for a in l:
            for b in l:
                if a != b:
                    together[a].add(b)
    sets = []
    for k in kws:
        for s in sets:
            if not any(o in together[k] for o in s):
                s.append(k)
                break
        else:
            sets.append([k])
    return sets


def variant_dispatch(variants, fn_prefix, alias):
    """#if chain selecting the best matching variant implementation."""
    order = sorted(range(len(variants)), key=lambda i: -len(variants[i][0]))
    out = []
    first = True
    default = None
    for i in order:
        kws, _ = variants[i]
        if not kws:
            default = i
            continue
        cond = " && ".join("defined(%s)" % k for k in kws)
        out.append("#%s %s" % ("if" if first else "elif", cond))
        out.append("#define %s %s_%d" % (alias, fn_prefix, i))
        first = False
    if default is None:
        default = order[-1]
    if first:
        out.append("#define %s %s_%d" % (alias, fn_prefix, default))
    else:
        out.append("#else")
        out.append("#define %s %s_%d" % (alias, fn_prefix, default))
        out.append("#endif")
    return "\n".join(out)


def pass_program(shader, pf, pass_, pass_id, warnings):
    kwnames = pf["m_KeywordNames"]
    stages = []
    for key, stage in (("progVertex", "vs"), ("progFragment", "ps")):
        subs = read_programs(shader, pass_, key, kwnames)
        if not subs:
            return None
        stages.append((stage, subs))
    for key in ("progGeometry", "progHull", "progDomain"):
        if read_programs(shader, pass_, key, kwnames):
            raise TranslateError("geometry/tessellation stages are not supported")
    decls = Decls()
    bodies = []
    all_kw = []
    dispatch = []
    for stage, subs in stages:
        variants = []
        for i, sp in enumerate(subs):
            kws = [k for k in sp.keywords if k not in IGNORED_KEYWORDS]
            if any(sorted(kws) == sorted(v[0]) for v in variants):
                continue
            prog = parse_dxbc(sp.dxbc)
            prefix = "p%d_%s_%d" % (pass_id, stage, len(variants))
            code = Translator(prog, sp.bindings, prefix, decls).build()
            bodies.append(code)
            variants.append((kws, prefix))
            all_kw.append(kws)
        dispatch.append(variant_dispatch(variants, "p%d_%s" % (pass_id, stage),
                                         "SP_%s_ENTRY" % stage.upper()))
        # struct aliases needed by the wrapper entry points
    sets = keyword_sets(all_kw)
    lines = ["HLSLPROGRAM", "#pragma vertex sp_vert", "#pragma fragment sp_frag",
             "#pragma target 3.5"]
    for s in sets:
        lines.append("#pragma multi_compile __ %s" % " ".join(s))
    lines.append(decls.text())
    for h in sorted(decls.helpers):
        if h in HELPERS:
            lines.append(HELPERS[h])
    lines.extend(bodies)
    lines.extend(dispatch)
    lines.append("#define SP_CAT2(a, b) a##b")
    lines.append("#define SP_CAT(a, b) SP_CAT2(a, b)")
    lines.append("SP_CAT(SP_VS_ENTRY, _out) sp_vert(SP_CAT(SP_VS_ENTRY, _in) i) "
                 "{ return SP_VS_ENTRY(i); }")
    lines.append("SP_CAT(SP_PS_ENTRY, _out) sp_frag(SP_CAT(SP_PS_ENTRY, _in) i) "
                 "{ return SP_PS_ENTRY(i); }")
    lines.append("ENDHLSL")
    return "\n".join(lines)


def convert_shader(shader):
    """Return ShaderLab source text for a Unity Shader typetree dict."""
    pf = shader["m_ParsedForm"]
    warnings = []
    out = ["// Regenerated for Metal from the D3D11 bytecode by tools/shaderport",
           "Shader %s {" % q(pf["m_Name"])]
    out.append(props_text(pf))
    pass_id = 0
    for ss in pf["m_SubShaders"]:
        out.append("SubShader {")
        tags = ss["m_Tags"]["tags"]
        out.append(tags_text(tags, 1).rstrip("\n"))
        if ss.get("m_LOD"):
            out.append(" LOD %d" % ss["m_LOD"])
        for p in ss["m_Passes"]:
            ptype = p["m_Type"]
            if ptype == 1:  # UsePass
                out.append(" UsePass %s" % q(p["m_UseName"]))
                continue
            if ptype == 2:  # GrabPass
                tn = p["m_TextureName"]
                out.append(" GrabPass { %s }" % (q(tn) if tn else ""))
                continue
            out.append(" Pass {")
            st = p["m_State"]
            if st.get("m_Name"):
                out.append("  Name %s" % q(st["m_Name"]))
            ptags = st["m_Tags"]["tags"] if "m_Tags" in st else []
            if p.get("m_Tags") and p["m_Tags"].get("tags"):
                ptags = p["m_Tags"]["tags"]
            t = tags_text(ptags, 2)
            if t:
                out.append(t.rstrip("\n"))
            if st.get("m_LOD"):
                out.append("  LOD %d" % st["m_LOD"])
            out.append(state_text(st, 2).rstrip("\n"))
            prog = pass_program(shader, pf, p, pass_id, warnings)
            pass_id += 1
            if prog is None:
                raise TranslateError("pass without vertex/fragment programs")
            out.append(prog)
            out.append(" }")
        out.append("}")
    if pf.get("m_FallbackName"):
        out.append("Fallback %s" % q(pf["m_FallbackName"]))
    else:
        out.append("Fallback Off")
    out.append("}")
    return "\n".join(l for l in out if l.strip() != ""), warnings
