using OpenTK.Mathematics;
using PhysicsSimulation.Base;
using PhysicsSimulation.SceneRendering;

namespace PhysicsSimulation.BuiltInScenes
{
    public class SimulationScene1 : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Simulation Scene 1 started");
            AnimateBackgroundColor(new Vector3(0.02f, 0.03f, 0.05f));

            var label = new Text("PHYSICS SIMULATION", y: 0.7f, fontSize: 0.1f, color: new Vector3(1f, 1f, 1f));
            var ball = new Circle(0f, 0.2f, 0.15f, color: new Vector3(0.3f, 0.7f, 1f));
            Add(label.Draw(1.5f), ball.Draw(1.5f));

            Wait(2);
            ball.Resize(0.3f, 2f).SetLineWidth(6);

            Wait(3f);
            label.MorphTo(new Text("END OF SIMULATION", fontSize: 0.1f, color: new Vector3(1f, 1f, 1f)));
        }
    }
}