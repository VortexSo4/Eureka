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

            // Helper: load scene by index, resetting arena and engine state cleanly
            SceneGpu LoadScene(int index)
            {
                // Clear registry to avoid duplicate function registrations across scenes
                ESharpEngine.Registry.Clear();

                // Reset arena so new scene starts at offset 0
                arena.Reset();

                // Recreate engine so builtins are re-registered fresh
                esharp = new ESharpEngine(arena);

                var newScene = new SceneGpu(arena);
                esharp.CurrentScene = newScene;
                esharp.LoadSceneFromFile(sceneFiles[index]);
                return esharp.CurrentScene;
            }

            // Загружаем первую сцену
            SceneGpu scene = LoadScene(currentSceneIndex);

            double lastTime = 0.0;

            window.RenderFrame += _ =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;
                ESharpEngine.Registry.RegisterVar("T", currentTime);

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

                    // Dispose old scene GL resources, then load fresh
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