"""Translate a parsed DXBC program back into compilable HLSL.

The output keeps the register-level structure of the bytecode (temporaries
are float4 r#, integer instructions reinterpret bits with asint/asuint) but
gives every constant, texture and sampler its original Unity property name so
that Unity can bind material values to the regenerated shader on Metal.
"""
import math
import struct

from dxbc import (OP, O_TEMP, O_INPUT, O_OUTPUT, O_INDEXABLE_TEMP, O_IMM32,
                  O_SAMPLER, O_RESOURCE, O_CB, O_ICB, O_NULL, O_OUTPUT_DEPTH,
                  O_INPUT_PRIMITIVEID, O_INPUT_COVERAGE_MASK,
                  O_OUTPUT_COVERAGE_MASK, O_OUTPUT_DEPTH_GE, O_OUTPUT_DEPTH_LE,
                  RESOURCE_DIMS, INTERP_MODES)

SW = "xyzw"

# Constant buffers whose layout is fixed by Unity and must be reproduced.
BUILTIN_CBUFFERS = {"UnityPerDraw", "UnityPerFrame", "UnityPerCamera",
                    "UnityPerMaterial", "UnityLighting", "UnityShadows",
                    "UnityFog", "UnityStereoGlobals", "UnityPerDrawRare",
                    "UnityMetaPass", "UnityLightingSettings"}



_FILTERS = {0: "Point", 1: "Linear", 2: "Trilinear"}
_WRAPS = {0: "Repeat", 1: "Clamp", 2: "Mirror", 3: "MirrorOnce"}


def inline_sampler_name(state, compare=False):
    """Name of an inline sampler from Unity's packed sampler state.

    bits 0-1 filter, 2-3 / 4-5 / 6-7 wrap mode of U / V / W; checked against
    URP's sampler_PointClamp (84), sampler_LinearClamp (85),
    sampler_PointRepeat (0) and sampler_LinearRepeat (1).
    """
    if isinstance(state, str):
        return state
    state = int(state)
    flt = _FILTERS.get(state & 3, "Linear")
    wu, wv, ww = (state >> 2) & 3, (state >> 4) & 3, (state >> 6) & 3
    if wu == wv == ww:
        wrap = _WRAPS[wu]
    else:
        wrap = "%sU_%sV_%sW" % (_WRAPS[wu], _WRAPS[wv], _WRAPS[ww])
    return "sampler_%s%s%s" % (flt, wrap, "Compare" if compare else "")

class TranslateError(Exception):
    pass


def f32(bits):
    return struct.unpack("<f", struct.pack("<I", bits & 0xFFFFFFFF))[0]


def fmt_float(bits):
    """Format raw bits as an HLSL float literal that round-trips exactly."""
    bits &= 0xFFFFFFFF
    v = f32(bits)
    if math.isnan(v) or math.isinf(v) or (v != 0.0 and abs(v) < 1.1754944e-38) \
            or bits == 0x80000000:
        return "asfloat(0x%08Xu)" % bits
    for prec in range(1, 10):
        s = "%.*g" % (prec, v)
        if struct.unpack("<I", struct.pack("<f", float(s)))[0] == bits:
            break
    if "e" not in s and "." not in s and "n" not in s:
        s += ".0"
    return s


def fmt_int(bits):
    bits &= 0xFFFFFFFF
    v = bits - (1 << 32) if bits & 0x80000000 else bits
    if v == -2147483648:
        return "(-2147483647 - 1)"
    return str(v)


def fmt_uint(bits):
    return "%du" % (bits & 0xFFFFFFFF)


def vec_type(base, n):
    return base if n == 1 else "%s%d" % (base, n)


class Decls:
    """Declarations shared by every variant of one pass."""

    def __init__(self):
        self.globals = {}          # name -> decl string
        self.cbuffers = {}         # cbname -> {offset: Param}
        self.textures = {}         # name -> type string
        self.samplers = {}         # name -> decl
        self.helpers = set()

    def text(self):
        out = []
        for cbname, params in sorted(self.cbuffers.items()):
            out.append("cbuffer %s {" % cbname)
            cursor = 0
            pad = 0
            for off in sorted(params):
                p = params[off]
                if off < cursor:
                    continue  # overlapping (shouldn't happen)
                while cursor < off:
                    # pad up to the next offset without crossing 16B lines
                    line_left = 16 - (cursor % 16)
                    gap = min(off - cursor, line_left)
                    n = gap // 4
                    out.append("    %s _pad%d_%s;" % (vec_type("float", n), pad,
                                                      cbname.replace("$", "")))
                    pad += 1
                    cursor += gap
                out.append("    %s;" % param_decl(p))
                cursor = off + param_size(p)
            out.append("};")
        for name in sorted(self.globals):
            out.append(self.globals[name])
        for name in sorted(self.textures):
            out.append("%s %s;" % (self.textures[name], name))
        for name in sorted(self.samplers):
            out.append(self.samplers[name])
        return "\n".join(out)


