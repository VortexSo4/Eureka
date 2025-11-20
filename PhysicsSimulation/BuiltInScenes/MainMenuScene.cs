using System.Data.SqlTypes;
using OpenTK.Mathematics;

namespace PhysicsSimulation.BuiltInScenes
{
    public class MainMenuScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Main Menu loaded");
            AnimateBackgroundColor(new Vector3(0.05f, 0.05f, 0.08f));

            var title = new Text(
                """
                PHYSICS
                SIMULATION
                """,
                y: -0.85f, x: 1.7f,
                fontSize: 0.2f,
                color: new Vector3(1f, 1f, 1f), 
                horizontal: Text.HorizontalAlignment.Right, vertical: Text.VerticalAlignment.Middle,
                filled: false,
                font: FontFamily.Audiowide
            );
            Add(title.Draw(1.5f));
            var hint = new Text("Press [SPACE] to continue", y: -0.95f, fontSize: 0.05f, color: new Vector3(0.8f, 0.8f, 0.8f));
            Add(hint.Draw(1.5f));
        }
    }
}