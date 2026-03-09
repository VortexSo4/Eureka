using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            DebugManager.Custom($"Starting E# Scene Runner", "E#", "#A0FF33");

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

            // === ЗАПУСК E# СЦЕН ===
            GeometryArena arena = new GeometryArena();
            ESharpEngine esharp = new ESharpEngine(arena);
            var stopwatch = Stopwatch.StartNew();

            // Сканируем все .es сцены в папке
            var scenesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scenes\\Built-In-Scenes");
            var sceneFiles = Directory.GetFiles(scenesDir, "*.es").ToList();
            if (sceneFiles.Count == 0)
            {
                DebugManager.Error($"Нет сцен .es в папке {scenesDir}");
                return;
            }

            int currentSceneIndex = 0;

            // Helper: полностью чистое переключение сцены
            SceneGpu LoadScene(int index)
            {
                ESharpEngine.Registry.Clear();
                arena.Reset();
                esharp = new ESharpEngine(arena);
                esharp.CurrentScene = new SceneGpu(arena);
                esharp.LoadSceneFromFile(sceneFiles[index]);
                return esharp.CurrentScene;
            }

            // Загружаем первую сцену
            SceneGpu scene = LoadScene(currentSceneIndex);

            double lastTime = 0.0;

            long frameCount = 0;

            window.RenderFrame += _ =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;
                frameCount++;

                // Update live globals accessible from any DSL expression
                var ms = window.MouseState;
                float aspect = (float)window.ClientSize.X / window.ClientSize.Y;
                float mx =  ((ms.X / window.ClientSize.X) * 2f - 1f) * aspect; // account for aspect ratio
                float my = -((ms.Y / window.ClientSize.Y) * 2f - 1f);           // flip Y: OpenGL Y-up
                ESharpEngine.Registry.RegisterVar("T",     currentTime);
                ESharpEngine.Registry.RegisterVar("DT",    (double)dt);
                ESharpEngine.Registry.RegisterVar("MX",    (double)mx);
                ESharpEngine.Registry.RegisterVar("MY",    (double)my);
                ESharpEngine.Registry.RegisterVar("CLICK", ms.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left) ? 1.0 : 0.0);
                ESharpEngine.Registry.RegisterVar("FRAME", (double)frameCount);

                scene.Update(dt);
                scene.Render();
                window.SwapBuffers();
            };

            window.UpdateFrame += _ =>
            {
                if (window.KeyboardState.WasKeyDown(Keys.Escape))
                    window.Close();

                // Переключение сцены по пробелу
                if (window.KeyboardState.IsKeyPressed(Keys.Space))
                {
                    currentSceneIndex = (currentSceneIndex + 1) % sceneFiles.Count;
                    scene.Dispose();
                    scene = LoadScene(currentSceneIndex);
                }
            };

            window.Resize += _ =>
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