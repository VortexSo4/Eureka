using OpenTK.Mathematics;

namespace PhysicsSimulation.Rendering.PrimitiveRendering
{
    public class VectorPrimitive : CompositePrimitive
    {
        public Line Shaft { get; }
        public Triangle ArrowHead { get; }
        public Text? Label { get; private set; }

        public VectorPrimitive(float x = 0f, float y = 0f, float endX = 0.3f, float endY = 0f,
            Vector3? color = null, bool showMagnitude = false, float arrowSize = 0.08f)
            : base(x, y)
        {
            var c = color ?? Vector3.One;
            Shaft = new Line(x1: endX, y1: endY, color: c) { LineWidth = 2f };
            Add(Shaft);
            ArrowHead = new Triangle(
                x: 0, y: 0,
                a: new Vector2(arrowSize, 0f),
                b: new Vector2(0f, arrowSize * 0.6f),
                c: new Vector2(0f, -arrowSize * 0.6f),
                filled: true,
                color: c);
            Add(ArrowHead);

            if (showMagnitude)
            {
                Label = new Text("", endX * 0.5f, endY * 0.5f, 0.08f, color: c);
                Add(Label);
            }

            SetEnd(endX, endY);
        }

        public void SetEnd(float globalEndX, float globalEndY)
        {
            float localEndX = globalEndX - X;
            float localEndY = globalEndY - Y;
            float length = MathF.Sqrt(localEndX * localEndX + localEndY * localEndY);
            float angle = MathF.Atan2(localEndY, localEndX);

            Shaft.X = 0f;
            Shaft.Y = 0f;
            Shaft.SetPoints([Vector2.Zero, new Vector2(localEndX, localEndY)]);

            ArrowHead.Rotation = angle;
            ArrowHead.X = localEndX;
            ArrowHead.Y = localEndY;

            if (Label != null)
            {
                Label.X = localEndX * 0.5f;
                Label.Y = localEndY * 0.5f;
                Label.SetDynamicText(() => $"|v|={length:F2}");
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

        private readonly List<Line> gridLines = [];

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
            var xAxis = new Line(0, 0, Length, 0, ColorAxis) { LineWidth = 2f };
            Add(xAxis);

            // ticks along X
            for (float t = -Length; t <= Length; t += TickSpacing)
            {
                // skip origin tick heavy (we'll keep it)
                var tick = new Line(t, 0, t, 0.04f, ColorAxis) { LineWidth = 1f };
                Add(tick);

                if (ShowGrid)
                {
                    var gl = new Line(t, -Length, t, Length, new Vector3(0.85f, 0.85f, 0.85f))
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
                var yAxis = new Line(0, 0, 0, Length, ColorAxis) { LineWidth = 2f };
                Add(yAxis);

                for (float t = -Length; t <= Length; t += TickSpacing)
                {
                    var tick = new Line(0, t, 0.04f, t, ColorAxis) { LineWidth = 1f };
                    Add(tick);

                    if (ShowGrid)
                    {
                        var gl = new Line(-Length, t, Length, t, new Vector3(0.85f, 0.85f, 0.85f))
                            { LineWidth = 1f };
                        gridLines.Add(gl);
                        Add(gl);
                    }

                    var label = new Text($"{t:F1}", -0.06f, t, 0.06f, color: ColorAxis);
                    Add(label);
                }
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
            var xAxis = new Line(0f, 0f, axisLength, 0f, axisColor) { LineWidth = 2f };
            comp.Add(xAxis);

            // X ticks, labels and optional vertical grid lines
            for (float t = -axisLength; t <= axisLength + 1e-6f; t += tickSpacing)
            {
                // tick: we create a small vertical tick at x = t
                var tick = new Line(t, 0f, t, 0.04f, axisColor) { LineWidth = 1f };
                comp.Add(tick);

                if (showGrid)
                {
                    var gl = new Line(t, -axisLength, t, axisLength, gridColor) { LineWidth = 1f };
                    comp.Add(gl);
                }

                var label = new Text($"{t:F1}", t, -0.06f, 0.06f, color: axisColor);
                comp.Add(label);
            }

            if (axisAmount >= 2)
            {
                // Y axis line
                var yAxis = new Line(0f, 0f, 0f, axisLength, axisColor) { LineWidth = 2f };
                comp.Add(yAxis);

                // Y ticks, labels and optional horizontal grid lines
                for (float t = -axisLength; t <= axisLength + 1e-6f; t += tickSpacing)
                {
                    var tick = new Line(0f, t, 0.04f, t, axisColor) { LineWidth = 1f };
                    comp.Add(tick);

                    if (showGrid)
                    {
                        var gl = new Line(-axisLength, t, axisLength, t, gridColor) { LineWidth = 1f };
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