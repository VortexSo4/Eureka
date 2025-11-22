using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using OpenTK.Mathematics;
using SkiaSharp;
using System.Threading;

namespace PhysicsSimulation
{
    public static class CharMap
    {
        // GlyphData как раньше
        private sealed class GlyphData
        {
            public IReadOnlyList<List<Vector2>> Contours { get; }
            public float Advance { get; }

            public GlyphData(List<List<Vector2>> contours, float advance)
            {
                Contours = contours;
                Advance = advance;
            }
        }

        private static readonly ConcurrentDictionary<string, GlyphData> GlyphCache = new();

        private static string MakeKey(char c, float size, SKTypeface? face) =>
            $"{c}|{face?.FamilyName ?? "Default"}|{size:F4}";

        // ThreadLocal caches для SKFont / SKPaint, ключ — typeface.FamilyName + size
        private static readonly ThreadLocal<Dictionary<string, SKFont>> _threadFonts =
            new(() => new Dictionary<string, SKFont>(StringComparer.OrdinalIgnoreCase));

        private static readonly ThreadLocal<Dictionary<string, SKPaint>> _threadPaints =
            new(() => new Dictionary<string, SKPaint>(StringComparer.OrdinalIgnoreCase));

        private static SKFont GetThreadFont(SKTypeface face, float size)
        {
            string k = $"{face.FamilyName ?? "default"}|{size:F4}";
            var d = _threadFonts.Value!;
            if (!d.TryGetValue(k, out var font))
            {
                font = new SKFont(face, size);
                d[k] = font;
            }
            return d[k];
        }

        private static SKPaint GetThreadPaint(SKTypeface face, float size)
        {
            string k = $"{face.FamilyName ?? "default"}|{size:F4}";
            var d = _threadPaints.Value!;
            if (!d.TryGetValue(k, out var paint))
            {
                paint = new SKPaint { Typeface = face, TextSize = size, IsAntialias = true };
                d[k] = paint;
            }
            return d[k];
        }

        private static GlyphData GetGlyphData(char c, float size, SKTypeface? typeface)
        {
            string key = MakeKey(c, size, typeface);
            return GlyphCache.GetOrAdd(key, _ => ComputeGlyphData(c, size, typeface));
        }

        private static GlyphData ComputeGlyphData(char c, float size, SKTypeface? typeface)
        {
            var tf = typeface ?? SKTypeface.Default;

            // берём объекты из ThreadLocal - это безопасно для параллельного выполнения
            var font = GetThreadFont(tf, size);
            var paint = GetThreadPaint(tf, size);

            // измерение advance через paint (кэшируется на уровне потоков)
            float advance = paint.MeasureText(c.ToString());

            // Получаем глиф и путь
            ushort glyphId = font.GetGlyph(c);
            if (glyphId != 0)
            {
                using var path = font.GetGlyphPath(glyphId);
                if (path != null && !path.IsEmpty)
                {
                    var contours = ExtractContours(path);
                    if (contours.Count != 0)
                        return new GlyphData(contours, advance);
                }
            }
            
            if (char.IsWhiteSpace(c))
            {
                return new GlyphData(new List<List<Vector2>>(), advance);
            }

            float s = size * 0.1f;
            var fallbackContours = new List<List<Vector2>>
            {
                new()
                {
                    new(-s, -s), new(s, -s), new(s, s), new(-s, s), new(-s, -s)
                }
            };

            return new GlyphData(fallbackContours, size * 0.5f);
        }

        // Парсинг SKPath в контуры (как раньше)
        private static List<List<Vector2>> ExtractContours(SKPath path)
        {
            var contours = new List<List<Vector2>>();
            List<Vector2>? contour = null;

            var iter = path.CreateRawIterator();
            var pts = new SKPoint[4];
            SKPathVerb verb;

            while ((verb = iter.Next(pts)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        CloseAndAdd(contour, contours);
                        contour = new List<Vector2> { ToVec(pts[0]) };
                        break;
                    case SKPathVerb.Line:
                        contour?.Add(ToVec(pts[1]));
                        break;
                    case SKPathVerb.Quad:
                    case SKPathVerb.Conic:
                        AddBezierCurve(contour, pts.AsSpan(0, 3));
                        break;
                    case SKPathVerb.Cubic:
                        AddBezierCurve(contour, pts);
                        break;
                    case SKPathVerb.Close:
                        CloseAndAdd(contour, contours);
                        contour = null;
                        break;
                }
            }

            CloseAndAdd(contour, contours);
            return contours;
        }

        private static Vector2 ToVec(SKPoint p) => new(p.X, -p.Y);

        private static void CloseAndAdd(List<Vector2>? contour, List<List<Vector2>> target)
        {
            if (contour == null || contour.Count == 0) return;
            if (contour[0] != contour[^1]) contour.Add(contour[0]);
            target.Add(contour);
        }

        private static void AddBezierCurve(List<Vector2>? contour, ReadOnlySpan<SKPoint> points, int steps = 8)
        {
            if (contour == null || points.Length < 2) return;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                SKPoint p = DeCasteljau(points, t);
                contour.Add(ToVec(p));
            }
        }

        private static SKPoint DeCasteljau(ReadOnlySpan<SKPoint> points, float t)
        {
            var tmp = points.ToArray();
            int n = tmp.Length - 1;
            for (int r = 1; r <= n; r++)
                for (int i = 0; i <= n - r; i++)
                    tmp[i] = new SKPoint(tmp[i].X + (tmp[i + 1].X - tmp[i].X) * t,
                                         tmp[i].Y + (tmp[i + 1].Y - tmp[i].Y) * t);
            return tmp[0];
        }

        // Public API (как в рефакторинге)
        public static List<List<Vector2>> GetCharContours(
            char c, float offsetX, float cursorY, float size, SKTypeface? typeface)
        {
            var data = GetGlyphData(c, size, typeface);

            var shifted = new List<List<Vector2>>(data.Contours.Count);
            foreach (var c0 in data.Contours)
            {
                var cn = new List<Vector2>(c0.Count + 1);
                foreach (var p in c0)
                    cn.Add(new Vector2(p.X + offsetX, p.Y + cursorY));
                if (cn.Count > 0 && cn[0] != cn[^1]) cn.Add(cn[0]);
                shifted.Add(cn);
            }

            return shifted;
        }

        public static float GetGlyphAdvance(char c, float size, SKTypeface? typeface) =>
            GetGlyphData(c, size, typeface).Advance;
    }
}