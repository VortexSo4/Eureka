// Program.cs
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;

namespace PhysicsSimulation
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine($"Current Directory: {Environment.CurrentDirectory}");
            Console.WriteLine($"Runtime: {Environment.Version}");

            string sceneName = args.Length > 0 ? args[0] : "CustomScene";

            var window = Helpers.InitOpenTKWindow();
            var (program, vbo) = Helpers.CreateGLContextAndProgram();

            Scene scene = LoadScene(sceneName);

            var stopwatch = Stopwatch.StartNew();
            double lastTime = stopwatch.Elapsed.TotalSeconds;
            window.RenderFrame += (_) =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;

                GL.ClearColor(0.12f, 0.12f, 0.12f, 1.0f);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                scene.Update(dt);
                scene.Render(program, vbo);

                window.SwapBuffers();
            };

            window.Run();
        }

        private static Scene LoadScene(string sceneName)
        {
            try
            {
                Type? sceneType = Type.GetType($"PhysicsSimulation.{sceneName}");
                if (sceneType == null)
                    throw new Exception($"Scene type not found: {sceneName}");
                Console.WriteLine($"Loaded scene: {sceneName}");
                return (Scene)Activator.CreateInstance(sceneType)!;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load {sceneName}: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                return new Scene();
            }
        }
    }
}