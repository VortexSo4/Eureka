using OpenTK.Mathematics;

namespace PhysicsSimulation
{
    public class CustomScene : Objects.Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Starting slides for CustomScene");
            var c1 = new Objects.Circle(x: 0.0f, y: 0.0f, radius: 0.25f, color: new Vector3(1.0f, 0.5f, 0.5f), filled: false);
            var r1 = new Objects.Rectangle(x: 0.1f, y: 0.2f, width: 0.3f, height: 0.2f, filled: true, color: new Vector3(0.5f, 1.0f, 0.5f));
            var t1 = new Objects.Text("BBBBBB", x: -0.2f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.5f, 0.5f, 1.0f));

            Add(c1.Draw(duration: 1.0f));
            Wait(duration: 2.0f);
            c1.AnimateColor(new Vector3(1.0f, 0.0f, 0.0f), duration: 2.0f, ease: "linear");
            c1.SetLineWidth(200.0f, duration: 1.0f, ease: "ease_in_out");
            Wait(duration: 2.0f);
            c1.MorphTo(r1, duration: 2.0f);
            Wait(duration: 3.0f);
            c1.MorphTo(t1, duration: 2.0f);
            Wait(duration: 3.0f);
            c1.MorphTo(new Objects.Circle(x: 0.0f, y: 0.0f, radius: 0.25f, color: new Vector3(1.0f, 0.5f, 0.5f), filled: false), duration: 1.0f);
            c1.Resize(2.0f, 1.0f);
            c1.SetFilled(true, duration: 1.0f);
            Wait(duration: 3.0f);
        }
    }
}