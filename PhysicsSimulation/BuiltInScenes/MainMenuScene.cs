using System.Data.SqlTypes;
using OpenTK.Mathematics;

namespace PhysicsSimulation.BuiltInScenes
{
    public class MainMenuScene : Scene
    {
        protected override void StartSlides()
        {
            DebugManager.Info("Main Menu loaded");
            AnimateBackgroundColor(new Vector3(0.05f, 0.05f, 0.08f));

            var title = new Text(
                "Eureka",
                y: -0.85f, x: 1.7f,
                fontSize: 0.2f,
                color: new Vector3(1f, 1f, 1f), 
                horizontal: Text.HorizontalAlignment.Right, vertical: Text.VerticalAlignment.Middle,
                filled: false
            );

            int N = 50;
            int M = 100;

            string longText = string.Join("\n",
                Enumerable.Range(0, N)
                    .Select(_ => new string('o', M))
            );
            
            var text = new Text(
                longText,
                y: 0f, x: 0f,
                fontSize: 0.01f,
                color: new Vector3(1f, 1f, 1f), 
                horizontal: Text.HorizontalAlignment.Center, vertical: Text.VerticalAlignment.Middle,
                filled: false,
                font: FontFamily.Audiowide
            );

            var circle = new Circle(x: -.5f);
            var square = new Rectangle(x: .5f);
            
            Add(title.Draw(), circle.Draw(), square.Draw());
            var hint = new Text("Press [SPACE] to continue", y: -0.95f, fontSize: 0.05f, color: new Vector3(0.8f, 0.8f, 0.8f));
            Add(hint.Draw(1.5f));
            Wait(4);
            circle.MorphTo(new Rectangle(x: -.5f));
            square.MorphTo(new Circle(x: .5f));
        }
    }
}