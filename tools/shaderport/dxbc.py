"""Minimal DXBC (Shader Model 4.x / 5.0) container and bytecode parser.

Only what is needed to translate the 2D shaders used by the game back to
HLSL: signatures (ISGN/OSGN/PCSG), the SHDR/SHEX token stream and the
declarations inside it.  Token layout follows d3d11TokenizedProgramFormat.hpp.
"""
import struct

# ---------------------------------------------------------------------------
# Opcodes
# ---------------------------------------------------------------------------
OPCODE_NAMES = [
    "add", "and", "break", "breakc", "call", "callc", "case", "continue",
    "continuec", "cut", "default", "deriv_rtx", "deriv_rty", "discard", "div",
    "dp2", "dp3", "dp4", "else", "emit", "emitthencut", "endif", "endloop",
    "endswitch", "eq", "exp", "frc", "ftoi", "ftou", "ge", "iadd", "if", "ieq",
    "ige", "ilt", "imad", "imax", "imin", "imul", "ine", "ineg", "ishl", "ishr",
    "itof", "label", "ld", "ld_ms", "log", "loop", "lt", "mad", "min", "max",
    "customdata", "mov", "movc", "mul", "ne", "nop", "not", "or", "resinfo",
    "ret", "retc", "round_ne", "round_ni", "round_pi", "round_z", "rsq",
    "sample", "sample_c", "sample_c_lz", "sample_l", "sample_d", "sample_b",
    "sqrt", "switch", "sincos", "udiv", "ult", "uge", "umul", "umad", "umax",
    "umin", "ushr", "utof", "xor", "dcl_resource", "dcl_constantbuffer",
    "dcl_sampler", "dcl_index_range", "dcl_gs_output_primitive_topology",
    "dcl_gs_input_primitive", "dcl_max_output_vertex_count", "dcl_input",
    "dcl_input_sgv", "dcl_input_siv", "dcl_input_ps", "dcl_input_ps_sgv",
    "dcl_input_ps_siv", "dcl_output", "dcl_output_sgv", "dcl_output_siv",
    "dcl_temps", "dcl_indexable_temp", "dcl_global_flags", "reserved0",
    # SM 4.1
    "lod", "gather4", "sample_pos", "sample_info",
    # SM 5.0
    "reserved1", "hs_decls", "hs_control_point_phase", "hs_fork_phase",
    "hs_join_phase", "emit_stream", "cut_stream", "emitthencut_stream",
    "interface_call", "bufinfo", "deriv_rtx_coarse", "deriv_rtx_fine",
    "deriv_rty_coarse", "deriv_rty_fine", "gather4_c", "gather4_po",
    "gather4_po_c", "rcp", "f32tof16", "f16tof32", "uaddc", "usubb",
    "countbits", "firstbit_hi", "firstbit_lo", "firstbit_shi", "ubfe", "ibfe",
    "bfi", "bfrev", "swapc", "dcl_stream", "dcl_function_body",
    "dcl_function_table", "dcl_interface", "dcl_input_control_point_count",
    "dcl_output_control_point_count", "dcl_tess_domain",
    "dcl_tess_partitioning", "dcl_tess_output_primitive",
    "dcl_hs_max_tessfactor", "dcl_hs_fork_phase_instance_count",
    "dcl_hs_join_phase_instance_count", "dcl_thread_group", "dcl_uav_typed",
    "dcl_uav_raw", "dcl_uav_structured", "dcl_tgsm_raw", "dcl_tgsm_structured",
    "dcl_resource_raw", "dcl_resource_structured", "ld_uav_typed",
    "store_uav_typed", "ld_raw", "store_raw", "ld_structured",
    "store_structured", "atomic_and", "atomic_or", "atomic_xor",
    "atomic_cmp_store", "atomic_iadd", "atomic_imax", "atomic_imin",
    "atomic_umax", "atomic_umin", "imm_atomic_alloc", "imm_atomic_consume",
    "imm_atomic_iadd", "imm_atomic_and", "imm_atomic_or", "imm_atomic_xor",
    "imm_atomic_exch", "imm_atomic_cmp_exch", "imm_atomic_imax",
    "imm_atomic_imin", "imm_atomic_umax", "imm_atomic_umin", "sync",
]
OP = {name: i for i, name in enumerate(OPCODE_NAMES)}

# Operand types
O_TEMP, O_INPUT, O_OUTPUT, O_INDEXABLE_TEMP, O_IMM32, O_IMM64, O_SAMPLER, \
    O_RESOURCE, O_CB, O_ICB, O_LABEL, O_INPUT_PRIMITIVEID, O_OUTPUT_DEPTH, \
    O_NULL, O_RASTERIZER, O_OUTPUT_COVERAGE_MASK = range(16)
