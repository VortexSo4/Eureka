// ============================================================
//  Program.cs  (Vulkan версия)
// ============================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using PhysicsSimulation.Rendering.Vulkan;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace PhysicsSimulation
{
    internal static class Program
    {
        private static string[]? _sceneFiles;
        public const bool V_SYNC = true;

        private static void Main(string[] args)
        {
            DebugManager.Custom($"Current Directory: {Environment.CurrentDirectory}", "SYSTEM", "#A0FF33");
            DebugManager.Custom($"Current Version: {Environment.Version}", "SYSTEM", "#A0FF33");
            DebugManager.Custom($"Starting E# Scene Runner [Vulkan]", "E#", "#FFFF00");

            var options = WindowOptions.DefaultVulkan with
            {
                Title = "EurekaSharp [Vulkan]",
                Size = new Vector2D<int>(1920, 1080),
                VSync = V_SYNC,
                ShouldSwapAutomatically = false,
            };

            using var window = Window.Create(options);

            VulkanContext? ctx = null;
            VulkanSceneGpu? scene = null;
            IInputContext? input = null;
            IMouse? mouse = null;

            GeometryArena arena = new GeometryArena();
            var stopwatch = Stopwatch.StartNew();

            var scenesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scenes", "Built-In-Scenes");
            int sceneIndex = 0;
            float sceneTime = 0f;
            long frameCount = 0;
            double lastTime = 0;

            VulkanSceneGpu LoadScene(int index, string[] files)
            {
                ESharpEngine.Registry.Clear();
                arena.Reset();

                var esharp = new ESharpEngine(arena);
                var s = new VulkanSceneGpu(ctx!, arena);

                esharp.SetScene(s);
                esharp.LoadSceneFromFile(files[index]);

                s.SetViewportSize(window.FramebufferSize.X, window.FramebufferSize.Y);
                return s;
            }

            window.Load += () =>
            {
                ctx = new VulkanContext(window, enableValidation: true);
                input = window.CreateInput();
                mouse = input.Mice.FirstOrDefault();

                if (!Directory.Exists(scenesDir))
                {
                    DebugManager.Error($"Папка сцен не найдена: {scenesDir}");
                    window.Close();
                    return;
                }

                var files = Directory.GetFiles(scenesDir, "*.es").OrderBy(f => f).ToArray();
                if (files.Length == 0)
                {
                    DebugManager.Error($"Нет сцен .es в папке {scenesDir}");
                    window.Close();
                    return;
                }

                _sceneFiles = files;
                lastTime = stopwatch.Elapsed.TotalSeconds;
                scene = LoadScene(sceneIndex, _sceneFiles);

                DebugManager.Custom("Vulkan + ESharp готов.", "SYSTEM", "#A0FF33");
            };

            window.Update += dt =>
            {
                if (scene == null || _sceneFiles == null) return;

                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float deltaTime = (float)(currentTime - lastTime);
                lastTime = currentTime;

                sceneTime += deltaTime;
                frameCount++;

                var kb = input?.Keyboards.FirstOrDefault();

                if (kb?.IsKeyPressed(Key.Escape) == true)
                    window.Close();

                if (kb?.IsKeyPressed(Key.Space) == true)
                {
                    sceneIndex = (sceneIndex + 1) % _sceneFiles.Length;
                    scene.Dispose();
                    arena.Reset();
                    scene = LoadScene(sceneIndex, _sceneFiles);
                    sceneTime = 0f;
                    frameCount = 0;
                    lastTime = stopwatch.Elapsed.TotalSeconds;
                }

                if (kb?.IsKeyPressed(Key.F5) == true)
                {
                    scene.Dispose();
                    arena.Reset();
                    scene = LoadScene(sceneIndex, _sceneFiles);
                    sceneTime = 0f;
                    frameCount = 0;
                    lastTime = stopwatch.Elapsed.TotalSeconds;
                }

                float aspect = window.FramebufferSize.X > 0
                    ? (float)window.FramebufferSize.X / window.FramebufferSize.Y
                    : 1f;

                float mxRaw = 0f, myRaw = 0f, mx = 0f;
                bool click = false;

                if (mouse != null)
                {
                    var pos = mouse.Position;
                    mxRaw = (pos.X / window.FramebufferSize.X) * 2f - 1f;
                    myRaw = -((pos.Y / window.FramebufferSize.Y) * 2f - 1f);
                    mx = mxRaw * aspect;
                    click = mouse.IsButtonPressed(MouseButton.Left);
                }

                ESharpEngine.Registry.RegisterVar("T", (double)sceneTime);
                ESharpEngine.Registry.RegisterVar("DT", (double)deltaTime);
                ESharpEngine.Registry.RegisterVar("MX", (double)mx);
                ESharpEngine.Registry.RegisterVar("MY", (double)myRaw);
                ESharpEngine.Registry.RegisterVar("MX_NDC", (double)mxRaw);
                ESharpEngine.Registry.RegisterVar("MY_NDC", (double)myRaw);
                ESharpEngine.Registry.RegisterVar("CLICK", click ? 1.0 : 0.0);
                ESharpEngine.Registry.RegisterVar("FRAME", (double)frameCount);

                scene.Update(deltaTime);
            };

            window.Render += _ =>
            {
                scene?.Render();
            };

            window.FramebufferResize += size =>
            {
                scene?.SetViewportSize(size.X, size.Y);
            };

            window.Closing += () =>
            {
                scene?.Dispose();
                input?.Dispose();
                ctx?.Dispose();
            };

            double instantTimer = 0, avgTimer = 0;
            int instantFrames = 0, avgFrames = 0;
            string baseTitle = "EurekaSharp [Vulkan]";

            window.Update += dt =>
            {
                instantFrames++; avgFrames++;
                instantTimer += dt; avgTimer += dt;

                if (instantTimer >= 0.5)
                {
                    int fps = (int)(instantFrames / instantTimer);
                    int avg = avgTimer > 0 ? (int)(avgFrames / avgTimer) : 0;
                    window.Title = $"{baseTitle} | FPS: {fps,5} | AVG: {avg,5}";
                    instantFrames = 0; instantTimer = 0;
                }
                if (avgTimer >= 5.0) { avgFrames = 0; avgTimer = 0; }
            };

            window.Run();
        }
    }
}