using SkiaSharp;
using OpenTK.Mathematics;
using System;
using System.Collections.Generic;

public static class CharMap
{
    public static List<Vector2> GetCharVerts(char c, float offsetX, float size)
    {
        var verts = new List<Vector2>();

        // Создаём шрифт
        using var typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal);
        using var font = new SKFont(typeface, size);

        // Получаем глиф
        ushort[] glyphs = new ushort[1];
        font.GetGlyphs(new char[] { c }, glyphs); // заполняем массив

        if (glyphs[0] == 0)
        {
            Console.WriteLine($"Символ '{c}' не найден.");
            return verts;
        }

        var path = font.GetGlyphPath(glyphs[0]);
        if (path == null)
            return verts;

        // Преобразуем SKPath в точки
        var iter = path.CreateRawIterator();
        SKPoint[] points = new SKPoint[4];
        SKPathVerb verb;
        while ((verb = iter.Next(points)) != SKPathVerb.Done)
        {
            for (int i = 0; i < 4; i++)
            {
                if (points[i] != SKPoint.Empty)
                    verts.Add(new Vector2(points[i].X + offsetX, -points[i].Y));
            }
        }

        return verts;
    }
}