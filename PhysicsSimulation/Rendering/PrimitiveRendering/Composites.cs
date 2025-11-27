using OpenTK.Mathematics;

namespace PhysicsSimulation.Rendering.PrimitiveRendering
{
    public class VectorPrimitive : CompositePrimitive
    {
        public Line Shaft { get; private set; }
        public Triangle ArrowHead { get; private set; }
        public Text? Label { get; private set; }

        public bool ShowMagnitude { get; set; } = false;

        /// <param name="endX">локальный end x (от начала в локальных координатах)</param>
        public VectorPrimitive(float x = 0f, float y = 0f, float endX = 0.3f, float endY = 0f,
            Vector3? color = null, bool dashed = false, bool showMagnitude = false, float arrowSize = 0.04f)
            : base(x, y)
        {
            var c = color ?? Vector3.One;
            Shaft = new Line(0, 0, endX, endY, dashed, c) { LineWidth = 2f };
            Add(Shaft);

            // arrowhead: small triangle positioned at end, pointing along shaft
            ArrowHead = CreateArrowHead(endX, endY, arrowSize, c);
            Add(ArrowHead);

            ShowMagnitude = showMagnitude;
            if (ShowMagnitude)
            {
                float len = new Vector2(endX, endY).Length;
                Label = new Text($"|v|={len:F2}", endX * 0.5f, endY * 0.5f, 0.08f, color: c);
                Add(Label);
            }
        }

        private Triangle CreateArrowHead(float endX, float endY, float size, Vector3 color)
        {
            // base triangle oriented along +X; we'll place as local triangle at (endX,endY) and rotate via Rotation property
            var dir = new Vector2(endX, endY);
            float ang = MathF.Atan2(dir.Y, dir.X);

            // triangle in local coords pointing right
            var a = new Vector2(0f, 0.0f);
            var b = new Vector2(-size, size * 0.6f);
            var c = new Vector2(-size, -size * 0.6f);

            var tri = new Triangle(endX, endY, a, b, c, color) { Filled = true };

            // rotate triangle around its local origin by ang — easiest: set triangle.Rotation (inherited)
            tri.Rotation = ang;
            return tri;
        }

        /// <summary>
        /// Update the vector end position — reconfigure shaft and arrowhead (and label position).
        /// </summary>
        public void SetEnd(float endX, float endY, bool dashed = false)
        {
            Shaft.EndX = endX;
            Shaft.EndY = endY;
            Shaft.Dashed = dashed;

            // reposition arrow
            ArrowHead.X = endX;
            ArrowHead.Y = endY;
            ArrowHead.Rotation = MathF.Atan2(endY, endX);

            if (Label != null)
            {
                var vector = new Vector2(endX, endY);
                Label.SetDynamicText(() => $"|v|={vector.Length:F2}");
            }
        }
    }

    public class AxisPrimitive : CompositePrimitive
    {
        public float Length { get; set; } = 1f;
        public int AxisAmount { get; set; } = 2;
        public float TickSpacing { get; set; } = 0.25f;
        public int TickCount { get; set; } = 8;
        public bool ShowGrid { get; set; } = false;
        public bool Dashed { get; set; } = false;
        public Vector3 ColorAxis { get; set; } = new Vector3(0.2f, 0.2f, 0.2f);

        private readonly List<Line> gridLines = new();

        public AxisPrimitive(float x = 0, float y = 0, float length = 1f, int axisAmount = 2, Vector3? color = null,
            bool showGrid = false, float tickSpacing = 0.25f)
            : base(x, y)
        {
            Length = length;
            AxisAmount = Math.Clamp(axisAmount, 1, 2);
            TickSpacing = tickSpacing;
            ColorAxis = color ?? new Vector3(0.2f, 0.2f, 0.2f);
            ShowGrid = showGrid;

            BuildAxes();
        }