O_UAV = 30
O_TGSM = 31
O_THREAD_ID = 32
O_THREAD_GROUP_ID = 33
O_THREAD_ID_IN_GROUP = 34
O_INPUT_COVERAGE_MASK = 35
O_THREAD_ID_IN_GROUP_FLATTENED = 36
O_OUTPUT_DEPTH_GE = 38
O_OUTPUT_DEPTH_LE = 39

PROGRAM_TYPES = {0: "ps", 1: "vs", 2: "gs", 3: "hs", 4: "ds", 5: "cs"}

RESOURCE_DIMS = {
    1: "Buffer", 2: "Texture1D", 3: "Texture2D", 4: "Texture2DMS",
    5: "Texture3D", 6: "TextureCube", 7: "Texture1DArray",
    8: "Texture2DArray", 9: "Texture2DMSArray", 10: "TextureCubeArray",
    11: "RawBuffer", 12: "StructuredBuffer",
}
RETURN_TYPES = {1: "unorm", 2: "snorm", 3: "sint", 4: "uint", 5: "float",
                6: "mixed", 7: "double", 8: "continued", 9: "unused"}

SYSTEM_VALUE_NAMES = {
    0: None, 1: "SV_Position", 2: "SV_ClipDistance", 3: "SV_CullDistance",
    4: "SV_RenderTargetArrayIndex", 5: "SV_ViewportArrayIndex",
    6: "SV_VertexID", 7: "SV_PrimitiveID", 8: "SV_InstanceID",
    9: "SV_IsFrontFace", 10: "SV_SampleIndex",
}

INTERP_MODES = {0: "", 1: "constant", 2: "linear", 3: "linear centroid",
                4: "linear noperspective", 5: "linear noperspective centroid",
                6: "linear sample", 7: "linear noperspective sample"}


class Operand:
    __slots__ = ("type", "ncomp", "sel_mode", "mask", "swizzle", "select1",
                 "indices", "modifier", "imm", "min_precision", "nonuniform")

    def __init__(self):
        self.type = 0
        self.ncomp = 0
        self.sel_mode = 0
        self.mask = 0xF
        self.swizzle = (0, 1, 2, 3)
        self.select1 = 0
        self.indices = []      # list of (imm, rel_operand_or_None)
        self.modifier = 0      # 1 neg, 2 abs, 3 -abs
        self.imm = None        # list of raw uint32 for immediates
        self.min_precision = 0
        self.nonuniform = False

    def comps(self):
        """Component indices selected by a source operand (always 4 long)."""
        if self.ncomp == 1:
            return (0, 0, 0, 0)
        if self.sel_mode == 0:   # mask used as source: treat as identity
            return (0, 1, 2, 3)
        if self.sel_mode == 1:
            return self.swizzle
        return (self.select1,) * 4

    def __repr__(self):
        return "Operand(t=%d idx=%r mask=%x sw=%r mod=%d imm=%r)" % (
            self.type, self.indices, self.mask, self.swizzle, self.modifier,
            self.imm)


class Instruction:
    __slots__ = ("opcode", "name", "saturate", "test_nz", "operands",
                 "extra", "raw", "resinfo_ret", "precise", "offsets",
                 "res_dim", "res_ret")

    def __init__(self, opcode):
        self.opcode = opcode
        self.name = OPCODE_NAMES[opcode] if opcode < len(OPCODE_NAMES) else "op%d" % opcode
        self.saturate = False
        self.test_nz = False
        self.operands = []
        self.extra = {}
        self.raw = []
        self.resinfo_ret = 0
        self.precise = 0
        self.offsets = (0, 0, 0)
        self.res_dim = None
        self.res_ret = None

    def __repr__(self):
        return "%s%s %r %r" % (self.name, "_sat" if self.saturate else "",
                               self.operands, self.extra)


class SigElement:
    __slots__ = ("name", "index", "sv", "comp_type", "register", "mask",
                 "rw_mask", "stream", "min_precision")

    def __repr__(self):
        return "%s%d r%d m%x t%d sv%d" % (self.name, self.index, self.register,
                                         self.mask, self.comp_type, self.sv)


