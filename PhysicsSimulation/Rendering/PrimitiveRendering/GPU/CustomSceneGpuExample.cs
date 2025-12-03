using Vector4 = System.Numerics.Vector4;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public class CustomSceneGpuExample(GeometryArena arena) : SceneGpu(arena)
    {
        public override void Setup()
        {
            Add(new PlotGpu(x => MathF.Sin(x * 5f) * 0.3f, -1f, 1f))
                .AnimateColor(0, 1, EaseType.EaseInOut, new Vector4(0f, 1f, 1f, 1f));

            Add(new PlotGpu(x => x * x * T - 0.5f, -0.5f, 0.5f, isDynamic: true)).AnimateColor(0, 1, EaseType.EaseInOut,
                new Vector4(1f, 0.7f, 0f, 1f));

            Add(new PlotGpu(x => MathF.Pow(MathF.Abs(x), 2f / 3f) +
                                 0.9f * MathF.Sqrt(MathF.Max(0f, 1f - x * x)) *
                                 MathF.Sin(20f * MathF.PI * x + T * 2f), -1f, 1f, isDynamic: true))
                .AnimateColor(to: new Vector4(1f, 0.1f, 0.3f, 1f));

            Add(new PlotGpu(x => MathF.Sin(x * 10f + T * 4f) * 0.22f +
                                 MathF.Cos(x * 23f + T * 2.7f) * 0.06f, -1f, 1f, isDynamic: true))
                .AnimateColor(to: new Vector4(0.2f, 1f, 0.6f, 1f));
        }
    }
}