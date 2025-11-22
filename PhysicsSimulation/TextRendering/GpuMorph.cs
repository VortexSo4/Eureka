using System;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace PhysicsSimulation
{
    /// <summary>
    /// Полностью автономный GPU-морфинг через compute shader.
    /// Никаких внешних .comp файлов — шейдер живёт прямо в C# коде.
    /// </summary>
    public sealed class GpuMorph : IDisposable
    {
        public int VertCount { get; }
        private readonly int _ssboStart;
        private readonly int _ssboTarget;
        private readonly int _ssboOutput;
        private readonly int _computeProgram;
        private readonly int _workGroups;

        // Uniform locations (кешируем)
        private readonly int _locT;
        private readonly int _locVertCount;
        private readonly int _locEasingType; // 0=Linear, 1=EaseInOut, 2=EaseOutBack и т.д.

        private const string ComputeSource = @"
        #version 430 core
        layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;
        
        layout(std430, binding = 0) restrict readonly buffer StartBuffer   { vec2 startVerts[]; };
        layout(std430, binding = 1) restrict readonly buffer TargetBuffer  { vec2 targetVerts[]; };
        layout(std430, binding = 2) restrict writeonly buffer OutputBuffer { vec2 resultVerts[]; };
        
        uniform float t;           // 0..1 (уже с учётом easing)
        uniform int   vertCount;
        
        void main()
        {
            uint i = gl_GlobalInvocationID.x;
            if (i >= uint(vertCount)) return;
        
            vec2 a = startVerts[i];
            vec2 b = targetVerts[i];
        
            float eased = t;
        
            resultVerts[i] = mix(a, b, eased);
        }
        ";
        
        public GpuMorph(IReadOnlyList<Vector2> startVerts, IReadOnlyList<Vector2> targetVerts, EaseType ease = EaseType.EaseInOut)
        {
            if (startVerts.Count != targetVerts.Count)
                throw new ArgumentException("Start and target vertex count must be equal");

            VertCount = startVerts.Count;
            if (VertCount == 0) throw new ArgumentException("Vertex count cannot be zero");

            _workGroups = (VertCount + 255) / 256;

            // Создаём SSBO
            _ssboStart  = CreateSSBO(startVerts);
            _ssboTarget = CreateSSBO(targetVerts);
            _ssboOutput = CreateSSBO(new Vector2[VertCount]); // пустой буфер на старте

            // Компилируем compute shader из строки
            _computeProgram = CompileComputeProgram(ComputeSource);

            // Биндим SSBO к фиксированным binding points
            GL.UseProgram(_computeProgram);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, _ssboStart);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, _ssboTarget);
            GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, _ssboOutput);

            // Кешируем uniform locations
            _locT         = GL.GetUniformLocation(_computeProgram, "t");
            _locVertCount = GL.GetUniformLocation(_computeProgram, "vertCount");
            _locEasingType = GL.GetUniformLocation(_computeProgram, "easingType");
            GL.UseProgram(0);
        }

        private static int CreateSSBO(IReadOnlyList<Vector2> data)
        {
            int ssbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);

            // Правильный способ: копируем в массив → пинним массив
            Vector2[] array = data as Vector2[] ?? data.ToArray();

            IntPtr ptr = IntPtr.Zero;
            GCHandle handle = default;

            try
            {
                handle = GCHandle.Alloc(array, GCHandleType.Pinned);
                ptr = handle.AddrOfPinnedObject();

                int size = array.Length * Vector2.SizeInBytes;
                GL.BufferData(BufferTarget.ShaderStorageBuffer, size, ptr, BufferUsageHint.StaticDraw);
            }
            finally
            {
                if (handle.IsAllocated)
                    handle.Free();
            }

            GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
            return ssbo;
        }

        private static int CompileComputeProgram(string source)
        {
            int shader = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out int status);
            if (status == 0)
            {
                string log = GL.GetShaderInfoLog(shader);
                GL.DeleteShader(shader);
                throw new Exception($"Compute shader compilation failed:\n{log}");
            }

            int program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out status);
            if (status == 0)
            {
                string log = GL.GetProgramInfoLog(program);
                GL.DeleteProgram(program);
                GL.DeleteShader(shader);
                throw new Exception($"Compute program linking failed:\n{log}");
            }

            GL.DetachShader(program, shader);
            GL.DeleteShader(shader);
            return program;
        }

        public void Update(float t)
        {
            GL.UseProgram(_computeProgram);
            GL.Uniform1(_locT, t);
            GL.Uniform1(_locVertCount, VertCount);
            // _locEasingType можно использовать позже

            GL.DispatchCompute(_workGroups, 1, 1);
            GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
            GL.UseProgram(0);
        }

        public void BindOutputAsArrayBuffer()
        {
            GL.BindBuffer(BufferTarget.ArrayBuffer, _ssboOutput);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, Vector2.SizeInBytes, 0);
        }

        public void Dispose()
        {
            if (_ssboStart  != 0) { GL.DeleteBuffer(_ssboStart); }
            if (_ssboTarget != 0) { GL.DeleteBuffer(_ssboTarget); }
            if (_ssboOutput != 0) { GL.DeleteBuffer(_ssboOutput); }
            if (_computeProgram != 0) { GL.DeleteProgram(_computeProgram); }
        }
        
        public List<Vector2> GetCurrentVertices()
        {
            var verts = new Vector2[VertCount];
            GL.BindBuffer(BufferTarget.CopyReadBuffer, _ssboOutput);
            GL.GetBufferSubData(BufferTarget.CopyReadBuffer, IntPtr.Zero, VertCount * Vector2.SizeInBytes, verts);
            GL.BindBuffer(BufferTarget.CopyReadBuffer, 0);
            return verts.ToList();
        }
    }
}