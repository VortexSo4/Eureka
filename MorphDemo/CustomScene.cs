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
            Add(alph1.Draw(), alph2.Draw());

            Add(t1.Draw(1f), t2.Draw(1f), t3.Draw(1f));
            Wait(2f);

            t1.GetSlice(0, t1.TextContent.Length).AnimateColor(new Vector3(1f, 0.8f, 0.2f), 1f).Move(0.3f, -0.1f, 1.5f);
            t2.GetSlice(0, t2.TextContent.Length).AnimateColor(new Vector3(0.2f, 1f, 0.4f), 1f).Move(0.2f, 0.1f, 1.5f);
            t3.GetSlice(0, t3.TextContent.Length).AnimateColor(new Vector3(0.9f, 0.3f, 0.7f), 1f).Move(-0.2f, -0.2f, 1.5f);

            Wait(2f);

            // --- Primitives stress test ---
            int numCircles = 30;
            int numRects = 30;
            var rnd = new Random();
            
            for (int i = 0; i < numCircles; i++)
            {
                var c = new Circle(
                    x: (float)(rnd.NextDouble() * 2 - 1),
                    y: (float)(rnd.NextDouble() * 2 - 1),
                    radius: 0.02f + (float)rnd.NextDouble() * 0.05f,
                    filled: true,
                    color: new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble())
                );
                Add(c.Draw(0.5f));
                // animate position and color
                c.MoveTo((float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 2 - 1), 3f, EaseType.EaseInOut)
                 .AnimateColor(new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble()), 3f);
            }

            for (int i = 0; i < numRects; i++)
            {
                var r = new Rectangle(
                    x: (float)(rnd.NextDouble() * 2 - 1),
                    y: (float)(rnd.NextDouble() * 2 - 1),
                    width: 0.03f + (float)rnd.NextDouble() * 0.07f,
                    height: 0.03f + (float)rnd.NextDouble() * 0.07f,
                    filled: true,
                    color: new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble())
                );
                Add(r.Draw(0.5f));
                r.MoveTo((float)(rnd.NextDouble() * 2 - 1), (float)(rnd.NextDouble() * 2 - 1), 3f, EaseType.EaseInOut)
                 .AnimateColor(new Vector3((float)rnd.NextDouble(), (float)rnd.NextDouble(), (float)rnd.NextDouble()), 3f)
                 .RotateTo((float)(rnd.NextDouble() * MathF.PI * 2), 3f, EaseType.EaseInOut);
            }

            Wait(4f);

            // --- Morph text to new word for stress test ---
            var t4 = new Text("OPENGL", x: -0.6f, y: 0.5f, fontSize: 0.1f, color: new Vector3(0.9f, 0.9f, 0.2f));
            t1.GetSlice(0, t1.TextContent.Length).Morph(t4, 2f, EaseType.EaseInOut);
            Wait(2.5f);

            var t5 = new Text("ENGINE", x: -0.7f, y: 0.3f, fontSize: 0.1f, color: new Vector3(0.2f, 0.9f, 0.9f));
            t2.GetSlice(0, t2.TextContent.Length).Morph(t5, 2f, EaseType.EaseInOut);
            Wait(2.5f);

            var t6 = new Text("BENCH", x: -0.8f, y: 0.1f, fontSize: 0.1f, color: new Vector3(0.9f, 0.3f, 0.3f));
            t3.GetSlice(0, t3.TextContent.Length).Morph(t6, 2f, EaseType.EaseInOut);
            Wait(3f);

            Console.WriteLine("Benchmark Scene setup complete!");
        }
    }
}