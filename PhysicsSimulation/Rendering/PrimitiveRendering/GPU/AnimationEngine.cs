﻿// File: AnimationEngine.cs
// Requires OpenTK 4.x (OpenTK.Graphics.OpenGL4)
// Place near PrimitivesGPU.cs (same namespace recommended)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.GPU;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU; // assumes PrimitiveGPU lives here

namespace PhysicsSimulation.Rendering.GPU
{
    public class AnimationEngine : IDisposable
    {
        // Binding points for SSBOs (must match shaders below)
        private const int BINDING_ANIMENTRIES = 0;
        private const int BINDING_ANIMINDEX = 1;
        private const int BINDING_MORPHDESC = 2;
        private const int BINDING_GEOMETRY = 3;
        private const int BINDING_RENDERINST = 4;

        // GL buffer handles
        private int _ssboAnimEntries = -1;
        private int _ssboAnimIndex = -1;
        private int _ssboMorphDesc = -1;
        private int _ssboGeometry = -1;        // also used as VBO for vertex attribute array
        private int _ssboRenderInstances = -1;

        // Shader programs
        private int _programAnimCompute = -1;
        private int _programMorphCompute = -1;
        private int _programRender = -1;
        private int _vao = -1;

        // Engine state
        private List<PrimitiveGpu> _primitives = new();
        private GeometryArena _arena;
        private int _primitiveCount => _primitives.Count;

        // Prebuilt arrays (CPU mirrors)
        private MorphDescCpu[] _morphDescs;
        private RenderInstanceCpu[] _renderInstances;
        private List<AnimEntryCpu> _uploadedAnimEntries = new();
        private bool _animationsEverUploaded = false;

        // Config
        private const int ANIM_ENTRY_SIZE_BYTES = 80; // from earlier spec
        private const int ANIM_INDEX_SIZE_BYTES = 16;
        private const int MORPH_DESC_SIZE_BYTES = 32;
        private const int RENDER_INSTANCE_SIZE_BYTES = 96; // 3 vec4 + vec4 + ivec4 + vec4 = 96
        private const int NO_ANIM_DEBUG_OUTPUT_RANGE = 10000;
        private int NO_ANIM_DEBUG_CURRENT;
        private const bool DO_NO_ANIM_DEBUG = false;

        public AnimationEngine(GeometryArena arena, IEnumerable<PrimitiveGpu> primitives)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            _primitives = primitives?.OrderBy(p => p.PrimitiveId).ToList() ?? throw new ArgumentNullException(nameof(primitives));

            // Assign PrimitiveIds if not already set
            for (int i = 0; i < _primitiveCount; i++)
            {
                if (_primitives[i].PrimitiveId == -1)
                {
                    _primitives[i].PrimitiveId = i;
                    DebugManager.Anim($"Assigned PrimitiveId {i} to primitive '{_primitives[i].Name}'");
                }
            }

            // allocate CPU-side descriptor arrays
            _morphDescs = new MorphDescCpu[_primitiveCount];
            _renderInstances = new RenderInstanceCpu[_primitiveCount];

            // create GL resources
            CreateBuffers();
            CreateShadersAndVAO();

            // initialize morph descriptors from primitives (offsets)
            InitMorphDescsFromPrimitives();
            UploadMorphDescBuffer(); // initial upload

            // initialize render instances from primitives
            InitRenderInstancesFromPrimitives();
            UploadRenderInstancesBuffer();
        }

        #region GL resource creation