def param_type(p):
    base = {0: "float", 1: "int", 2: "bool", 3: "half"}.get(p.type, "float")
    if base in ("bool", "half"):
        base = "float" if base == "half" else "int"
    if p.is_matrix:
        return "%s%dx%d" % (base, p.rows, p.cols)
    return vec_type(base, p.cols)


def param_decl(p):
    s = "%s %s" % (param_type(p), p.name)
    if p.array_size:
        s += "[%d]" % p.array_size
    return s


def param_size(p):
    """Bytes occupied in a cbuffer using HLSL packing rules."""
    if p.is_matrix:
        one = 16 * (p.cols - 1) + 4 * p.rows
        if p.array_size:
            return 16 * p.cols * (p.array_size - 1) + one
        return one
    if p.array_size:
        return 16 * (p.array_size - 1) + 4 * p.cols
    return 4 * p.cols


class Translator:
    def __init__(self, prog, bindings, prefix, decls):
        self.prog = prog
        self.b = bindings
        self.prefix = prefix
        self.decls = decls
        self.lines = []
        self.indent = 1
        self.ntemps = 0
        self.indexable = {}
        self.icb = None
        self.resources = {}       # slot -> (dim, ret)
        self.sampler_modes = {}
        self.in_regs = {}         # reg -> list of SigElement
        self.out_regs = {}
        self.cb_globals = set()
        self.used_depth = False
        self.depth_kind = None
        self.uses_coverage_out = False

    # ------------------------------------------------------------------
    def emit(self, s):
        self.lines.append("    " * self.indent + s)

    # ------------------------------------------------------------------
    # constant buffer lookup
    def cb_param_at(self, cbname, byte_off):
        cb = self.b.cbuffers.get(cbname)
        if cb is None:
            return None
        for p in cb.params.values():
            if p.offset <= byte_off < p.offset + param_size(p):
                return p
        return None

    def declare_param(self, cbname, p):
        if cbname in BUILTIN_CBUFFERS:
            d = self.decls.cbuffers.setdefault(cbname, {})
            old = d.get(p.offset)
            if old is not None and old.name != p.name:
                # layout conflict: fall back to a plain global
                self.decls.globals.setdefault(p.name, param_decl(p) + ";")
                return
            # avoid overlaps with differently-sized entries
            for o2, p2 in list(d.items()):
                if p2.name == p.name and o2 != p.offset:
                    return
            d[p.offset] = p
        else:
            self.decls.globals.setdefault(p.name, param_decl(p) + ";")

    def cb_component(self, cbname, reg, comp, rel_expr):
        """Expression (float typed scalar) for one component of cbN[reg]."""
        off = reg * 16 + comp * 4
        p = self.cb_param_at(cbname, off)
        if p is None:
            return "0.0"
        self.declare_param(cbname, p)
        rel = (off - p.offset)
        if p.is_matrix:
            col_regs = p.cols
            mat_bytes = 16 * p.cols
            idx = ""
            if p.array_size:
                if rel_expr:
                    idx = "[(%s + %d) / %d]" % (rel_expr, rel // 16, p.cols)
                else:
                    idx = "[%d]" % (rel // mat_bytes)
                rel %= mat_bytes
            col = rel // 16
            row = (rel % 16) // 4
            if row >= p.rows:
                return "0.0"
            if rel_expr and p.array_size:
                # column depends on the dynamic index as well
                return "%s%s[%d][(%s + %d) %% %d]" % (p.name, idx, row, rel_expr,
                                                       rel // 16, p.cols)
            e = "%s%s._m%d%d" % (p.name, idx, row, col)
        else:
            comp_in = (rel % 16) // 4
            if p.array_size:
                if rel_expr:
                    e = "%s[%s + %d]" % (p.name, rel_expr, rel // 16)
                else:
                    e = "%s[%d]" % (p.name, rel // 16)
            else:
                e = p.name
            if p.cols > 1:
                e += "." + SW[comp_in]
        if p.type in (1, 2):
            e = "asfloat(%s)" % e
        return e

    # ------------------------------------------------------------------
    def index_expr(self, idx):
        imm, rel = idx
        if rel is None:
            return None, imm
        r = self.src(rel, 1, "i", (rel.comps()[0],))
        return r, imm

    def reg_name(self, op):
        t = op.type
        if t == O_TEMP:
            return "r%d" % op.indices[0][0]
        if t == O_INPUT:
            if len(op.indices) > 1:
                raise TranslateError("multi-dim input")
            rel, imm = self.index_expr(op.indices[0])
            if rel:
                raise TranslateError("dynamic input indexing")
            return "v%d" % imm
        if t == O_OUTPUT:
            rel, imm = self.index_expr(op.indices[0])
            if rel:
                raise TranslateError("dynamic output indexing")
            return "o%d" % imm
        if t == O_INDEXABLE_TEMP:
            reg = op.indices[0][0]
            rel, imm = self.index_expr(op.indices[1])
            if rel:
                return "x%d[%s + %d]" % (reg, rel, imm)
            return "x%d[%d]" % (reg, imm)
        if t in (O_OUTPUT_DEPTH, O_OUTPUT_DEPTH_GE, O_OUTPUT_DEPTH_LE):
            self.used_depth = True
            self.depth_kind = t
            return "oDepth"
        if t == O_OUTPUT_COVERAGE_MASK:
            self.uses_coverage_out = True
            return "oMask"
        raise TranslateError("unsupported register type %d" % t)

    def src(self, op, n, kind, dest_comps=None):
        """Source operand expression with `n` components of type kind (f/i/u).

        dest_comps: which of the 4 swizzle slots to take (default first n).
        """
        comps = op.comps()
        if dest_comps is None:
            dest_comps = tuple(range(n))
        sel = [comps[c] for c in dest_comps]
        t = op.type
        if t == O_IMM32:
            vals = op.imm
            if len(vals) == 1:
                vals = vals * 4
            parts = []
            for c in dest_comps:
                bits = vals[c]
                if op.modifier:
                    bits = self._imm_mod(bits, kind, op.modifier)
                parts.append({"f": fmt_float, "i": fmt_int, "u": fmt_uint}[kind](bits))
            if n == 1:
                return parts[0]
            return "%s(%s)" % (vec_type({"f": "float", "i": "int", "u": "uint"}[kind], n),
                               ", ".join(parts))
        if t == O_CB:
            slot = op.indices[0][0]
            cbname = self.b.cb_slots.get(slot)
            if cbname is None:
                raise TranslateError("unknown cbuffer slot %d" % slot)
            rel, imm = self.index_expr(op.indices[1])
            parts = [self.cb_component(cbname, imm, c, rel) for c in sel]
            e = parts[0] if n == 1 else "float%d(%s)" % (n, ", ".join(parts))
        elif t == O_ICB:
            rel, imm = self.index_expr(op.indices[0])
            idx = "%s + %d" % (rel, imm) if rel else "%d" % imm
            e = "asfloat(%s_icb[%s])" % (self.prefix, idx)
            e += "." + "".join(SW[c] for c in sel)
        elif t == O_INPUT_PRIMITIVEID:
            e = "asfloat(vPrim)"
            e = e if n == 1 else "%s.%s" % (e, "x" * n)
        elif t == O_INPUT_COVERAGE_MASK:
            e = "asfloat(vCoverage)"
            e = e if n == 1 else "%s.%s" % (e, "x" * n)
        else:
            name = self.reg_name(op)
            if op.ncomp == 1:
                e = name if n == 1 else "%s.%s" % (name, "x" * n)
            else:
                e = "%s.%s" % (name, "".join(SW[c] for c in sel))
        if kind == "i":
            e = "asint(%s)" % e
        elif kind == "u":
            e = "asuint(%s)" % e
        if op.modifier == 1:
            e = "(-%s)" % e
        elif op.modifier == 2:
            e = "abs(%s)" % e
        elif op.modifier == 3:
            e = "(-abs(%s))" % e
        return e

    @staticmethod
    def _imm_mod(bits, kind, mod):
        if kind == "f":
            v = f32(bits)
            if mod in (2, 3):
                v = abs(v)
            if mod in (1, 3):
                v = -v
            return struct.unpack("<I", struct.pack("<f", v))[0]
        v = bits - (1 << 32) if bits & 0x80000000 else bits
        if mod in (2, 3):
            v = abs(v)
        if mod in (1, 3):
            v = -v
        return v & 0xFFFFFFFF

    def dest(self, op):
        """Returns (target string, component list) for a destination operand."""
        if op.type == O_NULL:
            return None, []
        name = self.reg_name(op)
        if op.type in (O_OUTPUT_DEPTH, O_OUTPUT_DEPTH_GE, O_OUTPUT_DEPTH_LE,
                       O_OUTPUT_COVERAGE_MASK) or op.ncomp == 1:
            return name, [0]
        comps = [c for c in range(4) if op.mask & (1 << c)]
        if not comps:
            return None, []
        return "%s.%s" % (name, "".join(SW[c] for c in comps)), comps

    def assign(self, ins, dop, expr, n, kind="f", scalar=False):
        target, comps = self.dest(dop)
        if target is None:
            return
        cnt = len(comps)
        if scalar and cnt > 1:
            expr = "(%s).%s" % (expr, "x" * cnt)
        if kind == "i":
            expr = "asfloat(%s)" % expr
        elif kind == "u":
            expr = "asfloat(%s)" % expr
        if ins.saturate:
            expr = "saturate(%s)" % expr
        self.emit("%s = %s;" % (target, expr))

    # ------------------------------------------------------------------
    def componentwise(self, ins, fn, kind_in, kind_out=None, nsrc=None):
        """Emit a per-component instruction. fn(list_of_src_exprs, n)."""
        kind_out = kind_out or kind_in
        d = ins.operands[0]
        target, comps = self.dest(d)
        if target is None:
            return
        n = len(comps)
        srcs = [self.src(s, n, kind_in, tuple(comps) if d.ncomp != 1 else (0,))
                for s in ins.operands[1:1 + (nsrc or len(ins.operands) - 1)]]
        expr = fn(srcs, n)
        self.assign(ins, d, expr, n, kind_out)

    def resource_info(self, op):
        slot = op.indices[0][0]
        dim, ret = self.resources.get(slot, (3, (5, 5, 5, 5)))
        tex = self.b.textures.get(slot)
        name = tex[0] if tex else "%s_t%d" % (self.prefix, slot)
        return slot, name, dim

    def declare_texture(self, slot, name, dim, ret):
        tname = RESOURCE_DIMS.get(dim, "Texture2D")
        rt = {3: "int4", 4: "uint4"}.get(ret[0], "float4") if ret else "float4"
        self.decls.textures.setdefault(name, "%s<%s>" % (tname, rt))

    def sampler_name(self, op):
        slot = op.indices[0][0]
        if slot in self.b.samplers:
            # inline sampler state: Unity derives it from the sampler's name
            nm = inline_sampler_name(self.b.samplers[slot], self.sampler_modes.get(slot) == 1)
        else:
            nm = None
            for tslot, (tname, _d, sslot, _ms) in self.b.textures.items():
                if sslot == slot:
                    nm = "sampler" + tname
                    break
            if nm is None:
                nm = "%s_s%d" % (self.prefix, slot)
        cmp = self.sampler_modes.get(slot) == 1
        self.decls.samplers.setdefault(
            nm, "%s %s;" % ("SamplerComparisonState" if cmp else "SamplerState", nm))
        return nm

    @staticmethod
    def coord_count(dim):
        return {1: 1, 2: 1, 3: 2, 4: 2, 5: 3, 6: 3, 7: 2, 8: 3, 9: 3, 10: 4}.get(dim, 2)

    def tex_result(self, ins, call, res_op):
        d = ins.operands[0]
        target, comps = self.dest(d)
        if target is None:
            return
        sw = res_op.comps()
        e = "(%s).%s" % (call, "".join(SW[sw[c]] for c in comps))
        self.assign(ins, d, e, len(comps))

    def offset_arg(self, ins, dim):
        if ins.offsets == (0, 0, 0):
            return ""
        n = {3: 2, 8: 2, 5: 3}.get(dim, 2)
        return ", int%d(%s)" % (n, ", ".join(str(v) for v in ins.offsets[:n]))

    # ------------------------------------------------------------------
    def translate_body(self):
        for ins in self.prog.instructions:
            self.translate(ins)

    def translate(self, ins):
        n = ins.name
        ops = ins.operands
        if n.startswith("dcl_"):
            self.declaration(ins)
            return
        if n == "customdata":
            if ins.extra["class"] == 3:  # immediate constant buffer
                vals = ins.raw
                rows = ["uint4(%s)" % ", ".join("0x%08Xu" % v for v in vals[i:i + 4])
                        for i in range(0, len(vals), 4)]
                self.icb = "static const uint4 %s_icb[%d] = { %s };" % (
                    self.prefix, len(rows), ", ".join(rows))
            return
        if n in ("nop", "label"):
            return
        bin_f = {"add": "%s + %s", "mul": "%s * %s", "div": "%s / %s"}
        if n in bin_f:
            fmt = bin_f[n]
            self.componentwise(ins, lambda s, k: fmt % (s[0], s[1]), "f")
        elif n == "mad":
            self.componentwise(ins, lambda s, k: "%s * %s + %s" % tuple(s), "f")
        elif n in ("min", "max"):
            self.componentwise(ins, lambda s, k, fn=n: "%s(%s, %s)" % (fn, s[0], s[1]), "f")
        elif n == "mov":
            self.componentwise(ins, lambda s, k: s[0], "f")
        elif n == "movc":
            d = ins.operands[0]
            target, comps = self.dest(d)
            if target is None:
                return
            k = len(comps)
            c = self.src(ops[1], k, "u", tuple(comps))
            a = self.src(ops[2], k, "f", tuple(comps))
            b = self.src(ops[3], k, "f", tuple(comps))
            if k == 1:
                e = "(%s != 0u) ? %s : %s" % (c, a, b)
            else:
                e = "%s((%s != 0u) ? %s : %s)" % (vec_type("float", k), c, a, b)
            self.assign(ins, d, e, k)
        elif n in ("dp2", "dp3", "dp4"):
            cnt = int(n[2])
            a = self.src(ops[1], cnt, "f")
            b = self.src(ops[2], cnt, "f")
            self.assign(ins, ops[0], "dot(%s, %s)" % (a, b), 1, scalar=True)
        elif n in ("lt", "ge", "eq", "ne", "ilt", "ige", "ieq", "ine", "ult", "uge"):
            cmp = {"lt": "<", "ge": ">=", "eq": "==", "ne": "!="}[n.lstrip("iu")]
            kind = "f" if n in ("lt", "ge", "eq", "ne") else ("u" if n[0] == "u" else "i")
            self.componentwise(
                ins, lambda s, k: "(-(%s)(%s %s %s))" % (vec_type("int", k), s[0], cmp, s[1]),
                kind, "i")
        elif n in ("and", "or", "xor"):
            o = {"and": "&", "or": "|", "xor": "^"}[n]
            self.componentwise(ins, lambda s, k: "(%s %s %s)" % (s[0], o, s[1]), "u")
        elif n == "not":
            self.componentwise(ins, lambda s, k: "(~%s)" % s[0], "u")
        elif n == "iadd":
            self.componentwise(ins, lambda s, k: "(%s + %s)" % (s[0], s[1]), "i")
        elif n == "ineg":
            self.componentwise(ins, lambda s, k: "(-%s)" % s[0], "i")
        elif n in ("imax", "imin"):
            self.componentwise(ins, lambda s, k, fn=n[1:]: "%s(%s, %s)" % (fn, s[0], s[1]), "i")
        elif n in ("umax", "umin"):
            self.componentwise(ins, lambda s, k, fn=n[1:]: "%s(%s, %s)" % (fn, s[0], s[1]), "u")
        elif n == "imad":
            self.componentwise(ins, lambda s, k: "(%s * %s + %s)" % tuple(s), "i")
        elif n == "umad":
            self.componentwise(ins, lambda s, k: "(%s * %s + %s)" % tuple(s), "u")
        elif n in ("ishl", "ishr", "ushr"):
            kind = "u" if n == "ushr" else "i"
            o = "<<" if n == "ishl" else ">>"
            d = ops[0]
            target, comps = self.dest(d)
            if target is None:
                return
            k = len(comps)
            a = self.src(ops[1], k, kind, tuple(comps))
            b = self.src(ops[2], k, "u", tuple(comps))
            self.assign(ins, d, "(%s %s (%s & 31u))" % (a, o, b), k, kind)
        elif n in ("imul", "umul"):
            kind = "i" if n == "imul" else "u"
            lo = ops[1]
            target, comps = self.dest(lo)
            if ops[0].type != O_NULL:
                raise TranslateError("%s high result not supported" % n)
            if target is None:
                return
            k = len(comps)
            a = self.src(ops[2], k, kind, tuple(comps))
            b = self.src(ops[3], k, kind, tuple(comps))
            self.assign(ins, lo, "(%s * %s)" % (a, b), k, kind)
        elif n == "udiv":
            for di, o in ((0, "/"), (1, "%")):
                d = ops[di]
                target, comps = self.dest(d)
                if target is None:
                    continue
                k = len(comps)
                a = self.src(ops[2], k, "u", tuple(comps))
                b = self.src(ops[3], k, "u", tuple(comps))
                self.assign(ins, d, "(%s %s %s)" % (a, o, b), k, "u")
        elif n == "ftoi":
            self.componentwise(ins, lambda s, k: "(%s)(%s)" % (vec_type("int", k), s[0]), "f", "i")
        elif n == "ftou":
            self.componentwise(ins, lambda s, k: "(%s)(%s)" % (vec_type("uint", k), s[0]), "f", "u")
        elif n == "itof":
            self.componentwise(ins, lambda s, k: "(%s)(%s)" % (vec_type("float", k), s[0]), "i", "f")
        elif n == "utof":
            self.componentwise(ins, lambda s, k: "(%s)(%s)" % (vec_type("float", k), s[0]), "u", "f")
        elif n in ("frc", "round_ne", "round_ni", "round_pi", "round_z", "exp", "log",
                   "rsq", "sqrt", "rcp", "deriv_rtx", "deriv_rty", "deriv_rtx_coarse",
                   "deriv_rtx_fine", "deriv_rty_coarse", "deriv_rty_fine"):
            fn = {"frc": "frac", "round_ne": "round", "round_ni": "floor",
                  "round_pi": "ceil", "round_z": "trunc", "exp": "exp2", "log": "log2",
                  "rsq": "rsqrt", "sqrt": "sqrt", "rcp": "rcp", "deriv_rtx": "ddx",
                  "deriv_rty": "ddy", "deriv_rtx_coarse": "ddx", "deriv_rtx_fine": "ddx",
                  "deriv_rty_coarse": "ddy", "deriv_rty_fine": "ddy"}[n]
            self.componentwise(ins, lambda s, k: "%s(%s)" % (fn, s[0]), "f")
        elif n == "sincos":
            for di, fn in ((0, "sin"), (1, "cos")):
                d = ops[di]
                target, comps = self.dest(d)
                if target is None:
                    continue
                k = len(comps)
                a = self.src(ops[2], k, "f", tuple(comps))
                self.assign(ins, d, "%s(%s)" % (fn, a), k)
        elif n in ("sample", "sample_b", "sample_l", "sample_d", "sample_c", "sample_c_lz"):
            res = ops[2]
            slot, tname, dim = self.resource_info(res)
            self.declare_texture(slot, tname, dim, self.resources.get(slot, (3, None))[1])
            if n in ("sample_c", "sample_c_lz"):
                self.sampler_modes[ops[3].indices[0][0]] = 1
            sname = self.sampler_name(ops[3])
            cc = self.coord_count(dim)
            coord = self.src(ops[1], cc, "f")
            off = self.offset_arg(ins, dim)
            if n == "sample":
                call = "%s.Sample(%s, %s%s)" % (tname, sname, coord, off)
            elif n == "sample_b":
                call = "%s.SampleBias(%s, %s, %s%s)" % (tname, sname, coord,
                                                       self.src(ops[4], 1, "f"), off)
            elif n == "sample_l":
                call = "%s.SampleLevel(%s, %s, %s%s)" % (tname, sname, coord,
                                                        self.src(ops[4], 1, "f"), off)
            elif n == "sample_d":
                gc = {6: 3, 5: 3}.get(dim, 2)
                call = "%s.SampleGrad(%s, %s, %s, %s%s)" % (
                    tname, sname, coord, self.src(ops[4], gc, "f"),
                    self.src(ops[5], gc, "f"), off)
            elif n == "sample_c":
                call = "%s.SampleCmp(%s, %s, %s%s)" % (tname, sname, coord,
                                                      self.src(ops[4], 1, "f"), off)
            else:
                call = "%s.SampleCmpLevelZero(%s, %s, %s%s)" % (
                    tname, sname, coord, self.src(ops[4], 1, "f"), off)
            if n in ("sample_c", "sample_c_lz"):
                call = "float4((%s).xxxx)" % call
            self.tex_result(ins, call, res)
        elif n == "ld":
            res = ops[2]
            slot, tname, dim = self.resource_info(res)
            self.declare_texture(slot, tname, dim, self.resources.get(slot, (3, None))[1])
            cc = self.coord_count(dim)
            if dim == 1:  # Buffer
                call = "%s.Load(%s)" % (tname, self.src(ops[1], 1, "i"))
            else:
                call = "%s.Load(%s%s)" % (tname, self.src(ops[1], cc + 1, "i"),
                                          self.offset_arg(ins, dim))
            ret = self.resources.get(slot, (3, (5,)))[1] or (5,)
            if ret[0] in (3, 4):
                call = "asfloat(%s)" % call
            self.tex_result(ins, call, res)
        elif n == "resinfo":
            res = ops[2]
            slot, tname, dim = self.resource_info(res)
            self.declare_texture(slot, tname, dim, self.resources.get(slot, (3, None))[1])
            self.decls.helpers.add("resinfo")
            mip = self.src(ops[1], 1, "u")
            kind = ins.resinfo_ret  # 0 float, 1 rcpfloat, 2 uint
            call = "%s_resinfo_%s(%s, %s)" % ("sp", dim, tname, mip)
            self.decls.helpers.add("resinfo_%d" % dim)
            if kind == 1:
                call = "float4(1.0 / (%s).xyz, (%s).w)" % (call, call)
            elif kind == 2:
                call = "asfloat(uint4(%s))" % call
            self.tex_result(ins, call, res)
        elif n == "if":
            c = self.src(ops[0], 1, "u")
            self.emit("if (%s %s 0u) {" % (c, "!=" if ins.test_nz else "=="))
            self.indent += 1
        elif n == "else":
            self.indent -= 1
            self.emit("} else {")
            self.indent += 1
        elif n in ("endif", "endloop", "endswitch"):
            self.indent -= 1
            self.emit("}")
        elif n == "loop":
            self.emit("[loop] while (true) {")
            self.indent += 1
        elif n == "break":
            self.emit("break;")
        elif n == "continue":
            self.emit("continue;")
        elif n in ("breakc", "continuec", "retc", "discard"):
            c = self.src(ops[0], 1, "u")
            stmt = {"breakc": "break;", "continuec": "continue;", "retc": "return;",
                    "discard": "discard;"}[n]
            self.emit("if (%s %s 0u) { %s }" % (c, "!=" if ins.test_nz else "==", stmt))
        elif n == "ret":
            if self.indent > 1:
                self.emit("return;")
        elif n == "switch":
            self.emit("switch (%s) {" % self.src(ops[0], 1, "i"))
            self.indent += 1
        elif n == "case":
            self.emit("case %s:" % fmt_int(ops[0].imm[0]))
        elif n == "default":
            self.emit("default:")
        elif n == "countbits":
            self.componentwise(ins, lambda s, k: "countbits(%s)" % s[0], "u")
        elif n == "firstbit_lo":
            self.componentwise(ins, lambda s, k: "firstbitlow(%s)" % s[0], "u")
        elif n == "firstbit_hi":
            self.componentwise(ins, lambda s, k: "(31u - firstbithigh(%s))" % s[0], "u")
        elif n == "bfrev":
            self.componentwise(ins, lambda s, k: "reversebits(%s)" % s[0], "u")
        elif n == "f32tof16":
            self.componentwise(ins, lambda s, k: "f32tof16(%s)" % s[0], "f", "u")
        elif n == "f16tof32":
            self.componentwise(ins, lambda s, k: "f16tof32(%s)" % s[0], "u", "f")
        else:
            raise TranslateError("unsupported instruction %s" % n)

    def declaration(self, ins):
        n = ins.name
        if n == "dcl_temps":
            self.ntemps = ins.extra["count"]
        elif n == "dcl_indexable_temp":
            self.indexable[ins.extra["reg"]] = ins.extra["size"]
        elif n == "dcl_resource":
            slot = ins.operands[0].indices[0][0]
            self.resources[slot] = (ins.extra["dim"], ins.extra["ret"])
        elif n == "dcl_sampler":
            self.sampler_modes[ins.operands[0].indices[0][0]] = ins.extra["mode"]
        # inputs/outputs are taken from the signatures

    # ------------------------------------------------------------------
    def signature_struct(self, elems, name, is_input, stage):
        members = []
        seen = set()
        for e in elems:
            sem = e.name.upper() if e.sv == 0 else e.name
            key = (sem, e.index)
            if key in seen:
                continue
            seen.add(key)
            mask = e.mask
            ncomp = bin(mask).count("1")
            first = (mask & -mask).bit_length() - 1
            base = {1: "uint", 2: "int"}.get(e.comp_type, "float")
            sv = e.sv
            if e.name.upper() == "SV_ISFRONTFACE":
                base, ncomp = "bool", 1
            if e.name.upper() in ("SV_TARGET", "SV_DEPTH", "SV_COVERAGE", "SV_POSITION",
                                  "SV_VERTEXID", "SV_INSTANCEID", "SV_ISFRONTFACE",
                                  "SV_PRIMITIVEID", "SV_SAMPLEINDEX"):
                semantic = e.name.upper()
                if semantic == "SV_TARGET":
                    semantic = "SV_Target%d" % e.index
                elif semantic == "SV_POSITION":
                    semantic = "SV_POSITION"
                    ncomp, first = 4, 0
            else:
                semantic = "%s%d" % (e.name.upper(), e.index)
            interp = ""
            if is_input and stage == "ps":
                mode = self.ps_interp.get(e.register)
                if mode == 1 and base == "float":
                    interp = "nointerpolation "
                elif mode in (4, 5) and semantic != "SV_POSITION":
                    interp = "noperspective "
                elif mode in (3, 5) and semantic != "SV_POSITION":
                    interp = "centroid "
            mname = "m_%s" % semantic
            members.append((e, mname, vec_type(base, ncomp), semantic, first, ncomp, interp))
        lines = ["struct %s {" % name]
        for (e, mname, ty, sem, first, nc, interp) in members:
            lines.append("    %s%s %s : %s;" % (interp, ty, mname, sem))
        lines.append("};")
        return "\n".join(lines), members

    def build(self):
        prog = self.prog
        stage = prog.kind
        self.ps_interp = {}
        for ins in prog.decls:
            if ins.name in ("dcl_input_ps", "dcl_input_ps_siv", "dcl_input_ps_sgv"):
                self.ps_interp[ins.operands[0].indices[0][0]] = ins.extra.get("interp", 0)
        self.translate_body()
        if self.indent != 1:
            raise TranslateError("unbalanced control flow")
        in_struct, in_members = self.signature_struct(prog.inputs, self.prefix + "_in",
                                                      True, stage)
        out_struct, out_members = self.signature_struct(prog.outputs, self.prefix + "_out",
                                                        False, stage)
        in_regs = sorted({e.register for (e, *_r) in in_members if e.register != 0xFFFFFFFF})
        out_regs = sorted({e.register for (e, *_r) in out_members if e.register != 0xFFFFFFFF})
        body_args = ["float4 v%d" % r for r in in_regs] + ["inout float4 o%d" % r for r in out_regs]
        if self.used_depth:
            body_args.append("inout float oDepth")
        if self.uses_coverage_out:
            body_args.append("inout float oMask")
        src = []
        if self.icb:
            src.append(self.icb)
        src.append(in_struct)
        src.append(out_struct)
        src.append("void %s_body(%s) {" % (self.prefix, ", ".join(body_args)))
        if self.ntemps:
            src.append("    float4 %s;" % ", ".join("r%d = 0" % i for i in range(self.ntemps)))
        for reg, size in sorted(self.indexable.items()):
            src.append("    float4 x%d[%d];" % (reg, size))
        src.extend(self.lines)
        src.append("}")
        # entry point
        src.append("%s_out %s(%s_in i) {" % (self.prefix, self.prefix, self.prefix))
        for r in in_regs:
            src.append("    float4 v%d = 0;" % r)
        for (e, mname, ty, sem, first, nc, interp) in in_members:
            if e.register == 0xFFFFFFFF:
                continue
            comps = "".join(SW[first + k] for k in range(nc))
            val = "i.%s" % mname
            if ty.startswith(("uint", "int")):
                val = "asfloat(%s)" % val
            elif ty.startswith("bool"):
                val = "(i.%s ? asfloat(0xFFFFFFFFu) : 0.0)" % mname
            src.append("    v%d.%s = %s;" % (e.register, comps, val))
        for r in out_regs:
            src.append("    float4 o%d = 0;" % r)
        if self.used_depth:
            src.append("    float oDepth = 0;")
        if self.uses_coverage_out:
            src.append("    float oMask = 0;")
        call_args = ["v%d" % r for r in in_regs] + ["o%d" % r for r in out_regs]
        if self.used_depth:
            call_args.append("oDepth")
        if self.uses_coverage_out:
            call_args.append("oMask")
        src.append("    %s_body(%s);" % (self.prefix, ", ".join(call_args)))
        src.append("    %s_out o;" % self.prefix)
        for (e, mname, ty, sem, first, nc, interp) in out_members:
            if sem == "SV_DEPTH":
                src.append("    o.%s = oDepth;" % mname)
                continue
            if sem == "SV_COVERAGE":
                src.append("    o.%s = asuint(oMask);" % mname)
                continue
            comps = "".join(SW[first + k] for k in range(nc))
            val = "o%d.%s" % (e.register, comps)
            if ty.startswith("uint"):
                val = "asuint(%s)" % val
            elif ty.startswith("int"):
                val = "asint(%s)" % val
            src.append("    o.%s = %s;" % (mname, val))
        src.append("    return o;")
        src.append("}")
        return "\n".join(src)


HELPERS = {
    "resinfo_3": """float4 sp_resinfo_3(Texture2D<float4> t, uint mip) {
    uint w, h, n; t.GetDimensions(mip, w, h, n); return float4(w, h, 0, n);
}""",
    "resinfo_5": """float4 sp_resinfo_5(Texture3D<float4> t, uint mip) {
    uint w, h, d, n; t.GetDimensions(mip, w, h, d, n); return float4(w, h, d, n);
}""",
    "resinfo_6": """float4 sp_resinfo_6(TextureCube<float4> t, uint mip) {
    uint w, h, n; t.GetDimensions(mip, w, h, n); return float4(w, h, 0, n);
}""",
}


def translate(prog, bindings, prefix, decls):
    return Translator(prog, bindings, prefix, decls).build()
