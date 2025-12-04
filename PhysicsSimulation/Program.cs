using System.Diagnostics;
using System.IO;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.GraphicsLibraryFramework;
using PhysicsSimulation.Base;
using PhysicsSimulation.Base.Utilities;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;

namespace PhysicsSimulation
{
    internal abstract class Program
    {
        private static void Main(string[] args)
        {
            DebugManager.Custom($"Current Directory: {Environment.CurrentDirectory}", "SYSTEM", "#A0FF33");
            DebugManager.Custom($"Current Version: {Environment.Version}", "SYSTEM", "A0FF33");
            DebugManager.Custom($"Starting E# Scene Runner", "E#", "#00FFFF");

            var window = Helpers.InitOpenTkWindow();
            var (program, vbo) = Helpers.CreateGlContextAndProgram();

            int vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            GL.EnableVertexAttribArray(0);

            int aspectLoc = GL.GetUniformLocation(program, "u_aspectRatio");
            if (aspectLoc >= 0)
            {
                GL.UseProgram(program);
                GL.Uniform1(aspectLoc, (float)window.Size.Y / window.Size.X);
            }

            // === ЗАПУСК E# СЦЕНЫ ===
            var arena = new GeometryArena();
            var esharp = new ESharpEngine(arena);
            var stopwatch = Stopwatch.StartNew();

            // Загружаем и выполняем E# файл
            string scriptPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scenes\\Built-In-Scenes", "CustomSceneGpuExample.es");
            if (!File.Exists(scriptPath))
            {
                DebugManager.Error($"E# scene not found: {scriptPath}");
                return;
            }

            esharp.LoadSceneFromFile(scriptPath);
            var scene = esharp.CurrentScene;

            double lastTime = 0.0;

            window.RenderFrame += _ =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;

                scene.Update(dt);
                scene.Render();
                window.SwapBuffers();
            };

            window.UpdateFrame += args =>
            {
                if (window.KeyboardState.WasKeyDown(Keys.Escape))
                    window.Close();
            };

            window.Resize += e =>
            {
                GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);
                if (aspectLoc >= 0)
                {
                    GL.UseProgram(program);
                    GL.Uniform1(aspectLoc, (float)window.Size.Y / window.Size.X);
                }
            };

            GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);
            window.Run();
        }
    }
}