def parse_signature(data, kind):
    count, _ = struct.unpack_from("<II", data, 0)
    elems = []
    # OSG5 has stream field, OSG1/ISG1 have stream + min precision
    if kind in ("ISGN", "OSGN", "PCSG"):
        stride, extra = 24, 0
    elif kind == "OSG5":
        stride, extra = 28, 1
    else:  # ISG1/OSG1/PSG1
        stride, extra = 32, 2
    for i in range(count):
        off = 8 + i * stride
        e = SigElement()
        p = off
        e.stream = 0
        e.min_precision = 0
        if extra >= 1:
            e.stream, = struct.unpack_from("<I", data, p)
            p += 4
        name_off, e.index, e.sv, e.comp_type, e.register = struct.unpack_from("<IIIII", data, p)
        p += 20
        e.mask = data[p]
        e.rw_mask = data[p + 1]
        if extra == 2:
            e.min_precision, = struct.unpack_from("<I", data, p + 4)
        end = data.index(b"\0", name_off)
        e.name = data[name_off:end].decode("ascii")
        elems.append(e)
    return elems


class Program:
    def __init__(self):
        self.major = 4
        self.minor = 0
        self.kind = "vs"
        self.instructions = []
        self.inputs = []
        self.outputs = []
        self.patch_consts = []
        self.decls = []


def parse_dxbc(blob):
    """Parse a DXBC container.  `blob` must start at the 'DXBC' magic."""
    assert blob[:4] == b"DXBC", blob[:4]
    total, nchunks = struct.unpack_from("<II", blob, 24)
    prog = Program()
    shader_chunk = None
    for i in range(nchunks):
        off, = struct.unpack_from("<I", blob, 32 + i * 4)
        fourcc = blob[off:off + 4].decode("ascii", "replace")
        size, = struct.unpack_from("<I", blob, off + 4)
        data = blob[off + 8: off + 8 + size]
        if fourcc in ("ISGN", "ISG1"):
            prog.inputs = parse_signature(data, fourcc)
        elif fourcc in ("OSGN", "OSG1", "OSG5"):
            prog.outputs = parse_signature(data, fourcc)
        elif fourcc in ("PCSG", "PSG1"):
            prog.patch_consts = parse_signature(data, fourcc)
        elif fourcc in ("SHDR", "SHEX"):
            shader_chunk = data
    if shader_chunk is None:
        raise ValueError("no shader bytecode chunk")
    parse_tokens(shader_chunk, prog)
    return prog


def _read_operand(toks, p):
    t = toks[p]
    p += 1
    op = Operand()
    num = t & 3
    op.ncomp = {0: 0, 1: 1, 2: 4, 3: 0}[num]
    if num == 2:
        op.sel_mode = (t >> 2) & 3
        if op.sel_mode == 0:
            op.mask = (t >> 4) & 0xF
        elif op.sel_mode == 1:
            sw = (t >> 4) & 0xFF
            op.swizzle = (sw & 3, (sw >> 2) & 3, (sw >> 4) & 3, (sw >> 6) & 3)
        else:
            op.select1 = (t >> 4) & 3
    op.type = (t >> 12) & 0xFF
    dim = (t >> 20) & 3
    reps = [(t >> 22) & 7, (t >> 25) & 7, (t >> 28) & 7]
    ext = (t >> 31) & 1
    while ext:
        et = toks[p]
        p += 1
        ext = (et >> 31) & 1
        if (et & 0x3F) == 1:
            op.modifier = (et >> 6) & 0xFF
            op.min_precision = (et >> 14) & 7
            op.nonuniform = bool((et >> 17) & 1)
    if op.type == O_IMM32:
        n = 1 if num == 1 else 4
        op.imm = list(toks[p:p + n])
        p += n
    elif op.type == O_IMM64:
        n = 2 if num == 1 else 4
        op.imm = list(toks[p:p + n * 2])
        p += n * 2
    for d in range(dim):
        rep = reps[d]
        imm = 0
        rel = None
        if rep in (0, 3):
            imm = toks[p]
            p += 1
        elif rep in (1, 4):
            imm = toks[p] | (toks[p + 1] << 32)
            p += 2
        if rep in (2, 3, 4):
            rel, p = _read_operand(toks, p)
        op.indices.append((imm, rel))
    return op, p