        private void CreateBuffers()
        {
            // AnimEntries
            _ssboAnimEntries = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboAnimEntries);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 1, IntPtr.Zero, BufferUsageHint.DynamicDraw); // empty initial
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_ANIMENTRIES, _ssboAnimEntries);

            // AnimIndex
            _ssboAnimIndex = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboAnimIndex);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, _primitiveCount * ANIM_INDEX_SIZE_BYTES, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_ANIMINDEX, _ssboAnimIndex);

            // MorphDesc
            _ssboMorphDesc = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboMorphDesc);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, _primitiveCount * MORPH_DESC_SIZE_BYTES, IntPtr.Zero, BufferUsageHint.DynamicCopy);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_MORPHDESC, _ssboMorphDesc);

            // Geometry / arena buffer -> will be sized on UploadGeometry
            _ssboGeometry = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboGeometry);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, 1, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_GEOMETRY, _ssboGeometry);

            // Also bind geometry as ARRAY_BUFFER for vertex attrib reads
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ssboGeometry);

            // RenderInstances
            _ssboRenderInstances = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboRenderInstances);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, _primitiveCount * RENDER_INSTANCE_SIZE_BYTES, IntPtr.Zero, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_RENDERINST, _ssboRenderInstances);

            // unbind
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        private void CreateShadersAndVAO()
        {
            // Compute shaders and render program are inlined
            _programAnimCompute = CreateComputeProgram(ANIMATION_COMPUTE_SRC, "anim_compute");
            _programMorphCompute = CreateComputeProgram(MORPH_COMPUTE_SRC, "morph_compute");
            _programRender = CreateProgram(VERTEX_RENDER_SRC, FRAGMENT_RENDER_SRC);

            // VAO + vertex attrib: geometry buffer is used as array buffer with vec2 position at location 0
            _vao = GL.GenVertexArray();
            GL.BindVertexArray(_vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ssboGeometry);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), 0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindVertexArray(0);
        }

        #endregion

        #region MorphDesc init + upload

        private void InitMorphDescsFromPrimitives()
        {
            for (int i = 0; i < _primitiveCount; i++)
            {
                var p = _primitives[i];
                _morphDescs[i] = new MorphDescCpu
                {
                    CurrentT = 0f,
                    EaseAsFloat = 0f,
                    R0 = 0f,
                    R1 = 0f,
                    OffsetA = p.VertexOffsetA >= 0 ? p.VertexOffsetA : p.VertexOffsetRaw,
                    OffsetB = p.VertexOffsetB >= 0 ? p.VertexOffsetB : p.VertexOffsetRaw,
                    OffsetM = p.VertexOffsetM >= 0 ? p.VertexOffsetM : p.VertexOffsetA >= 0 ? p.VertexOffsetA : p.VertexOffsetRaw,
                    VertexCount = p.VertexCount
                };
            }
        }

        private void UploadMorphDescBuffer()
        {
            int size = _primitiveCount * MORPH_DESC_SIZE_BYTES;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboMorphDesc);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, size, ToByteArray(_morphDescs));
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }

        #endregion

        #region RenderInstances init + upload

        private void InitRenderInstancesFromPrimitives()
        {
            for (int i = 0; i < _primitiveCount; i++)
            {
                var p = _primitives[i];
                _renderInstances[i] = p.ToRenderInstanceCpu();
            }
        }

        private void UploadRenderInstancesBuffer()
        {
            int size = _primitiveCount * RENDER_INSTANCE_SIZE_BYTES;
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboRenderInstances);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, size, ToByteArray(_renderInstances));
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }

        #endregion

        #region Geometry upload

        /// <summary>
        /// Upload geometry arena contents (flattened) into the Geometry SSBO / VBO.
        /// Ensure the GeometryArena.TotalVertexCount is accurate.
        /// Each vertex is vec2 (2 floats).
        /// </summary>
        public void UploadGeometryFromPrimitives()
        {
            int totalVertices = _arena.TotalVertexCount;
            if (totalVertices <= 0) return;
            int bytes = totalVertices * 2 * sizeof(float);

            // Build a single float[] containing all primitive flattened data.
            // The engine (caller) is responsible to ensure primitives' RegisterRawGeometry stored correct offsets in arena
            var allData = new float[totalVertices * 2];
            int writePos = 0;
            // We assume that GeometryArena.FlattenContours used same ordering as allocations.
            // For PoC we'll expect engine to maintain correct ordering and provide flattened array per allocation.
            // Here we will query primitives and fill in their flattened arrays into the buffer at the appropriate offsets.
            foreach (var p in _primitives)
            {
                if (p.VertexOffsetRaw < 0 || p.VertexCount <= 0) continue;
                // Expect that Primitive subclasses have method GetFlattenedVertices (PolygonGPU, TextGPU)
                if (p is PolygonGpu poly)
                {
                    var flat = poly.GetFlattenedVertices();
                    for (int k = 0; k < flat.Length; k++)
                    {
                        int idx = (p.VertexOffsetRaw + k) * 2;
                        allData[idx + 0] = flat[k].X;
                        allData[idx + 1] = flat[k].Y;
                    }
                }
                else
                {
                    // Generic: we cannot extract raw flattened data unless primitive offers it.
                    // For safety, we write zeros for its allocated range.
                    for (int k = 0; k < p.VertexCount; k++)
                    {
                        int idx = (p.VertexOffsetRaw + k) * 2;
                        allData[idx + 0] = 0f;
                        allData[idx + 1] = 0f;
                    }
                }
            }

            // Upload to buffer
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ssboGeometry);
            GL.BufferData(BufferTarget.ArrayBuffer, bytes, allData, BufferUsageHint.DynamicDraw);
            // Also bind as SSBO base
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_GEOMETRY, _ssboGeometry);
        }

        #endregion

        #region Animation upload

        public void UploadPendingAnimationsAndIndex()
        {
            var newEntries = new List<AnimEntryCpu>();
            bool hasNew = false;

            foreach (var prim in _primitives)
            {
                for (int i = 0; i < prim.PendingAnimations.Count; i++)
                {
                    var entry = prim.PendingAnimations[i];
                    if (!entry.PendingOnGpu) continue;

                    if (entry.PrimitiveId < 0 || entry.PrimitiveId >= _primitiveCount)
                    {
                        DebugManager.Warn($"Invalid PrimitiveId {entry.PrimitiveId} in AnimEntry for type {(AnimType)entry.MetaType}. Skipping.");
                        continue;
                    }

                    newEntries.Add(entry);
                    entry.PendingOnGpu = false;
                    prim.PendingAnimations[i] = entry;
                    hasNew = true;
                }
            }

            if (!hasNew)
            {
                NO_ANIM_DEBUG_CURRENT += 1;
                if (DO_NO_ANIM_DEBUG && NO_ANIM_DEBUG_OUTPUT_RANGE == NO_ANIM_DEBUG_CURRENT)
                {
                    DebugManager.Anim("UploadPendingAnimationsAndIndex: No new animations. Skipping upload.");
                    NO_ANIM_DEBUG_CURRENT = 0;
                }
                return;
            }

            DebugManager.Anim($"UploadPendingAnimationsAndIndex: Uploading {newEntries.Count} NEW animation entries (total will be {_uploadedAnimEntries.Count + newEntries.Count})");

            int startIndex = _uploadedAnimEntries.Count;
            _uploadedAnimEntries.AddRange(newEntries);

            var allBytes = PrimitiveGpu.SerializeAnimEntries(_uploadedAnimEntries);

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboAnimEntries);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, allBytes.Length, allBytes, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

            var indices = PrimitiveGpu.BuildAnimIndex(_uploadedAnimEntries, _primitiveCount);
            var indexBytes = ToByteArray(indices);

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboAnimIndex);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, indexBytes.Length, indexBytes, BufferUsageHint.DynamicDraw);
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);

            DebugManager.Anim($"UploadPendingAnimationsAndIndex: Successfully uploaded {newEntries.Count} new entries. Total: {_uploadedAnimEntries.Count}");
        }

        #endregion

        #region Update & Dispatch

        public void UpdateAndDispatch(float time)
        {
            // Dispatch animation compute (updates RenderInstances and MorphDesc)
            GL.UseProgram(_programAnimCompute);
            GL.Uniform1(GL.GetUniformLocation(_programAnimCompute, "u_time"), time);
            int groups = (int)MathF.Ceiling(_primitiveCount / 64f);
            GL.DispatchCompute(groups, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

            // Dispatch morph compute for each primitive that has morphs
            GL.UseProgram(_programMorphCompute);
            int morphUniformLoc = GL.GetUniformLocation(_programMorphCompute, "u_primitiveId");
            for (int pid = 0; pid < _primitiveCount; pid++)
            {
                if (_morphDescs[pid].VertexCount <= 0 || _morphDescs[pid].CurrentT <= 0f) continue;

                GL.Uniform1(morphUniformLoc, pid);
                int morphGroups = (int)MathF.Ceiling(_morphDescs[pid].VertexCount / 256f);
                GL.DispatchCompute(morphGroups, 1, 1);
            }

            // Барьер после всех морф-вычислений: SSBO + данные вершинного буфера
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | 
                             MemoryBarrierFlags.VertexAttribArrayBarrierBit);

            GL.UseProgram(0);
        }

        #endregion

        #region Render

        public void RenderAll()
        {
            GL.UseProgram(_programRender);
            GL.BindVertexArray(_vao);

            int primLoc = GL.GetUniformLocation(_programRender, "u_primIndex");

            for (int pid = 0; pid < _primitiveCount; pid++)
            {
                var md = _morphDescs[pid];
                if (md.VertexCount <= 0) continue;

                int offset = md.OffsetM; // Use morphed geometry if available
                int count = md.VertexCount;

                GL.Uniform1(primLoc, pid);
                GL.DrawArrays(PrimitiveType.LineStrip, offset, count);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        #endregion

        #region Helpers
        
        public void RebuildAllDescriptors()
        {
            InitMorphDescsFromPrimitives();
            UploadMorphDescBuffer();
            InitRenderInstancesFromPrimitives();
            UploadRenderInstancesBuffer();
        }

        private static int CreateComputeProgram(string src, string nameForDebug)
        {
            int cs = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(cs, src);
            GL.CompileShader(cs);
            GL.GetShader(cs, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0)
            {
                string log = GL.GetShaderInfoLog(cs);
                throw new Exception($"Compute shader compile error ({nameForDebug}): {log}\nSource:\n{src}");
            }
            int prog = GL.CreateProgram();
            GL.AttachShader(prog, cs);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0)
            {
                string log = GL.GetProgramInfoLog(prog);
                throw new Exception($"Compute program link error ({nameForDebug}): {log}");
            }
            GL.DetachShader(prog, cs);
            GL.DeleteShader(cs);
            return prog;
        }

        private static int CreateProgram(string vsSrc, string fsSrc)
        {
            int vs = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vs, vsSrc);
            GL.CompileShader(vs);
            GL.GetShader(vs, ShaderParameter.CompileStatus, out int okv);
            if (okv == 0) throw new Exception("Vertex shader compile: " + GL.GetShaderInfoLog(vs));

            int fs = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fs, fsSrc);
            GL.CompileShader(fs);
            GL.GetShader(fs, ShaderParameter.CompileStatus, out int okf);
            if (okf == 0) throw new Exception("Fragment shader compile: " + GL.GetShaderInfoLog(fs));

            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vs);
            GL.AttachShader(prog, fs);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linked);
            if (linked == 0) throw new Exception("Program link: " + GL.GetProgramInfoLog(prog));

            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            return prog;
        }

        private static byte[] ToByteArray<T>(T[] arr) where T : struct
        {
            int size = Marshal.SizeOf<T>();
            var bytes = new byte[arr.Length * size];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                IntPtr ptr = handle.AddrOfPinnedObject();
                for (int i = 0; i < arr.Length; i++)
                {
                    IntPtr dst = ptr + i * size;
                    Marshal.StructureToPtr(arr[i], dst, false);
                }
            }
            finally { handle.Free(); }
            return bytes;
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            if (_programAnimCompute != -1) GL.DeleteProgram(_programAnimCompute);
            if (_programMorphCompute != -1) GL.DeleteProgram(_programMorphCompute);
            if (_programRender != -1) GL.DeleteProgram(_programRender);
            if (_ssboAnimEntries != -1) GL.DeleteBuffer(_ssboAnimEntries);
            if (_ssboAnimIndex != -1) GL.DeleteBuffer(_ssboAnimIndex);
            if (_ssboMorphDesc != -1) GL.DeleteBuffer(_ssboMorphDesc);
            if (_ssboGeometry != -1) GL.DeleteBuffer(_ssboGeometry);
            if (_ssboRenderInstances != -1) GL.DeleteBuffer(_ssboRenderInstances);
            if (_vao != -1) GL.DeleteVertexArray(_vao);
        }

        #endregion

        #region Inline shader sources

        // AnimationCompute: reads AnimEntries & AnimIndex, writes MorphDesc and RenderInstances
        // local_size_x must match the dispatch groups calculation (we used 64)
        private const string ANIMATION_COMPUTE_SRC = @"
