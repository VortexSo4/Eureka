using OpenTK.Mathematics;

namespace PhysicsSimulation.BuiltInScenes
{
    public class EditorScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Editor Scene initialized");
            AnimateBackgroundColor(new Vector3(0.07f, 0.1f, 0.12f));

            var title = new Text("EDITOR MODE", y: 0.6f, fontSize: 0.1f, color: new Vector3(0.9f, 0.9f, 0.9f));
            Add(title.Draw());

            var grid = new Rectangle(0f, 0f, 1.5f, 1.0f, color: new Vector3(0.2f, 0.5f, 1f));
            Add(grid.Draw());
        }
    }
}