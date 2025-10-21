using OpenTK.Mathematics;

namespace PhysicsSimulation;

public class CustomScene : Scene
{
    protected override void StartSlides()
    {
        Console.WriteLine("Starting slides for CustomScene");
        var t1 = new Text("Eп=Eк", x: -0.2f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.5f, 0.5f, 1.0f));
        var t11 = new Text("=", x: -0.075f, y: -0.4f, fontSize: 0.1f, color: new Vector3(0.5f, 0.5f, 1.0f));
        var t2 = new Text("mgh", x: -0.2f, y: -0.4f, fontSize: 0.1f, color: new Vector3(0.5f, 0.5f, 1.0f));
        var t3 = new Text("(mV^2)/2", x: 0.2f, y: -0.4f, fontSize: 0.1f, color: new Vector3(0.5f, 0.5f, 1.0f));

        Add(t1.Draw(duration: 1.0f), t3.Draw());
        Wait(4.0f);
        t1.GetSlice(0, 2).AnimateColor(new Vector3(1.0f, 0.3f, 0.3f));
        t1.GetSlice(0, 2).Move(0.1f, 0.2f);
        Wait(1.0f);
        t1.GetSlice(0, 2).Morph(t2);
        Wait(1.0f);
        t1.GetSlice(2, 1).AnimateColor(new Vector3(1.0f, 0.3f, 0.3f));
        Wait(1.0f);
        t1.GetSlice(2, 1).Morph(t11);
        Wait(1.0f);
        t1.GetSlice(3, 2).AnimateColor(new Vector3(1.0f, 0.3f, 0.3f));
        Wait(1.0f);
        t1.GetSlice(3, 2).Morph(t3);
    }
}