#version 430
layout(local_size_x = 64) in;

struct AnimEntry {
    ivec4 meta; // type, pid, r2, r3
    vec4 times; // start, end, ease, r
    vec4 from;
    vec4 to;
    ivec4 morph; // offA, offB, offM, vcount
};

struct MorphDesc { float currentT; float easeType; float r0; float r1; ivec4 offsets; };
struct RenderInstance {
    vec4 row0;
    vec4 row1;
    vec4 row2;
    vec4 color;
    ivec4 meta;
    vec4 dash;
};

layout(std430, binding = 0) buffer AnimEntries { AnimEntry entries[]; };
layout(std430, binding = 1) buffer AnimIndex { ivec4 index[]; };
layout(std430, binding = 2) buffer MorphDescSB { MorphDesc morphs[]; };
layout(std430, binding = 4) buffer RenderInstances { RenderInstance instances[]; };

uniform float u_time;

float easeVal(float t, int e) {
    if (e == 0) return t;
    if (e == 1) return t*t;
    if (e == 2) return 1.0 - (1.0 - t)*(1.0 - t);
    if (e == 3) return t*t*(3.0 - 2.0*t);
    return t;
}

void main() {
    int pid = int(gl_GlobalInvocationID.x);
    if (pid >= index.length()) return;

    ivec4 idx = index[pid];
    int start = idx.x;
    int count = idx.y;

    RenderInstance inst = instances[pid];
    MorphDesc mdesc = morphs[pid];

    // Extract initial values from instance
    vec2 pos = inst.row2.xy;
    float rot = atan(inst.row1.y, inst.row1.x); // derive from matrix
    float scale = length(inst.row0.xy);
    vec4 color = inst.color;
    vec4 dash = inst.dash;
    ivec4 meta = inst.meta;

    for (int i = 0; i < count; i++) {
        AnimEntry e = entries[start + i];
        float st = e.times.x;
        float et = e.times.y;
        int easeI = int(e.times.z + 0.5);
        int type = e.meta.x;

        if (u_time < st) continue; // skip future
        if (u_time > et) { // already finished - apply final value
            if (type == 1) pos = e.to.xy;
            else if (type == 2) rot = e.to.x;
            else if (type == 3) scale = e.to.x;
            else if (type == 4) color = e.to;
            else if (type == 5) {
                mdesc.currentT = 1.0;
                mdesc.easeType = e.times.z;
                mdesc.offsets = e.morph;
            }
            else if (type == 6) dash.xy = e.to.xy;
            continue;
        }

        float progress = (u_time - st) / max(1e-6, et - st);
        progress = clamp(progress, 0.0, 1.0);
        progress = easeVal(progress, easeI);

        if (type == 1) { // Translate
            pos = mix(pos, e.to.xy, progress);
        } else if (type == 2) { // Rotate
            rot = mix(rot, e.to.x, progress);
        } else if (type == 3) { // Scale
            scale = mix(scale, e.to.x, progress);
        } else if (type == 4) { // Color
            color = mix(color, e.to, progress);
        } else if (type == 5) { // Morph
            mdesc.currentT = progress;
            mdesc.easeType = e.times.z;
            mdesc.offsets = e.morph;
        } else if (type == 6) { // Dash
            dash.xy = mix(dash.xy, e.to.xy, progress);
        }
    }

    // Rebuild transform matrix
    float c = cos(rot);
    float s = sin(rot);
    vec4 row0 = vec4(scale * c, scale * s, 0.0, 0.0);
    vec4 row1 = vec4(-scale * s, scale * c, 0.0, 0.0);
    vec4 row2 = vec4(pos.x, pos.y, 1.0, 0.0);

    // Write back
    inst.row0 = row0;
    inst.row1 = row1;
    inst.row2 = row2;
    inst.color = color;
    inst.dash = dash;
    instances[pid] = inst;

    morphs[pid] = mdesc;
}
";

        // Morph compute shader:
        private const string MORPH_COMPUTE_SRC = @"
