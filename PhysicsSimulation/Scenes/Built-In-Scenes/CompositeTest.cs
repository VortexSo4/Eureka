using OpenTK.Mathematics;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering;
using PhysicsSimulation.Rendering.SceneRendering;
using PhysicsSimulation.Rendering.TextRendering;

namespace PhysicsSimulation.Scenes.Built_In_Scenes
{
    public class CompositeTest : Scene
    {
        protected override void StartSlides()
        {
            DebugManager.Info("Composite Test Scene Started");
            AnimateBackgroundColor(new Vector3(0.05f, 0.05f, 0.07f), 1f);

            // --- COMPOSITE #1: простой ---
            var comp1 = new CompositePrimitive(x: -0.6f, y: 0.0f);

            var c1 = new Circle(0, 0, 0.12f, color: new Vector3(0.3f, 0.8f, 1f));
            var r1 = new Rectangle(0.25f, 0, 0.25f, 0.15f, color: new Vector3(1f, 0.4f, 0.4f));
            var txt1 = new Text("C1", 0, -0.2f, fontSize: 0.07f);

            comp1.Add(c1);
            comp1.Add(r1);
            comp1.Add(txt1);

            Add(comp1.Draw());
            Wait(1f);

            comp1.RotateTo(45, 2f);
            comp1.MoveTo(-0.3f, 0.3f, 2f);
            Wait(2.5f);

            r1.MoveTo(0.4f, 0.2f, 2f);
            Wait(1.5f);

            // --- COMPOSITE #2: тест масштабирования ---
            var comp2 = new CompositePrimitive(x: 0.5f, y: -0.3f);

            var baseRect = new Rectangle(0, 0, 0.25f, 0.25f, color: new Vector3(0.6f, 0.9f, 0.3f));
            var innerCircle = new Circle(0.1f, 0.1f, 0.08f, color: new Vector3(0.9f, 0.6f, 0.2f));

            comp2.Add(baseRect);
            comp2.Add(innerCircle);

            Add(comp2.Draw());
            Wait(1f);

            innerCircle.MoveTo(-0.15f, 0.1f, 2f);
            Wait(2f);

            // --- COMPOSITE #3: тест глобального override вращения ---
            var comp3 = new CompositePrimitive(x: 0.0f, y: 0.5f);

            var arrowBody = new Rectangle(0, 0, 0.4f, 0.05f, color: new Vector3(0.3f, 0.7f, 1f));
            var arrowText = new Text("GLOBAL", 0.1f, -0.15f, fontSize: 0.07f);

            comp3.Add(arrowBody);
            comp3.Add(arrowText);

            Add(comp3.Draw());
            Wait(0.8f);

            comp3.RotateTo(90, 2f);
            comp3.SetChildGlobalRotationOverride(arrowText, 0);
            Wait(2.5f);

            // --- БЕНЧМАРК: VectorPrimitive с динамическим текстом ---
            var vec = new VectorPrimitive(-0.5f, 0.0f, 0.4f, 0.3f, color: new Vector3(1f, 0.8f, 0.2f), showMagnitude: true);
            Add(vec.Draw());
            Wait(1.5f);

            vec.SetEnd(0.2f, 0.5f);
            Wait(1.5f);

            // --- БЕНЧМАРК: GraphPrimitive через фабрику ---
            var (graphComposite, plot) = GraphPrimitiveFactory.CreateGraph(
                f: x => 0.3f * MathF.Sin(8f * x),
                xMin: -1f,
                xMax: 1f,
                resolution: 200,
                axisLength: 0.8f,
                axisAmount: 1,
                showGrid: true);

            graphComposite.X = 0.6f;
            graphComposite.Y = -0.4f;
            Add(graphComposite.Draw());
            Wait(1.5f);

            // Сделать график пунктирным
            plot.Dashed = true;
            plot.DashLength = 0.02f;
            plot.GapLength = 0.01f;
            Wait(1.5f);

            // --- БЕНЧМАРК: несколько текстов с динамическим обновлением ---
            var dynamicText = new Text("Counter: 0", 0f, 0f, 0.08f);
            int counter = 0;
            dynamicText.SetDynamicText(() => $"Counter: {counter}");
            Add(dynamicText.Draw());

            for (int i = 1; i <= 5; i++)
            {
                counter = i;
                Wait(0.5f);
            }

            // --- Конец сцены ---
            AnimateBackgroundColor(new Vector3(0, 0, 0), 1.5f);
            Wait(2f);
        }
    }
}