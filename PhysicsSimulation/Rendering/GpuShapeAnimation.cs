using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using PhysicsSimulation.Base.Utilities;
using PhysicsSimulation.Rendering.PrimitiveRendering;

namespace PhysicsSimulation.Rendering;

public sealed class GpuShapeAnimation
{
    public List<Vector2> Start { get; }
    public List<Vector2> Target { get; }
    public float Duration { get; }
    public EaseType Ease { get; }
    
    public float Elapsed { get; set; }
    public bool IsFinished => Elapsed >= Duration;
    
    // GPU ресурсы
    public int SsboSource { get; }
    public int SsboTarget { get; }
    public int SsboOutput { get; }
    public int VertexCount { get; }

    private static int _computeProgram = -1;
    private static bool _initialized = false;

    public GpuShapeAnimation(List<Vector2> start, List<Vector2> target, float duration, EaseType ease)
    {
        Start = start;
        Target = target;
        Duration = Math.Max(0.01f, duration);
        Ease = ease;
        VertexCount = Math.Max(start.Count, target.Count);

        // Инициализация compute шейдера один раз
        if (!_initialized)
        {
            string compSource = File.ReadAllText("Assets/Shaders/morph.comp");
            _computeProgram = Helpers.LoadComputeShader(compSource);
            _initialized = true;
        }

        // Создаём SSBO
        SsboSource = GL.GenBuffer();
        SsboTarget = GL.GenBuffer();
        SsboOutput = GL.GenBuffer();

        // Загружаем данные
        UploadSsbo(SsboSource, start);
        UploadSsbo(SsboTarget, target);
        
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, SsboOutput);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, VertexCount * Vector2.SizeInBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    private void UploadSsbo(int ssbo, List<Vector2> data)
    {
        var padded = new Vector2[VertexCount];
        for (int i = 0; i < VertexCount; i++)
            padded[i] = i < data.Count ? data[i] : new Vector2(float.NaN, float.NaN);

        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
        GL.BufferData(BufferTarget.ShaderStorageBuffer, VertexCount * Vector2.SizeInBytes, padded, BufferUsageHint.StaticDraw);
        GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
    }

    public void UpdateAndDispatch(float dt)
    {
        Elapsed += dt;
        float tRaw = Math.Min(1f, Elapsed / Duration);
        float t = Easing.Ease(Ease, tRaw);

        // Dispatch compute shader
        GL.UseProgram(_computeProgram);
        
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, SsboSource);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, SsboTarget);
        GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 2, SsboOutput);
        
        int tLoc = GL.GetUniformLocation(_computeProgram, "t");
        GL.Uniform1(tLoc, t);

        int groups = (VertexCount + 255) / 256;
        GL.DispatchCompute(groups, 1, 1);
        GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
    }

    public void BindOutputAsVertexBuffer()
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, SsboOutput);
    }

    public void Dispose()
    {
        GL.DeleteBuffer(SsboSource);
        GL.DeleteBuffer(SsboTarget);
        GL.DeleteBuffer(SsboOutput);
    }
}