#version 430
layout(local_size_x = 256) in;

struct MorphDesc { float currentT; float easeType; float r0; float r1; ivec4 offsets; };
layout(std430, binding = 2) buffer MorphDescSB { MorphDesc morphs[]; };

layout(std430, binding = 3) buffer GeometryArena { vec2 geom[]; };

// uniforms to control which primitive to process
uniform int u_primitiveId;

float easeValGeneric(float t, int e)
{
    if (e == 0) return t;
    if (e == 1) return t*t;
    if (e == 2) return 1.0 - (1.0 - t)*(1.0 - t);
    if (e == 3) return t*t*(3.0 - 2.0*t);
    return t;
}

void main()
{
    uint localIdx = gl_GlobalInvocationID.x; // per-vertex index within primitive
    int pid = u_primitiveId;
    MorphDesc md = morphs[pid];
    int vcount = md.offsets.w;
    if (vcount <= 0) return;
    if (int(localIdx) >= vcount) return;

    int offA = md.offsets.x;
    int offB = md.offsets.y;
    int offM = md.offsets.z;

    vec2 a = geom[offA + int(localIdx)];
    vec2 b = geom[offB + int(localIdx)];
    float t = clamp(md.currentT, 0.0, 1.0);
    int easeI = int(md.easeType + 0.5);
    float te = easeValGeneric(t, easeI);

    vec2 v = mix(a, b, te);
    geom[offM + int(localIdx)] = v;
}
";

        // Simple render shaders: vertex reads position from VBO (geom buffer) and reads render metadata from morphs (for transform/color)
        // For PoC we will read transform directly from MorphDesc array to position vertices (we stored row2.x/y in morph.r0/r1? In production - use proper RenderInstances)
        private const string VERTEX_RENDER_SRC = @"
