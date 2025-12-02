using System.Numerics;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public class CustomSceneGpuExample(GeometryArena arena) : SceneGpu(arena)
    {
        public void Setup()
        {
            // Примитив 1: линия
            var line = new PolygonGpu();
            line.InitGeometry(_arena, [
                [new Vector2(0f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0.5f)]
            ]);
            AddPrimitive(line);

            // Примитив 2: другая линия
            var line2 = new PolygonGpu();
            line2.InitGeometry(_arena, [
                [new Vector2(-0.5f, -0.5f), new Vector2(0f, -0.5f), new Vector2(0f, 0f), new Vector2(-0.5f, 0f)]
            ]);
            AddPrimitive(line2);

            // Анимация фона
            AnimateBackground(new Vector3(1.0f, 0.0f, 0.0f), 0, 5);
            AnimateBackground(new Vector3(0.1f, 0.4f, 0.8f), 5, 10);

            // --- Анимации примитивов ---
            // Line1: перемещение, масштаб, поворот
            line.AnimatePosition(0f, 3f, EaseType.EaseInOut, new Vector2(0f, 0f), new Vector2(0.3f, 0.2f));
            line.AnimateScale(0f, 3f, EaseType.EaseInOut, 1f, 1.5f);
            line.ScheduleAnimation(AnimType.Rotate, 0f, 3f, EaseType.EaseInOut,
                new Vector4(0f,0f,0f,0f), new Vector4(MathF.PI/2f,0f,0f,0f));

            // Line2: перемещение, цвет
            line2.AnimatePosition(1f, 4f, EaseType.EaseInOut, new Vector2(-0.5f, -0.5f), new Vector2(0.2f, 0.3f));
            line2.AnimateColor(1f, 4f, EaseType.EaseInOut,
                new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 1f, 0f, 1f));

            // Инициализация движка анимаций
            Initialize();
        }
    }
}