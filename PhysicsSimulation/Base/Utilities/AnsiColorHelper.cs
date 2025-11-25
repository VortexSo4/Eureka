namespace PhysicsSimulation.Base.Utilities;

public static class AnsiColorHelper
{
    public static int HexToAnsi256(string hex)
    {
        var (r, g, b) = HexToRgb(hex);

        int r6 = (int)Math.Round(r / 255.0 * 5);
        int g6 = (int)Math.Round(g / 255.0 * 5);
        int b6 = (int)Math.Round(b / 255.0 * 5);

        int cubeIndex = 16 + 36 * r6 + 6 * g6 + b6;
        var (cr, cg, cb) = AnsiCubeToRgb(cubeIndex);

        int grayLevel = (int)Math.Round(((r + g + b) / 3.0 - 8) / 10);
        int grayIndex = Math.Clamp(232 + grayLevel, 232, 255);
        var (gr, gg, gb) = GrayToRgb(grayIndex);

        double cubeDist = Dist(r, g, b, cr, cg, cb);
        double grayDist = Dist(r, g, b, gr, gg, gb);

        return cubeDist < grayDist ? cubeIndex : grayIndex;
    }

    public static string HexToAnsiEscape(string hex)
    {
        int id = HexToAnsi256(hex);
        return $"\u001b[38;5;{id}m";
    }

    private static (int r, int g, int b) HexToRgb(string hex)
    {
        hex = hex.Replace("#", "");
        int r = Convert.ToInt32(hex.Substring(0, 2), 16);
        int g = Convert.ToInt32(hex.Substring(2, 2), 16);
        int b = Convert.ToInt32(hex.Substring(4, 2), 16);
        return (r, g, b);
    }

    private static (int r, int g, int b) AnsiCubeToRgb(int index)
    {
        index -= 16;
        int r = index / 36 % 6;
        int g = index / 6 % 6;
        int b = index % 6;

        return (r * 51, g * 51, b * 51);
    }

    private static (int r, int g, int b) GrayToRgb(int index)
    {
        int level = (index - 232) * 10 + 8;
        return (level, level, level);
    }

    private static double Dist(int r1, int g1, int b1, int r2, int g2, int b2)
    {
        return Math.Sqrt(
            Math.Pow(r1 - r2, 2) +
            Math.Pow(g1 - g2, 2) +
            Math.Pow(b1 - b2, 2)
        );
    }
}