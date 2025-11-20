using OpenTK.Mathematics;
using SkiaSharp;

namespace PhysicsSimulation
{
    public static class CharMap
    {
        public static List<List<Vector2>> GetCharContours(
            char c, 
            float offsetX, 
            float size,
            FontFamily? fontFamily = null,
            string? fontName = null)
        {
            var contours = new List<List<Vector2>>();

            fontFamily ??= FontFamily.Arial;
            var typeface = FontManager.GetTypeface(fontFamily, fontName);
            using var font = new SKFont(typeface, size);

            if (!TryGetGlyph(font, c, out ushort glyph)) return contours;
            using var path = font.GetGlyphPath(glyph);
            if (path == null) return contours;

            List<Vector2>? currentContour = null;
            var iter = path.CreateRawIterator();
            var points = new SKPoint[4];
            SKPathVerb verb;

            while ((verb = iter.Next(points)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        currentContour = StartNewContour(currentContour, contours, size);
                        currentContour.Add(ToVec(points[0], offsetX));
                        break;
                    case SKPathVerb.Line:
                        currentContour?.Add(ToVec(points[1], offsetX));
                        break;
                    case SKPathVerb.Quad:
                    case SKPathVerb.Conic:
                        AddCurve(currentContour, points[..3], offsetX);
                        break;
                    case SKPathVerb.Cubic:
                        AddCurve(currentContour, points[..4], offsetX);
                        break;
                    case SKPathVerb.Close:
                        CloseContour(currentContour, contours, size);
                        currentContour = null;
                        break;
                }
            }

            CloseContour(currentContour, contours, size);
            return contours;
        }

        // Возвращает ширину символа
        public static float GetGlyphAdvance(
            char c,
            float size,
            FontFamily? fontFamily = null,
            string? fontName = null)
        {
            var typeface = FontManager.GetTypeface(fontFamily, fontName);
            using var font = new SKFont(typeface, size);

            if (!TryGetGlyph(font, c, out ushort glyph)) return size * 0.5f;
            float[] widths = new float[1];
            SKRect[] bounds = new SKRect[1];
            font.GetGlyphWidths(new ushort[] { glyph }, widths, bounds, null);
            if (widths[0] > 0) return widths[0];

            var contours = GetCharContours(c, 0f, size);
            if (contours.Count == 0) return size * 0.5f;

            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (var contour in contours)
                foreach (var p in contour)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                }

            return (minX == float.MaxValue || maxX == float.MinValue) ? size * 0.5f : maxX - minX;
        }

        private static bool TryGetGlyph(SKFont font, char c, out ushort glyph)
        {
            glyph = 0;
            ushort[] glyphs = new ushort[1];
            font.GetGlyphs(new[] { c }, glyphs);
            glyph = glyphs[0];
            return glyph != 0;
        }

        private static Vector2 ToVec(SKPoint p, float offsetX) => new(p.X + offsetX, -p.Y);

        private static List<Vector2> StartNewContour(List<Vector2>? existing, List<List<Vector2>> target, float minSize)
        {
            CloseContour(existing, target, minSize);
            return new List<Vector2>();
        }

        private static void CloseContour(List<Vector2>? contour, List<List<Vector2>> target, float minSize)
        {
            if (contour == null || contour.Count == 0) return;

            if (contour.Count < 2)
            {
                float s = minSize * 0.1f;
                contour.Clear();
                contour.AddRange(new[] { new Vector2(-s, -s), new Vector2(s, -s), new Vector2(s, s), new Vector2(-s, s) });
            }
            else if ((contour[^1] - contour[0]).Length > 1e-5f)
            {
                contour.Add(contour[0]);
            }

            target.Add(contour);
        }

        private static void AddCurve(List<Vector2>? contour, SKPoint[] controlPoints, float offsetX, int steps = 12)
        {
            if (contour == null || controlPoints.Length < 2) return;
            for (int i = 1; i <= steps; i++)
            {
                float t = i / (float)steps;
                var pt = DeCasteljau(controlPoints, t);
                contour.Add(new Vector2(pt.X + offsetX, -pt.Y));
            }
        }

        private static SKPoint DeCasteljau(SKPoint[] points, float t)
        {
            var temp = (SKPoint[])points.Clone();
            int n = points.Length - 1;
            for (int r = 1; r <= n; r++)
                for (int i = 0; i <= n - r; i++)
                    temp[i] = new SKPoint((1 - t) * temp[i].X + t * temp[i + 1].X,
                                           (1 - t) * temp[i].Y + t * temp[i + 1].Y);
            return temp[0];
        }
    }
}