#version 430
layout(location = 0) in vec2 in_pos;
uniform int u_primIndex;

struct RenderInstance {
    vec4 row0;
    vec4 row1;
    vec4 row2;
    vec4 color;
    ivec4 meta;
    vec4 dash;
};

layout(std430, binding = 4) buffer RenderInstances {
    RenderInstance instances[];
};

void main() {
    RenderInstance inst = instances[u_primIndex];
    vec4 r0 = inst.row0;
    vec4 r1 = inst.row1;
    vec4 r2 = inst.row2;

    mat2 rs = mat2(r0.xy, r1.xy);
    vec2 p = rs * in_pos + r2.xy;

    // Skip rendering if NaN (contour separator)
    if (isnan(in_pos.x) || isnan(in_pos.y)) {
        gl_Position = vec4(0.0, 0.0, 0.0, 0.0); // Or discard in frag, but this prevents drawing
    } else {
        gl_Position = vec4(p, 0.0, 1.0);
    }
}
";

        private const string FRAGMENT_RENDER_SRC = @"
#version 430
out vec4 outColor;
uniform int u_primIndex;

struct RenderInstance {
    vec4 row0;
    vec4 row1;
    vec4 row2;
    vec4 color;
    ivec4 meta;
    vec4 dash;
};

layout(std430, binding = 4) buffer RenderInstances {
    RenderInstance instances[];
};

void main(){
    outColor = instances[u_primIndex].color;
    // TODO: If you add discard for NaN or other effects, do it here.
}
";
        #endregion
    }
}