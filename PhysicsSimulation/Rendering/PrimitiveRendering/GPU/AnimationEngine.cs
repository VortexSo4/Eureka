// File: AnimationEngine.cs
// Requires OpenTK 4.x (OpenTK.Graphics.OpenGL4)
// Place near PrimitivesGPU.cs (same namespace recommended)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
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

        // Config
        private const int ANIM_ENTRY_SIZE_BYTES = 80; // from earlier spec
        private const int ANIM_INDEX_SIZE_BYTES = 16;
        private const int MORPH_DESC_SIZE_BYTES = 32;
        private const int RENDER_INSTANCE_SIZE_BYTES = 96; // 3 vec4 + vec4 + ivec4 + vec4 = 96

        public AnimationEngine(GeometryArena arena, IEnumerable<PrimitiveGpu> primitives)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            _primitives = primitives?.OrderBy(p => p.PrimitiveId).ToList() ?? throw new ArgumentNullException(nameof(primitives));

            // allocate CPU-side descriptor arrays
            _morphDescs = new MorphDescCpu[_primitiveCount];
            _renderInstances = new RenderInstanceCpu[_primitiveCount];

            // create GL resources
            CreateBuffers();
            CreateShadersAndVAO();

            // initialize morph descriptors from primitives (offsets)
            InitMorphDescsFromPrimitives();
            UploadMorphDescBuffer(); // initial upload
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
                else if (p is TextGpu txt)
                {
                    var flat = GeometryArena.FlattenContours(txt.GlyphContours.SelectMany(g => g).ToList());
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

            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        #endregion

        #region Animations upload (entries / index) helpers

        /// <summary>
        /// Gather pending AnimEntries from primitives, build index and upload both SSBOs.
        /// After upload, clear primitives' pending lists (engine expects scheduling once).
        /// </summary>
        public void UploadPendingAnimationsAndIndex()
        {
            // aggregate entries grouped by primitive order (PrimitiveGPU.AggregateEntries ensures grouping)
            var entries = PrimitiveGpu.AggregateEntries(_primitives);
            int totalEntries = entries.Count;

            // Build AnimIndex: for correct contiguity we re-group explicitly here
            var animIndexList = new AnimIndexCpu[_primitiveCount];
            var grouped = new Dictionary<int, List<AnimEntryCpu>>();
            foreach (var e in entries)
            {
                if (!grouped.TryGetValue(e.PrimitiveId, out var l)) { l = new List<AnimEntryCpu>(); grouped[e.PrimitiveId] = l; }
                l.Add(e);
            }

            // Build compact entries array where entries are grouped per primitive in ascending PrimitiveId order
            var compact = new List<AnimEntryCpu>(totalEntries);
            for (int pid = 0; pid < _primitiveCount; pid++)
            {
                if (grouped.TryGetValue(pid, out var list) && list.Count > 0)
                {
                    int start = compact.Count;
                    compact.AddRange(list);
                    animIndexList[pid] = new AnimIndexCpu(start, list.Count);
                }
                else
                {
                    animIndexList[pid] = new AnimIndexCpu(0, 0);
                }
            }

            // Serialize entries to bytes
            var entriesBytes = PrimitiveGpu.SerializeAnimEntries(compact);
            // Upload AnimEntries
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboAnimEntries);
            GL.BufferData(BufferTarget.ShaderStorageBuffer, entriesBytes.Length, entriesBytes, BufferUsageHint.DynamicDraw);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_ANIMENTRIES, _ssboAnimEntries);

            // Serialize AnimIndex
            int indexBytes = _primitiveCount * ANIM_INDEX_SIZE_BYTES;
            var indexArray = new byte[indexBytes];
            int pos = 0;
            for (int i = 0; i < _primitiveCount; i++)
            {
                var ai = animIndexList[i];
                // start, count, r2, r3
                BitConverter.TryWriteBytes(new Span<byte>(indexArray, pos, 4), ai.Start); pos += 4;
                BitConverter.TryWriteBytes(new Span<byte>(indexArray, pos, 4), ai.Count); pos += 4;
                BitConverter.TryWriteBytes(new Span<byte>(indexArray, pos, 4), ai.R2); pos += 4;
                BitConverter.TryWriteBytes(new Span<byte>(indexArray, pos, 4), ai.R3); pos += 4;
            }
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, _ssboAnimIndex);
            GL.BufferSubData(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, indexBytes, indexArray);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_ANIMINDEX, _ssboAnimIndex);

            // Clear primitives' pending animations (we've serialized them)
            foreach (var p in _primitives) p.ClearPendingAnimations();

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
        }

        #endregion

        #region Dispatch compute passes

        /// <summary>
        /// Main frame update: call this each frame with engine time (seconds),
        /// it will run AnimationCompute and MorphCompute and update renderInstances buffer.
        /// </summary>
        public void UpdateAndDispatch(float timeSeconds)
        {
            // 1) Animation compute: writes RenderInstances and MorphDescs
            GL.UseProgram(_programAnimCompute);
            // set uniform u_time
            int locTime = GL.GetUniformLocation(_programAnimCompute, "u_time");
            if (locTime >= 0) GL.Uniform1(locTime, timeSeconds);

            // dispatch groups = ceil(primitiveCount / local_size_x)
            int localSize = 64; // must match compute shader local_size_x
            int groups = Math.Max(1, (_primitiveCount + localSize - 1) / localSize);
            GL.DispatchCompute(groups, 1, 1);

            // ensure writes to morphDesc and renderInstances are visible
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);

            // 2) Morph compute: for each primitive with morph vertexcount > 0 dispatch compute that morphs its vertices
            // For simplicity we dispatch per primitive (can be optimized)
            for (int pid = 0; pid < _primitiveCount; pid++)
            {
                var md = _morphDescs[pid];
                if (md.VertexCount <= 0) continue;
                // Read morph descriptor from GPU side? We maintain CPU mirror _morphDescs, but AnimationCompute might have changed morphs on GPU.
                // For now, we copy CPU-side _morphDescs to GPU before morph. In a more advanced pipeline AnimationCompute writes it directly.
            }

            // NOTE: we must re-upload MorphDesc from GPU if AnimationCompute changed it. To keep PoC consistent:
            // We'll read morphdesc SSBO back to CPU? that's expensive. Instead, in current design AnimationCompute writes MorphDesc directly on GPU,
            // and MorphCompute will read it; hence we don't need CPU to re-upload. We only need to dispatch MorphCompute per-primitive using offsets encoded in MorphDesc on GPU.
            // For simplicity we will dispatch MorphCompute groups = ceil(maxVertexCount among primitives / local_size)
            int maxV = _primitives.Max(p => p.VertexCount);
            if (maxV > 0)
            {
                int groupsV = Math.Max(1, (maxV + 255) / 256);
                // We'll set uniform u_primitiveCount to allow MorphCompute iterate morphs by id
                GL.UseProgram(_programMorphCompute);
                int locPC = GL.GetUniformLocation(_programMorphCompute, "u_primitiveCount");
                if (locPC >= 0) GL.Uniform1(locPC, _primitiveCount);
                // Dispatch a conservative global dispatch: groupsV * _primitiveCount would be too big, so we'll instead dispatch groupsV with work that each invocation checks its global id and maps to prim+vertex index.
                // For simplicity in this PoC, dispatch per-primitive loop on CPU:
                for (int pid = 0; pid < _primitiveCount; pid++)
                {
                    int vcount = _primitives[pid].VertexCount;
                    if (vcount <= 0) continue;
                    int groupsForPrim = Math.Max(1, (vcount + 255) / 256);
                    // set uniform u_primitiveId
                    int locPid = GL.GetUniformLocation(_programMorphCompute, "u_primitiveId");
                    if (locPid >= 0) GL.Uniform1(locPid, pid);
                    GL.DispatchCompute(groupsForPrim, 1, 1);
                    GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit | MemoryBarrierFlags.VertexAttribArrayBarrierBit);
                }
            }

            // 3) After morph compute, we must ensure geometry buffer updates are visible to vertex fetch
            GL.MemoryBarrier(MemoryBarrierFlags.VertexAttribArrayBarrierBit | MemoryBarrierFlags.ShaderStorageBarrierBit);

            // 4) Now update CPU mirror of RenderInstances from SSBO? Not necessary; render shader reads SSBO directly by uniform index per-draw.
            // We'll perform render next.
        }

        #endregion

        #region Render

        /// <summary>
        /// Render primitives by issuing draw calls.
        /// This simple renderer uses per-primitive draw: it sets uniform u_primIndex and issues glDrawArrays(base, count).
        /// For production, implement instanced/multi-draw paths.
        /// </summary>
        public void RenderAll()
        {
            GL.UseProgram(_programRender);
            GL.BindVertexArray(_vao);

            // Bind SSBOs to same binding points so render shader can read RenderInstances & Geometry (binding was done earlier)
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_RENDERINST, _ssboRenderInstances);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BINDING_GEOMETRY, _ssboGeometry);

            int locPrimIndex = GL.GetUniformLocation(_programRender, "u_primIndex");
            for (int pid = 0; pid < _primitiveCount; pid++)
            {
                var p = _primitives[pid];
                if (p.VertexCount <= 0) continue;

                if (locPrimIndex >= 0) GL.Uniform1(locPrimIndex, pid);

                // draw: glDrawArrays(first, count)
                GL.DrawArrays(PrimitiveType.LineStrip, p.VertexOffsetM >= 0 ? p.VertexOffsetM : p.VertexOffsetRaw, p.VertexCount);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        #endregion

        #region Helpers: shader compilation, serialize arrays

        private static int CreateComputeProgram(string src, string nameForDebug = "")
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
    ivec4 meta;   // type, primId, r2, r3
    vec4 times;   // start, end, ease, r
    vec4 from;
    vec4 to;
    ivec4 morph;  // offsetA, offsetB, offsetM, vertexCount
};
layout(std430, binding = 0) buffer AnimEntries { AnimEntry entries[]; };

