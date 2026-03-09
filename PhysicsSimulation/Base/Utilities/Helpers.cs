using System.Numerics;

namespace PhysicsSimulation.Base.Utilities
{
    public static class Helpers
    {
        // ── Путь к папке приложения ────────────────────────────────────────────
        public static string GetApplicationPath(string subfolder)
        {
            string root = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null)
                {
                    if (dir.GetFiles("*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
                    {
                        root = dir.FullName;
                        break;
                    }
                    dir = dir.Parent;
                }
                if (string.IsNullOrEmpty(root))
                    root = Directory.GetCurrentDirectory();
            }

            string path = Path.Combine(root, subfolder);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }

        // ── Сравнение float с эпсилоном ───────────────────────────────────────
        public static bool AlmostEqual(float a, float b, float eps = 1e-5f) =>
            Math.Abs(a - b) <= eps;

        // ── Ресемплинг вершин (контуры с NaN-разделителями) ──────────────────
        // Используется при морфинге: выравниваем число точек в двух контурах.
        public static List<Vector2> ResizeVertexList(List<Vector2> source, int targetTotalPoints)
        {
            if (source.Count == 0)
                return [..new Vector2[targetTotalPoints]];
            if (source.Count == targetTotalPoints)
                return source;
            if (targetTotalPoints <= 0)
                return [];

            // Разбиваем на контуры по NaN
            var contours = new List<List<Vector2>>();
            var current  = new List<Vector2>();
            foreach (var v in source)
            {
                if (float.IsNaN(v.X))
                {
                    if (current.Count > 0) { contours.Add(current); current = []; }
                }
                else current.Add(v);
            }
            if (current.Count > 0) contours.Add(current);

            if (contours.Count == 0)
                return [..new Vector2[targetTotalPoints]];

            var result = new List<Vector2>();

            const int minPointsPerContour = 12;
            int pointsPerContour = targetTotalPoints / contours.Count;
            int extraPoints      = targetTotalPoints % contours.Count;

            if (pointsPerContour < minPointsPerContour)
            {
                pointsPerContour = minPointsPerContour;
                extraPoints      = 0;
            }

            for (int i = 0; i < contours.Count; i++)
            {
                var contour      = contours[i];
                int targetPoints = pointsPerContour + (i < extraPoints ? 1 : 0);

                if (contour.Count < 2)
                {
                    for (int j = 0; j < targetPoints; j++)
                        result.Add(contour.Count > 0 ? contour[0] : Vector2.Zero);
                }
                else
                {
                    result.Add(contour[0]);
                    if (targetPoints > 2)
                    {
                        float step = (float)(contour.Count - 1) / (targetPoints - 1);
                        for (int j = 1; j < targetPoints - 1; j++)
                        {
                            float t    = j * step;
                            int   idx  = (int)t;
                            float frac = t - idx;
                            result.Add(idx >= contour.Count - 1
                                ? contour[^1]
                                : Vector2.Lerp(contour[idx], contour[idx + 1], frac));
                        }
                    }
                    result.Add(contour[^1]);
                }

                if (i < contours.Count - 1)
                    result.Add(new Vector2(float.NaN, float.NaN));
            }

            return result;
        }

        // ── Тест каналов DebugManager ─────────────────────────────────────────
        public static void TestDebugManager()
        {
            DebugManager.Info("some text for testing debug output");
            DebugManager.Warn("some text for testing debug output");
            DebugManager.Error("some text for testing debug output");
            DebugManager.Stats("some text for testing debug output");
            DebugManager.Morph("some text for testing debug output");
            DebugManager.Render("some text for testing debug output");
            DebugManager.Memory("some text for testing debug output");
            DebugManager.Scene("some text for testing debug output");
            DebugManager.Font("some text for testing debug output");
        }
    }
}