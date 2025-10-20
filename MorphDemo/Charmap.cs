using OpenTK.Mathematics;

namespace PhysicsSimulation
{
    public static class CharMap
    {
        public static List<Vector2> GetCharVerts(char c, float offsetX, float size) => char.ToUpper(c) switch
        {
            'A' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(0.0f + offsetX, 0.5f * size), new(0.25f * size + offsetX, -0.5f * size),
                new(0.125f * size + offsetX, 0.0f), new(-0.125f * size + offsetX, 0.0f)
            ],
            'B' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, 0.5f * size),
                new(0.25f * size + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.0f),
                new(-0.125f * size + offsetX, 0.0f), new(-0.125f * size + offsetX, -0.5f * size),
                new(-0.25f * size + offsetX, -0.5f * size)
            ],
            'C' => [
                new(0.25f * size + offsetX, -0.5f * size), new(0.0f + offsetX, -0.5f * size),
                new(-0.25f * size + offsetX, -0.25f * size), new(-0.25f * size + offsetX, 0.25f * size),
                new(0.0f + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.5f * size)
            ],
            'D' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, 0.5f * size),
                new(0.0f + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.25f * size),
                new(0.25f * size + offsetX, -0.25f * size), new(0.0f + offsetX, -0.5f * size),
                new(-0.25f * size + offsetX, -0.5f * size)
            ],
            'E' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, 0.5f * size),
                new(0.25f * size + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.0f),
                new(-0.25f * size + offsetX, 0.0f), new(0.25f * size + offsetX, 0.0f),
                new(0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, -0.5f * size)
            ],
            'F' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, 0.5f * size),
                new(0.25f * size + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.0f),
                new(-0.25f * size + offsetX, 0.0f), new(0.25f * size + offsetX, 0.0f),
                new(0.25f * size + offsetX, -0.5f * size)
            ],
            'G' => [
                new(0.25f * size + offsetX, -0.5f * size), new(0.0f + offsetX, -0.5f * size),
                new(-0.25f * size + offsetX, -0.25f * size), new(-0.25f * size + offsetX, 0.25f * size),
                new(0.0f + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.5f * size),
                new(0.25f * size + offsetX, 0.0f), new(0.0f + offsetX, 0.0f)
            ],
            'I' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(0.25f * size + offsetX, -0.5f * size),
                new(0.0f + offsetX, -0.5f * size), new(0.0f + offsetX, 0.5f * size),
                new(-0.25f * size + offsetX, 0.5f * size), new(0.25f * size + offsetX, 0.5f * size)
            ],
            'J' => [
                new(0.25f * size + offsetX, -0.5f * size), new(0.0f + offsetX, -0.5f * size),
                new(-0.25f * size + offsetX, -0.25f * size), new(-0.25f * size + offsetX, 0.5f * size),
                new(0.25f * size + offsetX, 0.5f * size)
            ],
            'K' => [
                new(-0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, 0.5f * size),
                new(-0.25f * size + offsetX, 0.0f), new(0.25f * size + offsetX, 0.5f * size),
                new(0.25f * size + offsetX, -0.5f * size), new(-0.25f * size + offsetX, 0.0f)
            ],
            _ => []
        };
    }
}