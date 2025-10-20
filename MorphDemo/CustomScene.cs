using OpenTK.Mathematics;

namespace PhysicsSimulation
{
    public class CustomScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Starting slides for CustomScene");
            var c1 = new Circle(x: 0.0f, y: 0.0f, radius: 0.25f, color: new Vector3(1.0f, 0.5f, 0.5f));
            var r1 = new Rectangle(x: 0.1f, y: 0.2f, width: 0.3f, height: 0.2f, filled: true, color: new Vector3(0.5f, 1.0f, 0.5f));
            var t1 = new Text("BBBBBBBBBBBBBBBBBBBBBBBBBBBBB", x: -0.2f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.5f, 0.5f, 1.0f));

            Add(c1.Draw(duration: 1.0f));
            Wait(2.0f);
            c1
                .AnimateColor(new Vector3(1.0f, 0.0f, 0.0f), duration: 2.0f, ease: "linear")
                .SetLineWidth(10.0f, duration: 1.0f, ease: "ease_in_out");
            Wait(2.0f);
            c1.MorphTo(r1, duration: 2.0f);
            Wait(3.0f);
            c1.MorphTo(t1, duration: 2.0f);
            Wait(3.0f);
            c1
                .MorphTo(new Circle(x: 0.0f, y: 0.0f, radius: 0.25f, color: new Vector3(1.0f, 0.5f, 0.5f)), duration: 1.0f)
                .Resize(2.0f);
            Wait(3.0f);
            c1.SetFilled(true, duration: 1.0f);
        }
    }
}