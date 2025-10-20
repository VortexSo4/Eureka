using System;
using System.Diagnostics;
using OpenTK.Windowing.Desktop;
using OpenTK.Graphics.OpenGL4;

namespace PhysicsSimulation
{
    class Program
    {
        static void Main(string[] args)
        {
            // Print environment info for debugging
            Console.WriteLine($"Current Directory: {Environment.CurrentDirectory}");
            Console.WriteLine($"Runtime: {Environment.Version}");

            // Determine scene name from args or default
            string sceneName = args.Length > 0 ? args[0] : "CustomScene";

            // Initialize OpenTK window
            var window = Helpers.InitOpenTKWindow();
            var ctx = Helpers.CreateGLContextAndProgram();

            // Load scene using reflection
            Objects.Scene scene;
            try
            {
                Type? sceneType = Type.GetType("PhysicsSimulation." + sceneName);
                if (sceneType == null)
                {
                    throw new Exception("Scene type not found: " + sceneName);
                }
                scene = (Objects.Scene)Activator.CreateInstance(sceneType);
                Console.WriteLine("Loaded scene: " + sceneName);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load {sceneName}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                scene = new Objects.Scene();
            }

            // Main loop
            var stopwatch = Stopwatch.StartNew();
            double lastTime = stopwatch.Elapsed.TotalSeconds;
            window.RenderFrame += (args) =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;

                GL.ClearColor(0.12f, 0.12f, 0.12f, 1.0f);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                scene.Update(dt);
                scene.Render(ctx.Item1, ctx.Item2);

                window.SwapBuffers();
            };

            window.Run();
        }
    }
}