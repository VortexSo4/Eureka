using System.Diagnostics;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
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
            DebugManager.Log(LogLevel.Custom, $"Current Directory: {Environment.CurrentDirectory}", "SYSTEM", "A0FF33");
            DebugManager.Log(LogLevel.Custom, $"Current Version: {Environment.Version}", "SYSTEM", "A0FF33");

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
                GL.UseProgram(0);
            }

            // Создаём сцену ДО запуска окна
            var arena = new GeometryArena();
            var scene = new CustomSceneGpuExample(arena);

            scene.Setup();        // ← создаём примитивы
            scene.Initialize();   // ← ЭТО САМОЕ ГЛАВНОЕ! Создаётся AnimationEngine!

            var stopwatch = Stopwatch.StartNew();
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
            };

            // Устанавливаем viewport один раз
            GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);

            // Запускаем окно
            window.Run();
        }
    }
}