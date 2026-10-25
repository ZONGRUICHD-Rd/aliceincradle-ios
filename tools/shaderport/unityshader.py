"""Read compiled Unity 2022.3 Shader objects (via UnityPy typetrees).

Extracts, for every pass/program/variant, the DXBC bytecode together with the
parameter bindings needed to give registers their original names again.
"""
import struct

import lz4.block

from dxbc import find_dxbc

PLATFORM_D3D11 = 4

# m_GpuProgramType values (ShaderGpuProgramType) for D3D11
GPU_PROGRAM_STAGE = {
    9: "vs", 10: "ps", 11: "vs", 12: "vs", 13: "ps", 14: "ps",  # DX10 level9 / legacy
    15: "vs", 16: "vs", 17: "ps", 18: "ps", 19: "gs", 20: "gs", 21: "hs", 22: "ds",
}


class Reader:
    def __init__(self, data, pos=0):
        self.d = data
        self.p = pos

    def u32(self):
        v, = struct.unpack_from("<I", self.d, self.p)
        self.p += 4
        return v

    def i32(self):
        v, = struct.unpack_from("<i", self.d, self.p)
        self.p += 4
        return v

    def string(self):
        n = self.u32()
        s = self.d[self.p:self.p + n].decode("utf-8", "replace")
        self.p += (n + 3) & ~3
        return s

    def bytes_(self):
        n = self.u32()
        b = self.d[self.p:self.p + n]
        self.p += (n + 3) & ~3
        return b


class Param:
    """A value inside a constant buffer (vector, scalar or matrix)."""

    def __init__(self, name, offset, rows, cols, array_size, type_=0):
        self.name = name
        self.offset = offset          # in bytes
        self.rows = rows              # 1 for vectors, >1 for matrices
        self.cols = cols              # components per row
        self.array_size = array_size  # 0 = not an array
        self.type = type_             # 0 float, 1 int, 2 bool, 3 half(?)

    @property
    def is_matrix(self):
        return self.rows > 1

    def reg_count(self):
        n = max(self.array_size, 1)
        if self.is_matrix:
            # column-major storage: one register per column
            return n * self.cols if self.cols > 1 else n * self.rows
        return n if self.array_size else 1

    def __repr__(self):
        return "Param(%s @%d %dx%d[%d])" % (self.name, self.offset, self.rows,
                                            self.cols, self.array_size)


class CBuffer:
    def __init__(self, name, size):
        self.name = name
        self.size = size
        self.params = {}

    def add(self, p):
        self.params[p.name] = p


class Bindings:
    """Union of common parameters and the per-variant parameter blob."""

    def __init__(self):
        self.cbuffers = {}       # name -> CBuffer
        self.cb_slots = {}       # slot -> name
        self.textures = {}       # slot -> (name, dim, sampler_slot, multisampled)
        self.samplers = {}       # slot -> name (inline sampler states)
        self.buffers = {}        # slot -> name
        self.uavs = {}           # slot -> name
        self.globals_vec = []    # vector params outside cbuffers (rare)

    def cb(self, name, size):
        c = self.cbuffers.get(name)
        if c is None:
            c = self.cbuffers[name] = CBuffer(name, size)
        c.size = max(c.size, size)
        return c


def _names(pass_):
    return {idx: name for name, idx in pass_["m_NameIndices"]}


def common_bindings(prog, names):
    b = Bindings()
    cp = prog["m_CommonParameters"]
    for cbd in cp.get("m_ConstantBuffers", []):
        c = b.cb(names[cbd["m_NameIndex"]], cbd["m_Size"])
        for v in cbd.get("m_VectorParams", []):
            c.add(Param(names[v["m_NameIndex"]], v["m_Index"], 1, v["m_Dim"],
                        v["m_ArraySize"], v["m_Type"]))
        for m in cbd.get("m_MatrixParams", []):
            c.add(Param(names[m["m_NameIndex"]], m["m_Index"], m["m_RowCount"],
                        m.get("m_ColumnCount", 4), m["m_ArraySize"], m["m_Type"]))
        for sp in cbd.get("m_StructParams", []):
            # flatten struct members as individual params with dotted names
            for v in sp.get("m_VectorMembers", []):
                c.add(Param(names[v["m_NameIndex"]], v["m_Index"], 1, v["m_Dim"],
                            v["m_ArraySize"], v["m_Type"]))
            for m in sp.get("m_MatrixMembers", []):
                c.add(Param(names[m["m_NameIndex"]], m["m_Index"], m["m_RowCount"],
                            m.get("m_ColumnCount", 4), m["m_ArraySize"], m["m_Type"]))
    for bd in cp.get("m_ConstantBufferBindings", []):
        b.cb_slots[bd["m_Index"]] = names[bd["m_NameIndex"]]
    for t in cp.get("m_TextureParams", []):
        b.textures[t["m_Index"]] = (names[t["m_NameIndex"]], t["m_Dim"],
                                    t["m_SamplerIndex"], t.get("m_MultiSampled", False))
    for s in cp.get("m_Samplers", []):
        b.samplers[s["bindPoint"]] = s["sampler"]
    for bp in cp.get("m_BufferParams", []):
        b.buffers[bp["m_Index"]] = names[bp["m_NameIndex"]]
    for u in cp.get("m_UAVParams", []):
        b.uavs[u["m_Index"]] = names[u["m_NameIndex"]]
    for v in cp.get("m_VectorParams", []):
        b.globals_vec.append(Param(names[v["m_NameIndex"]], v["m_Index"], 1,
                                   v["m_Dim"], v["m_ArraySize"], v["m_Type"]))
    return b


