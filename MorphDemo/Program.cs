// Program.cs
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;

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

            int aspectLoc = GL.GetUniformLocation(program, "aspectRatio");
            if (aspectLoc >= 0)
            {
                GL.UseProgram(program);
                GL.Uniform1(aspectLoc, 9f / 16f);
                GL.UseProgram(0);
            }

            UpdateViewport(window);

            window.Resize += (_) => UpdateViewport(window);

            Scene scene = LoadScene(sceneName);

            var stopwatch = Stopwatch.StartNew();
            double lastTime = stopwatch.Elapsed.TotalSeconds;
            window.RenderFrame += (_) =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;

                scene.Update(dt);
                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                UpdateViewport(window);
                GL.ClearColor(scene.BackgroundColor.X, scene.BackgroundColor.Y, scene.BackgroundColor.Z, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                scene.Render(program, vbo);

                window.SwapBuffers();
            };

            window.Run();
        }

        private static void UpdateViewport(GameWindow window)
        {
            int width = window.Size.X;
            int height = window.Size.Y;
            if (width <= 0 || height <= 0) return;

            float desiredAspect = 16f / 9f;
            float windowAspect = (float)width / height;

            int vpX = 0, vpY = 0, vpWidth = width, vpHeight = height;

            if (windowAspect > desiredAspect)
            {
                // Window is wider: black bars left and right
                vpWidth = (int)(height * desiredAspect);
                vpX = (width - vpWidth) / 2;
            }
            else
            {
                // Window is taller: black bars top and bottom
                vpHeight = (int)(width / desiredAspect);
                vpY = (height - vpHeight) / 2;
            }

            GL.Viewport(vpX, vpY, vpWidth, vpHeight);
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