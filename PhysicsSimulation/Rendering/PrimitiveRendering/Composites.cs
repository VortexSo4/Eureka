using OpenTK.Mathematics;

namespace PhysicsSimulation.Rendering.PrimitiveRendering
{
    public class Vector : Composite
    {
        public Line Shaft { get; }
        public Triangle ArrowHead { get; }
        public Text? Label { get; private set; }

        public Vector(float x = 0f, float y = 0f, float endX = 0.3f, float endY = 0f,
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

    public class Graph : Composite
    {
        public float XMin { get; }
        public float XMax { get; }
        public int Resolution { get; }
        public float AxisLength { get; }
        public int AxisAmount { get; }
        public bool ShowGrid { get; }
        public float TickSpacing { get; }

        public Vector3 GraphColor { get; }
        public Vector3 AxisColor { get; }
        public Vector3 GridColor { get; }

        public Plot Plot { get; }

        public Graph(
            Func<float, float> f,
            float x = 0f,
            float y = 0f,
            float xMin = -1f,
            float xMax = 1f,
            int resolution = 300,
            float axisLength = 1.0f,
            int axisAmount = 2,
            bool showGrid = true,
            Vector3? color = null,
            float tickSpacing = 0.25f)
            : base(x, y)
        {
            XMin = xMin;
            XMax = xMax;
            Resolution = resolution;
            AxisLength = axisLength;
            AxisAmount = axisAmount;
            ShowGrid = showGrid;
            TickSpacing = tickSpacing;

            GraphColor = color ?? new Vector3(0f, 0.4f, 0.8f);
            AxisColor = new Vector3(0.2f, 0.2f, 0.2f);
            GridColor = new Vector3(0.2f, 0.2f, 0.2f);

            BuildAxes();
            Add(new Plot(f, XMin, XMax, Resolution, GraphColor));
        }

        private void BuildAxes()
        {
            Add(new Text("0", -0.06f, -0.06f, 0.06f, color: AxisColor));
            Add(new Vector(-AxisLength, 0f, AxisLength, 0f, AxisColor, arrowSize: 0.02f) { LineWidth = 2f });
            for (float t = TickSpacing; t <= AxisLength; t += TickSpacing)
            {
                AddTick(t, 0f, vertical: false);
                AddTick(-t, 0f, vertical: false);
            }
            if (AxisAmount < 2) return;
            Add(new Vector(0f, -AxisLength, 0f, AxisLength, AxisColor, arrowSize: 0.02f) { LineWidth = 2f });
            for (float t = TickSpacing; t <= AxisLength; t += TickSpacing)
            {
                AddTick(0f, t, vertical: true);
                AddTick(0f, -t, vertical: true);
            }
        }
        private void AddTick(float x, float y, bool vertical)
        {
            Add(vertical 
                ? new Line(0f, y, 0.04f, y, AxisColor) 
                : new Line(x, 0f, x, 0.04f, AxisColor));
            if (ShowGrid)
            {
                Add(vertical
                    ? new Line(-AxisLength, y, AxisLength, y, GridColor)
                    : new Line(x, -AxisLength, x, AxisLength, GridColor));
            }
            Add(vertical
                ? new Text($"{y:F1}", -0.06f, y, 0.06f, color: AxisColor)
                : new Text($"{x:F1}", x, -0.06f, 0.06f, color: AxisColor));
        }

    }
}