def merge_param_blob(b, blob):
    """Merge a per-variant parameter blob (binary) into bindings `b`.

    Layout (Unity 2022.3, magic 202012090):
        u32 magic, u32 cbCount+1, u32 x4 (vector/matrix/texture/buffer counts,
        always zero in this game), then cbCount cbuffers:
            string name, u32 size, u32 nParams, params..., u32 nStructs(=0)
        then u32 nBindings, bindings: string name, u32 kind, then
            kind 0 (texture): u32 slot, u32 samplerSlot, u32 dim
            kind 1 (cbuffer): u32 slot, u32 arraySize
    A param is: string name, u32 type, u32 rows, u32 cols, u32 isMatrix,
    u32 arraySize, u32 offset.
    """
    r = Reader(blob)
    r.u32()
    ncb = r.u32() - 1
    zeros = [r.u32() for _ in range(4)]
    if any(zeros):
        raise ValueError("unsupported parameter blob (non-cbuffer params)")
    for _ in range(ncb):
        name = r.string()
        size = r.u32()
        c = b.cb(name, size)
        nparams = r.u32()
        for _ in range(nparams):
            pname = r.string()
            ptype = r.u32()
            rows = r.u32()
            cols = r.u32()
            _is_matrix = r.u32()
            arr = r.u32()
            off = r.u32()
            c.add(Param(pname, off, rows, cols, arr, ptype))
        nstruct = r.u32()
        if nstruct:
            raise ValueError("struct params in parameter blob not supported")
    if r.p + 4 <= len(blob):
        nb = r.u32()
        for _ in range(nb):
            name = r.string()
            kind = r.u32()
            if kind == 0:
                slot = r.u32()
                sampler = r.u32()
                dim = r.u32()
                b.textures[slot] = (name, dim, sampler, False)
            elif kind == 1:
                slot = r.u32()
                r.u32()
                b.cb_slots[slot] = name
            else:
                raise ValueError("unsupported binding kind %d for %s" % (kind, name))
    return b


class SubProgram:
    def __init__(self, stage, keywords, dxbc, bindings, gpu_type):
        self.stage = stage
        self.keywords = keywords      # list of keyword names
        self.dxbc = dxbc
        self.bindings = bindings
        self.gpu_type = gpu_type


def decompress_blob(shader, platform_index):
    offs = shader["offsets"][platform_index]
    clen = shader["compressedLengths"][platform_index]
    dlen = shader["decompressedLengths"][platform_index]
    data = bytes(shader["compressedBlob"])
    segs = []
    for o, c, dl in zip(offs, clen, dlen):
        segs.append(lz4.block.decompress(data[o:o + c], uncompressed_size=dl))
    return segs


def blob_entry(segs, index):
    seg0 = segs[0]
    n, = struct.unpack_from("<I", seg0, 0)
    off, ln, seg = struct.unpack_from("<III", seg0, 4 + index * 12)
    return segs[seg][off:off + ln]


def parse_subprogram_entry(e):
    r = Reader(e)
    _ver = r.u32()
    gpu_type = r.u32()
    r.p += 16
    nk = r.u32()
    kws = [r.string() for _ in range(nk)]
    code = r.bytes_()
    return gpu_type, kws, find_dxbc(code)


def read_programs(shader, pass_, prog_key, keyword_names):
    """Yield SubProgram objects for one program (progVertex etc.) of a pass."""
    platforms = shader["platforms"]
    if PLATFORM_D3D11 not in platforms:
        return []
    pi = platforms.index(PLATFORM_D3D11)
    segs = decompress_blob(shader, pi)
    prog = pass_[prog_key]
    names = _names(pass_)
    subs = prog["m_PlayerSubPrograms"]
    pblobs = prog["m_ParameterBlobIndices"]
    if not subs or pi >= len(subs) or not subs[pi]:
        # some shaders keep a single non-empty list regardless of platform index
        subs_l = next((s for s in subs if s), [])
        pbl = next((s for s in pblobs if s), [])
    else:
        subs_l = subs[pi]
        pbl = pblobs[pi] if pi < len(pblobs) else []
    out = []
    for i, sp in enumerate(subs_l):
        gpu_type, kws_inline, dx = parse_subprogram_entry(blob_entry(segs, sp["m_BlobIndex"]))
        b = common_bindings(prog, names)
        if i < len(pbl):
            merge_param_blob(b, blob_entry(segs, pbl[i]))
        kws = [keyword_names[k] for k in sp["m_KeywordIndices"]]
        stage = GPU_PROGRAM_STAGE.get(sp["m_GpuProgramType"], "?")
        out.append(SubProgram(stage, kws, dx, b, sp["m_GpuProgramType"]))
    return out
