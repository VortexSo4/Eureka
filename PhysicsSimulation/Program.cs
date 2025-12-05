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

            // === ЗАПУСК E# СЦЕН ===
            var arena = new GeometryArena();
            var esharp = new ESharpEngine(arena);
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

            // Загружаем первую сцену
            esharp.CurrentScene = new SceneGpu(arena); // базовая сцена
            esharp.LoadSceneFromFile(sceneFiles[currentSceneIndex]);
            var scene = esharp.CurrentScene;

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

                    // Создаем новый объект сцены и загружаем .es файл
                    esharp.CurrentScene = new SceneGpu(arena);
                    esharp.LoadSceneFromFile(sceneFiles[currentSceneIndex]);
                    scene = esharp.CurrentScene;
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
