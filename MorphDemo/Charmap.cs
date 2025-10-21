// CharmapFixed.cs
using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;
using SkiaSharp;

namespace PhysicsSimulation
{
    public static class CharMap
    {
        public static List<List<Vector2>> GetCharContours(char c, float offsetX, float size)
        {
            var contours = new List<List<Vector2>>();

            using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
            using var font = new SKFont(typeface, size);

            ushort[] glyphs = new ushort[1];
            font.GetGlyphs(new[] { c }, glyphs);
            if (glyphs[0] == 0) return contours;

            var path = font.GetGlyphPath(glyphs[0]);
            if (path == null) return contours;

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
                        // если был предыдущий контур — замкнуть и добавить
                        if (currentContour != null && currentContour.Count > 0)
                            CloseContour(currentContour, size);
                        currentContour = new List<Vector2>();
                        lastPoint = ToVec(points[0], offsetX);
                        currentContour.Add(lastPoint);
                        break;

                    case SKPathVerb.Line:
                        lastPoint = ToVec(points[1], offsetX);
                        currentContour?.Add(lastPoint);
                        break;

                    case SKPathVerb.Quad:
                        AddQuadratic(currentContour, points[0], points[1], points[2], offsetX);
                        lastPoint = ToVec(points[2], offsetX);
                        break;

                    case SKPathVerb.Conic:
                        AddQuadratic(currentContour, points[0], points[1], points[2], offsetX);
                        lastPoint = ToVec(points[2], offsetX);
                        break;

                    case SKPathVerb.Cubic:
                        AddCubic(currentContour, points[0], points[1], points[2], points[3], offsetX);
                        lastPoint = ToVec(points[3], offsetX);
                        break;

                    case SKPathVerb.Close:
                        if (currentContour != null && currentContour.Count > 0)
                            CloseContour(currentContour, size);
                        currentContour = null;
                        break;
                }
            }

            // добавить последний контур если остался
            if (currentContour != null && currentContour.Count > 0)
                CloseContour(currentContour, size);

            return contours;

            // --- локальные функции ---
            Vector2 ToVec(SKPoint p, float ox) => new(p.X + ox, -p.Y);

            void CloseContour(List<Vector2> contour, float minSize)
            {
                if (contour.Count < 2)
                {
                    // очень маленький контур, превратим в квадрат 2x2 px
                    contour.Clear();
                    float s = minSize * 0.1f;
                    contour.Add(new Vector2(-s, -s));
                    contour.Add(new Vector2(s, -s));
                    contour.Add(new Vector2(s, s));
                    contour.Add(new Vector2(-s, s));
                }
                else
                {
                    // замкнуть контур
                    if ((contour[^1] - contour[0]).Length > 1e-5f)
                        contour.Add(contour[0]);
                }
                contours.Add(contour);
            }

            void AddQuadratic(List<Vector2>? contour, SKPoint p0, SKPoint p1, SKPoint p2, float ox, int steps = 8)
            {
                if (contour == null) return;
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float u = 1 - t;
                    contour.Add(new Vector2(
                        u * u * p0.X + 2 * u * t * p1.X + t * t * p2.X + ox,
                        -(u * u * p0.Y + 2 * u * t * p1.Y + t * t * p2.Y)
                    ));
                }
            }

            void AddCubic(List<Vector2>? contour, SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float ox, int steps = 12)
            {
                if (contour == null) return;
                for (int i = 1; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    float u = 1 - t;
                    contour.Add(new Vector2(
                        u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X + ox,
                        -(u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y)
                    ));
                }
            }
        }
    }
}