def parse_tokens(data, prog):
    toks = struct.unpack_from("<%dI" % (len(data) // 4), data, 0)
    ver = toks[0]
    prog.minor = ver & 0xF
    prog.major = (ver >> 4) & 0xF
    prog.kind = PROGRAM_TYPES.get(ver >> 16, "?")
    length = toks[1]
    p = 2
    while p < length:
        tok = toks[p]
        opcode = tok & 0x7FF
        if opcode == OP["customdata"]:
            cls = tok >> 11
            ln = toks[p + 1]
            ins = Instruction(opcode)
            ins.extra["class"] = cls
            ins.raw = list(toks[p + 2:p + ln])
            prog.instructions.append(ins)
            p += ln
            continue
        ln = (tok >> 24) & 0x7F
        if ln == 0:
            raise ValueError("zero-length instruction %d at %d" % (opcode, p))
        end = p + ln
        ins = Instruction(opcode)
        ins.saturate = bool((tok >> 13) & 1)
        ins.test_nz = bool((tok >> 18) & 1)
        ins.resinfo_ret = (tok >> 11) & 3
        ins.precise = (tok >> 19) & 0xF
        q = p + 1
        ext = (tok >> 31) & 1
        while ext:
            et = toks[q]
            q += 1
            ext = (et >> 31) & 1
            etype = et & 0x3F
            if etype == 1:  # sample controls
                def s4(v):
                    v &= 0xF
                    return v - 16 if v & 8 else v
                ins.offsets = (s4(et >> 9), s4(et >> 13), s4(et >> 17))
            elif etype == 2:  # resource dim
                ins.res_dim = (et >> 6) & 0x1F
            elif etype == 3:  # resource return type
                ins.res_ret = tuple((et >> (6 + 4 * i)) & 0xF for i in range(4))
        name = ins.name
        if name == "dcl_temps":
            ins.extra["count"] = toks[q]
        elif name == "dcl_indexable_temp":
            ins.extra["reg"], ins.extra["size"], ins.extra["comps"] = toks[q:q + 3]
        elif name == "dcl_global_flags":
            ins.extra["flags"] = (tok >> 11) & 0x1FFF
        elif name == "dcl_thread_group":
            ins.extra["xyz"] = tuple(toks[q:q + 3])
        elif name in ("dcl_resource", "dcl_uav_typed"):
            ins.extra["dim"] = (tok >> 11) & 0x1F
            if name == "dcl_uav_typed":
                ins.extra["globally_coherent"] = (tok >> 16) & 1
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            rt = toks[q]
            q += 1
            ins.extra["ret"] = tuple((rt >> (4 * i)) & 0xF for i in range(4))
            if q < end:  # SM5.1 space
                ins.extra["space"] = toks[q]
        elif name in ("dcl_resource_raw", "dcl_uav_raw"):
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
        elif name in ("dcl_resource_structured", "dcl_uav_structured"):
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            ins.extra["stride"] = toks[q]
        elif name == "dcl_tgsm_raw":
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            ins.extra["bytes"] = toks[q]
        elif name == "dcl_tgsm_structured":
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            ins.extra["stride"], ins.extra["count"] = toks[q:q + 2]
        elif name == "dcl_constantbuffer":
            ins.extra["dynamic"] = bool((tok >> 11) & 1)
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
        elif name == "dcl_sampler":
            ins.extra["mode"] = (tok >> 11) & 0xF
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
        elif name in ("dcl_input_ps", "dcl_input_ps_siv", "dcl_input_ps_sgv"):
            ins.extra["interp"] = (tok >> 11) & 0xF
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            if q < end:
                ins.extra["sv"] = toks[q]
        elif name in ("dcl_input", "dcl_output", "dcl_input_sgv",
                      "dcl_input_siv", "dcl_output_sgv", "dcl_output_siv"):
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            if q < end:
                ins.extra["sv"] = toks[q]
        elif name == "dcl_index_range":
            op, q = _read_operand(toks, q)
            ins.operands.append(op)
            ins.extra["count"] = toks[q]
        elif name in ("dcl_gs_output_primitive_topology", "dcl_gs_input_primitive",
                      "dcl_tess_domain", "dcl_tess_partitioning",
                      "dcl_tess_output_primitive"):
            ins.extra["value"] = (tok >> 11) & 0x3F
        elif name in ("dcl_max_output_vertex_count", "dcl_input_control_point_count",
                      "dcl_output_control_point_count", "dcl_hs_fork_phase_instance_count",
                      "dcl_hs_join_phase_instance_count", "dcl_stream"):
            ins.extra["value"] = toks[q] if q < end else (tok >> 11) & 0x3F
        elif name == "dcl_hs_max_tessfactor":
            ins.extra["value"] = struct.unpack("<f", struct.pack("<I", toks[q]))[0]
        elif name in ("dcl_function_body", "dcl_function_table", "dcl_interface"):
            ins.raw = list(toks[q:end])
        else:
            while q < end:
                op, q = _read_operand(toks, q)
                ins.operands.append(op)
        if name.startswith("dcl_"):
            prog.decls.append(ins)
        prog.instructions.append(ins)
        p = end
    return prog


def find_dxbc(buf):
    i = buf.find(b"DXBC")
    if i < 0:
        return None
    total, = struct.unpack_from("<I", buf, i + 24)
    return buf[i:i + total]