        private void BuildAxes()
        {
            // X axis
            var xAxis = new Line(0, 0, Length, 0, false, ColorAxis) { LineWidth = 2f };
            Add(xAxis);

            // ticks along X
            for (float t = -Length; t <= Length; t += TickSpacing)
            {
                // skip origin tick heavy (we'll keep it)
                var tick = new Line(t, 0, t, 0.04f, false, ColorAxis) { LineWidth = 1f };
                Add(tick);

                if (ShowGrid)
                {
                    var gl = new Line(t, -Length, t, Length, false, new Vector3(0.85f, 0.85f, 0.85f))
                        { LineWidth = 1f };
                    gridLines.Add(gl);
                    Add(gl);
                }

                // labels: small numbers below x-axis — relies on Text constructor
                var label = new Text($"{t:F1}", t, -0.06f, 0.06f, color: ColorAxis);
                Add(label);
            }

            if (AxisAmount >= 2)
            {
                // Y axis
                var yAxis = new Line(0, 0, 0, Length, false, ColorAxis) { LineWidth = 2f };
                Add(yAxis);

                for (float t = -Length; t <= Length; t += TickSpacing)
                {
                    var tick = new Line(0, t, 0.04f, t, false, ColorAxis) { LineWidth = 1f };
                    Add(tick);

                    if (ShowGrid)
                    {
                        var gl = new Line(-Length, t, Length, t, false, new Vector3(0.85f, 0.85f, 0.85f))
                            { LineWidth = 1f };
                        gridLines.Add(gl);
                        Add(gl);
                    }

                    var label = new Text($"{t:F1}", -0.06f, t, 0.06f, color: ColorAxis);
                    Add(label);
                }
            }
        }

        public void SetDashed(bool dashed, float dashLen = 0.05f, float gapLen = 0.03f)
        {
            Dashed = dashed;
            // set dashed on all child Line elements
            foreach (var child in Children.OfType<Line>())
            {
                child.Dashed = dashed;
                child.DashLength = dashLen;
                child.GapLength = gapLen;
            }
        }
    }

    public static class GraphPrimitiveFactory
    {
        public static (CompositePrimitive composite, Plot plot) CreateGraph(
            Func<float, float> f,
            float xMin = -1f,
            float xMax = 1f,
            int resolution = 300,
            float axisLength = 1.0f,
            int axisAmount = 2,
            bool showGrid = true,
            Vector3? color = null,
            float tickSpacing = 0.25f)
        {
            var col = color ?? new Vector3(0f, 0.4f, 0.8f);
            var axisColor = new Vector3(0.2f, 0.2f, 0.2f);
            var gridColor = new Vector3(0.85f, 0.85f, 0.85f);

            var comp = new CompositePrimitive(0f, 0f);

            // X axis line (centered at composite origin)
            var xAxis = new Line(0f, 0f, axisLength, 0f, false, axisColor) { LineWidth = 2f };
            comp.Add(xAxis);

            // X ticks, labels and optional vertical grid lines
            for (float t = -axisLength; t <= axisLength + 1e-6f; t += tickSpacing)
            {
                // tick: we create a small vertical tick at x = t
                var tick = new Line(t, 0f, t, 0.04f, false, axisColor) { LineWidth = 1f };
                comp.Add(tick);

                if (showGrid)
                {
                    var gl = new Line(t, -axisLength, t, axisLength, false, gridColor) { LineWidth = 1f };
                    comp.Add(gl);
                }

                var label = new Text($"{t:F1}", t, -0.06f, 0.06f, color: axisColor);
                comp.Add(label);
            }

            if (axisAmount >= 2)
            {
                // Y axis line
                var yAxis = new Line(0f, 0f, 0f, axisLength, false, axisColor) { LineWidth = 2f };
                comp.Add(yAxis);

                // Y ticks, labels and optional horizontal grid lines
                for (float t = -axisLength; t <= axisLength + 1e-6f; t += tickSpacing)
                {
                    var tick = new Line(0f, t, 0.04f, t, false, axisColor) { LineWidth = 1f };
                    comp.Add(tick);

                    if (showGrid)
                    {
                        var gl = new Line(-axisLength, t, axisLength, t, false, gridColor) { LineWidth = 1f };
                        comp.Add(gl);
                    }

                    var label = new Text($"{t:F1}", -0.06f, t, 0.06f, color: axisColor);
                    comp.Add(label);
                }
            }
            var plot = new Plot(f, xMin, xMax, resolution, col);
            comp.Add(plot);

            return (comp, plot);
        }
    }
}