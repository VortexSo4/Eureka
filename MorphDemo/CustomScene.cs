// CustomScene.cs
using OpenTK.Mathematics;
using System;

namespace PhysicsSimulation
{
    public class CustomScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Starting Benchmark Scene");

            // --- Text morphing test ---
            var t1 = new Text("PHYSICS", x: -0.7f, y: 0.6f, fontSize: 0.1f, color: new Vector3(0.2f, 0.7f, 1f));
            var t2 = new Text("SIMULATION", x: -0.8f, y: 0.4f, fontSize: 0.08f, color: new Vector3(1f, 0.5f, 0.2f));
            var t3 = new Text("BENCHMARK", x: -0.9f, y: 0.2f, fontSize: 0.09f, color: new Vector3(0.8f, 0.2f, 0.9f));
            
            var alph1 = new Text("ABCDEFGHIJKLMNOPQRSTUVWXYZ", x: 0.0f, y: 0.1f, fontSize: 0.09f, color: new Vector3(0.8f, 0.2f, 0.9f));
            var alph2 = new Text("abcdefghijklmnopqrstuvwxyz", x: 0.0f, y: 0.0f, fontSize: 0.09f, color: new Vector3(0.8f, 0.2f, 0.9f));
            var c1 = new Circle(x: -0.2f);
            var r2 = new Rectangle();
            Add(alph1.Draw(), alph2.Draw());

            Add(t1.Draw(1f), t2.Draw(1f), t3.Draw(1f));
            Wait(2f);

            t1.GetSlice(0, t1.TextContent.Length).AnimateColor(new Vector3(1f, 0.8f, 0.2f), 1f).Move(0.3f, -0.1f, 1.5f);
            t2.GetSlice(0, t2.TextContent.Length).AnimateColor(new Vector3(0.2f, 1f, 0.4f), 1f).Move(0.2f, 0.1f, 1.5f);
            t3.GetSlice(0, t3.TextContent.Length).AnimateColor(new Vector3(0.9f, 0.3f, 0.7f), 1f).Move(-0.2f, -0.2f, 1.5f);
            
            Wait();
            AnimateBackgroundColor(new Vector3(0.0f, 0.0f, 0.0f));
            
            AnimateBackgroundColor(new Vector3(0.04f, 0.04f, 0.08f), 3f);

            // --- Morph text to new word for stress test ---
            var t4 = new Text("OPENGL", x: -0.6f, y: 0.5f, fontSize: 0.1f, color: new Vector3(0.9f, 0.9f, 0.2f));
            t1.GetSlice(0, t1.TextContent.Length).Morph(c1, 2f, EaseType.EaseInOut);
            Wait(2.5f);

            var t5 = new Text("ENGINE", x: -0.7f, y: 0.3f, fontSize: 0.1f, color: new Vector3(0.2f, 0.9f, 0.9f));
            t2.GetSlice(0, t2.TextContent.Length).Morph(r2, 2f, EaseType.EaseInOut);
            Wait(2.5f);

            var t6 = new Text("BENCH", x: -0.8f, y: 0.1f, fontSize: 0.1f, color: new Vector3(0.9f, 0.3f, 0.3f));
            t3.GetSlice(0, t3.TextContent.Length).Morph(t6, 2f, EaseType.EaseInOut);
            Wait(3f);

            Console.WriteLine("Benchmark Scene setup complete!");
        }
    }
}