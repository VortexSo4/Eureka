using OpenTK.Mathematics;
using SkiaSharp;

namespace PhysicsSimulation
{
    public static class CharMap
    {
        public static List<List<Vector2>> GetCharVerts(char c, float offsetX, float size)
        {
            var contours = new List<List<Vector2>>();

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
            using var font = new SKFont(typeface, size);

            ushort[] glyphs = new ushort[1];
            font.GetGlyphs(new[] { c }, glyphs);

            if (glyphs[0] == 0)
                return contours;

            var path = font.GetGlyphPath(glyphs[0]);
            if (path == null)
                return contours;

            var iter = path.CreateRawIterator();
            SKPoint[] points = new SKPoint[4];
            SKPathVerb verb;

            List<Vector2>? currentContour = null;
            Vector2 lastPoint = Vector2.Zero;

            while ((verb = iter.Next(points)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        if (currentContour != null && currentContour.Count > 0)
                            contours.Add(currentContour);
                        currentContour = new List<Vector2>();
                        lastPoint = new Vector2(points[0].X + offsetX, -points[0].Y);
                        currentContour.Add(lastPoint);
                        break;

                    case SKPathVerb.Line:
                        lastPoint = new Vector2(points[1].X + offsetX, -points[1].Y);
                        currentContour?.Add(lastPoint);
                        break;

                    case SKPathVerb.Quad:
                        var quadPoints = new[]
                        {
                            lastPoint,
                            new(points[1].X + offsetX, -points[1].Y),
                            new(points[2].X + offsetX, -points[2].Y)
                        };
                        currentContour?.Add(quadPoints[2]);
                        lastPoint = quadPoints[2];
                        break;

                    case SKPathVerb.Conic:
                        var conicPoints = new[]
                        {
                            lastPoint,
                            new(points[1].X + offsetX, -points[1].Y),
                            new(points[2].X + offsetX, -points[2].Y)
                        };
                        currentContour?.Add(conicPoints[2]);
                        lastPoint = conicPoints[2];
                        break;

                    case SKPathVerb.Cubic:
                        var cubicPoints = new[]
                        {
                            lastPoint,
                            new(points[1].X + offsetX, -points[1].Y),
                            new(points[2].X + offsetX, -points[2].Y),
                            new(points[3].X + offsetX, -points[3].Y)
                        };
                        currentContour?.Add(cubicPoints[3]);
                        lastPoint = cubicPoints[3];
                        break;

                    case SKPathVerb.Close:
                        if (currentContour != null && currentContour.Count > 2)
                            contours.Add(currentContour);
                        currentContour = null;
                        break;
                }
            }

            if (currentContour != null && currentContour.Count > 2)
                contours.Add(currentContour);

            return contours;
        }
    }
}