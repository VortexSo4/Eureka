using OpenTK.Mathematics;
using PhysicsSimulation.Rendering.SceneRendering;
using Vector4 = System.Numerics.Vector4;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public class CustomSceneGpuExample(GeometryArena arena) : SceneGpu(arena)
    {
        public void Setup()
        {
            var scene = this;
            var sine = new PlotGpu(
                x => MathF.Sin(x * 5f) * 0.3f,
                xMin: -1f, xMax: 1f, resolution: 80, name: "SineWave"
            );
            sine.Color = new Vector4(0f, 1f, 1f, 1f);
            AddPrimitive(sine);

            var parabola = new PlotGpu(
                x => x * x * scene.Time - 0.5f,
                xMin: -0.5f, xMax: 0.5f, resolution: 40, name: "Parabola"
            );
            parabola.Color = new Vector4(1f, 0.7f, 0f, 1f);
            AddPrimitive(parabola);

            // Анимация диапазона — будет работать через Update()
            parabola.AnimateRange(toMin: -1.5f, toMax: 1.5f, duration: 4f, ease: EaseType.EaseInOut);

            var heart = new PlotGpu(
                x => MathF.Pow(MathF.Abs(x), 2f / 3f) +
                     0.9f * MathF.Sqrt(MathF.Max(0f, 1f - x * x)) *
                     MathF.Sin(20f * MathF.PI * x + scene.Time * 2f),
                xMin: -1f, xMax: 1f, resolution: 120, name: "Heart"
            );
            heart.Color = new Vector4(1f, 0.1f, 0.3f, 1f);
            AddPrimitive(heart);

            var wave = new PlotGpu(
                x => MathF.Sin(x * 10f + scene.Time * 4f) * 0.22f +
                     MathF.Cos(x * 23f + scene.Time * 2.7f) * 0.06f,
                xMin: -1f, xMax: 1f, resolution: 120, name: "LiveWave"
            );
            wave.Color = new Vector4(0.2f, 1f, 0.6f, 1f);
            AddPrimitive(wave);

            // Сохраняем ссылки для Update()
            _heart = heart;
            _wave = wave;
            _parabola = parabola;
        }

        private PlotGpu? _heart;
        private PlotGpu? _wave;
        private PlotGpu? _parabola;

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);

            _heart?.UpdateRange(_heart.XMin, _heart.XMax);
            _wave?.UpdateRange(_wave.XMin, _wave.XMax);
            if (_parabola != null && Time >= 0 && Time <= 4f)
                _parabola.UpdateRange(MathHelper.Lerp(-0.5f, -1.5f, Time / 4f), MathHelper.Lerp(0.5f, 1.5f, Time / 4f));
        }
    }
}