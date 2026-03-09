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
            DebugManager.Custom($"Starting E# Scene Runner", "E#", "#FFFF00");
            
            bool debugShutdown =  true;

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
                var s = esharp.CurrentScene;
                s.SetViewportSize(window.ClientSize.X, window.ClientSize.Y);
                return s;
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
                float mxRaw =  (ms.X / window.ClientSize.X) * 2f - 1f;          // NDC [-1,1]
                float myRaw = -((ms.Y / window.ClientSize.Y) * 2f - 1f);        // flip Y
                float mx    = mxRaw * aspect;                                    // world coords (matches dynPos)
                ESharpEngine.Registry.RegisterVar("T",      currentTime);
                ESharpEngine.Registry.RegisterVar("DT",     (double)dt);
                ESharpEngine.Registry.RegisterVar("MX",     (double)mx);        // world X (use in dynPos)
                ESharpEngine.Registry.RegisterVar("MY",     (double)myRaw);     // world Y
                ESharpEngine.Registry.RegisterVar("MX_NDC", (double)mxRaw);     // raw NDC X (use in hit-test)
                ESharpEngine.Registry.RegisterVar("MY_NDC", (double)myRaw);     // raw NDC Y
                ESharpEngine.Registry.RegisterVar("CLICK", ms.IsButtonDown(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left) ? 1.0 : 0.0);
                ESharpEngine.Registry.RegisterVar("FRAME", (double)frameCount);

                scene.Update(dt);
                scene.Render();
                window.SwapBuffers();
            };

            window.UpdateFrame += _ =>
            {
                // Закрыть приложение через 60 секунд после запуска
                if (stopwatch.Elapsed.TotalSeconds >= 60 && debugShutdown)
                {
                    DebugManager.Custom("Benchmark finished (60s). Closing...", "SYSTEM", "#FFAA00");
                    window.Close();
                    return;
                }

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
                scene.SetViewportSize(window.ClientSize.X, window.ClientSize.Y);
            };

            GL.Viewport(0, 0, window.ClientSize.X, window.ClientSize.Y);
            scene.SetViewportSize(window.ClientSize.X, window.ClientSize.Y);
            window.Run();
        }
    }
}