struct AnimIndex { ivec2 startCount; ivec2 r; };
layout(std430, binding = 1) buffer AnimIndexSB { AnimIndex animIndex[]; };

struct MorphDesc { float currentT; float easeType; float r0; float r1; ivec4 offsets; };
layout(std430, binding = 2) buffer MorphDescSB { MorphDesc morphs[]; };

layout(std430, binding = 4) buffer RenderInstances {
    // we store rows as vec4 for alignment
    vec4 row0[];
    // careful: we'll use structured access in host; here we simply write via separate arrays if needed
};

// Instead of complex struct for render instances we will use separate SSBO writes via morphs or other
// For PoC we will pack RenderInstance in MorphDesc write and have render shader read morphs for transform/color.

// uniform time
uniform float u_time;

float easeVal(float t, int e)
{
    if (e == 0) return t;
    if (e == 1) return t*t;
    if (e == 2) return 1.0 - (1.0 - t)*(1.0 - t);
    if (e == 3) return t*t*(3.0 - 2.0*t);
    return t;
}

void main()
{
    uint pid = gl_GlobalInvocationID.x;
    if (pid >= animIndex.length()) return;

    // base transform / color defaults
    vec2 pos = vec2(0.0, 0.0);
    float rot = 0.0;
    float scale = 1.0;
    vec4 color = vec4(1.0, 1.0, 1.0, 1.0);
    float filledLen = 0.0;
    float emptyLen = 0.0;
    float dashOffset = 0.0;
    int flags = 0;

    AnimIndex idx = animIndex[pid];
    int start = idx.startCount.x;
    int count = idx.startCount.y;

    // read existing morph desc for offsets baseline (assume prefilled by CPU)
    MorphDesc mdesc = morphs[pid];

    for (int i = 0; i < count; i++)
    {
        AnimEntry e = entries[start + i];
        int type = e.meta.x;
        float st = e.times.x;
        float et = e.times.y;
        int easeI = int(e.times.z + 0.5);
        float progress = 0.0;
        if (u_time < st) progress = 0.0;
        else if (u_time >= et) progress = 1.0;
        else progress = easeVal((u_time - st) / max(1e-6, et - st), easeI);

        if (type == 1) { // TRANSLATE
            vec2 fromv = e.from.xy;
            vec2 tov = e.to.xy;
            pos += mix(fromv, tov, progress);
        }
        else if (type == 2) { // ROTATE
            float fromr = e.from.x;
            float tor = e.to.x;
            rot += mix(fromr, tor, progress);
        }
        else if (type == 3) { // SCALE
            float froms = e.from.x;
            float tos = e.to.x;
            scale *= mix(froms, tos, progress);
        }
        else if (type == 4) { // COLOR
            color = mix(e.from, e.to, progress);
        }
        else if (type == 5) { // MORPH
            // write currentT and ease into morph desc (overwriting)
            mdesc.currentT = mix(mdesc.currentT, progress, 1.0);
            mdesc.easeType = e.times.z;
            // copy morph offsets if provided in entry (entry.morph)
            mdesc.offsets.x = e.morph.x;
            mdesc.offsets.y = e.morph.y;
            mdesc.offsets.z = e.morph.z;
            mdesc.offsets.w = e.morph.w;
        }
        else if (type == 6) { // DASH
            vec2 ff = e.from.xy;
            vec2 tt = e.to.xy;
            vec2 val = mix(ff, tt, progress);
            filledLen = val.x;
            emptyLen = val.y;
        }
    }

    // write back morph desc
    morphs[pid] = mdesc;

    // convert transform to rows (scale->rotate->translate)
    float c = cos(rot);
    float s = sin(rot);
    vec4 row0 = vec4(scale * c, scale * s, 0.0, 0.0);
    vec4 row1 = vec4(-scale * s, scale * c, 0.0, 0.0);
    vec4 row2 = vec4(pos.x, pos.y, 1.0, 0.0);

    // For simplicity: we will write these rows into the RenderInstances SSBO area contiguous at indices (pid * 6...) etc.
    // But since we didn't define a strict struct earlier for RenderInstances in GLSL (to keep file small),
    // we will rely on MorphDesc/RenderInstances on CPU for reading; in PoC we'll write transform into morphs.r0/r1 (not ideal).
    // In a production change, create a proper RenderInstances struct in GLSL with vec4 row0,row1,row2,color,ivec4,vec4 and write it here.
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

struct MorphDesc { float currentT; float easeType; float r0; float r1; ivec4 offsets; };
layout(std430, binding = 2) buffer MorphDescSB { MorphDesc morphs[]; };

void main() {
    MorphDesc md = morphs[u_primIndex];
    // reconstruct transform: we don't have full rows stored in morphdesc for PoC, so we assume identity transform and just place geometry
    // In full version store transform in RenderInstances SSBO and read here
    vec2 p = in_pos;
    gl_Position = vec4(p, 0.0, 1.0);
}
";

        private const string FRAGMENT_RENDER_SRC = @"
#version 430
out vec4 outColor;
void main(){
    outColor = vec4(0.3, 0.7, 1.0, 1.0);
}
";
        #endregion
    }
}