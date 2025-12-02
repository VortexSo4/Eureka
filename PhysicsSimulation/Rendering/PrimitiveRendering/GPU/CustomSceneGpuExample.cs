// File: CustomSceneExample.cs

using System.Numerics;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public class CustomSceneGpuExample(GeometryArena arena) : SceneGpu(arena)
    {
        /// <summary>
        /// Создаем примитивы и задаем анимации
        /// </summary>
        public void Setup()
        {
            // Примитив 1: простая линия
            var line = new PolygonGpu
            {
                PrimitiveId = 0,
                VertexOffsetRaw = 0,
                VertexCount = 4
            };
            line.SetVertices(new Vector2[]
            {
                new(0f, 0f),
                new(0.5f, 0f),
                new(0.5f, 0.5f),
                new(0f, 0.5f)
            });

            AddPrimitive(line);

            // Примитив 2: еще одна линия
            var line2 = new PolygonGpu
            {
                PrimitiveId = 1,
                VertexOffsetRaw = 4,
                VertexCount = 4
            };
            line2.SetVertices(new Vector2[]
            {
                new(-0.5f, -0.5f),
                new(0f, -0.5f),
                new(0f, 0f),
                new(-0.5f, 0f)
            });

            AddPrimitive(line2);

            // Фон: плавно меняем цвет через 5 секунд
            AnimateBackground(new Vector3(0.1f, 0.4f, 0.8f), 5f);

            // Анимации примитивов
            // Линия 1: перемещение вправо за 3 секунды
            line.ScheduleAnimTranslate(new Vector2(0f, 0f), new Vector2(0.3f, 0.2f), 0f, 3f);

            // Линия 2: смена цвета (через анимацию типа COLOR)
            line2.ScheduleAnimColor(new Vector4(1f, 0f, 0f, 1f), new Vector4(0f, 1f, 0f, 1f), 0f, 4f);

            // Инициализация AnimationEngine (загрузка геометрии и буферов)
            Initialize();
        }
    }
}
