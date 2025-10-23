using OpenTK.Mathematics;

namespace PhysicsSimulation.BuiltInScenes
{
    public class MainMenuScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Main Menu loaded");
            AnimateBackgroundColor(new Vector3(0.05f, 0.05f, 0.08f));

            var text = new Text("MAIN MENU", y: 0.3f, fontSize: 0.12f, color: new Vector3(1f, 1f, 1f));
            Add(text.Draw(1.5f));

            var hint = new Text("Press SPACE to continue", y: -0.5f, fontSize: 0.05f, color: new Vector3(0.8f, 0.8f, 0.8f));
            Add(hint.Draw(1.5f));
        }
    }
}