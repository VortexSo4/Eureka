// ============================================================
//  Program.cs  (Vulkan версия)
//  Замена оригинального Program.cs на Vulkan backend.
//
//  Что изменилось vs оригинал:
//    - Helpers.InitOpenTkWindow()      → Silk.NET window
//    - Helpers.CreateGlContextAndProgram() → убрано (не нужно)
//    - GL.* вызовы                     → убраны
//    - new SceneGpu(arena)             → new VulkanESharpScene(ctx, arena)
//    - OpenTK.MouseState               → Silk.NET.Input.IMouse
//
//  Что НЕ изменилось:
//    - ESharpEngine и DSL — без изменений
//    - Логика переключения сцен (Space / F5)
//    - Registry переменные (T, DT, MX, MY, CLICK, FRAME)
//    - Путь к .es файлам
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
        private static void Main(string[] args)
        {
            DebugManager.Custom($"Current Directory: {Environment.CurrentDirectory}", "SYSTEM", "#A0FF33");
            DebugManager.Custom($"Current Version: {Environment.Version}",            "SYSTEM", "#A0FF33");
            DebugManager.Custom($"Starting E# Scene Runner [Vulkan]",                "E#",     "#FFFF00");

            bool debugShutdown = false;

            // ── Silk.NET окно (заменяет Helpers.InitOpenTkWindow) ─────────────
            var options = WindowOptions.DefaultVulkan with
            {
                Title                   = "EurekaSharp [Vulkan]",
                Size                    = new Vector2D<int>(1920, 1080),
                VSync                   = false,
                ShouldSwapAutomatically = false,
            };

            using var window = Window.Create(options);

            // ── Состояние ─────────────────────────────────────────────────────
            VulkanContext?       ctx    = null;
            VulkanESharpScene?   scene  = null;
            IInputContext?       input  = null;
            IMouse?              mouse  = null;

            GeometryArena arena      = new GeometryArena();
            var           stopwatch  = Stopwatch.StartNew();

            var scenesDir  = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Scenes", "Built-In-Scenes");
            int sceneIndex = 0;
            float sceneTime = 0f;
            long  frameCount = 0;
            double lastTime  = 0;

            // ── Загрузка .es сцены ────────────────────────────────────────────
            VulkanESharpScene LoadScene(int index, string[] files)
            {
                ESharpEngine.Registry.Clear();
                arena.Reset();

                var esharp = new ESharpEngine(arena);
                var s = new VulkanESharpScene(ctx!, arena);

                // Подключаем VulkanESharpScene к ESharpEngine как текущую сцену.
                // SetScene принимает SceneGpu — VulkanESharpScene наследует его.
                esharp.SetScene(s);
                esharp.LoadSceneFromFile(files[index]);

                s.SetViewportSize(window.FramebufferSize.X, window.FramebufferSize.Y);
                return s;
            }

            // ── Load ──────────────────────────────────────────────────────────
            window.Load += () =>
            {
                ctx   = new VulkanContext(window, enableValidation: true);
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

                // Сохраняем список файлов в замыкание через локальный массив
                _sceneFiles = files;
                lastTime    = stopwatch.Elapsed.TotalSeconds;
                scene       = LoadScene(sceneIndex, _sceneFiles);

                DebugManager.Custom("Vulkan + ESharp готов.", "SYSTEM", "#A0FF33");
            };

            // ── Update ────────────────────────────────────────────────────────
            window.Update += _ =>
            {
                if (scene == null || _sceneFiles == null) return;

                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float  dt          = (float)(currentTime - lastTime);
                lastTime           = currentTime;

                sceneTime  += dt;
                frameCount++;

                // Дебаг-таймаут
                if (debugShutdown && stopwatch.Elapsed.TotalSeconds >= 60)
                {
                    DebugManager.Custom("Benchmark finished (60s). Closing...", "SYSTEM", "#FFAA00");
                    window.Close();
                    return;
                }

                // Клавиши (Silk.NET.Input)
                var kb = input?.Keyboards.FirstOrDefault();

                if (kb?.IsKeyPressed(Key.Escape) == true)
                    window.Close();

                if (kb?.IsKeyPressed(Key.Space) == true)
                {
                    sceneIndex = (sceneIndex + 1) % _sceneFiles.Length;
                    scene.Dispose();
                    arena.Reset();
                    scene      = LoadScene(sceneIndex, _sceneFiles);
                    sceneTime  = 0f;
                    frameCount = 0;
                    lastTime   = stopwatch.Elapsed.TotalSeconds;
                }

                if (kb?.IsKeyPressed(Key.F5) == true)
                {
                    scene.Dispose();
                    arena.Reset();
                    scene      = LoadScene(sceneIndex, _sceneFiles);
                    sceneTime  = 0f;
                    frameCount = 0;
                    lastTime   = stopwatch.Elapsed.TotalSeconds;
                }

                // ── Registry переменные (идентично оригинальному Program.cs) ──
                float aspect = window.FramebufferSize.X > 0
                    ? (float)window.FramebufferSize.X / window.FramebufferSize.Y
                    : 1f;

                float mxRaw = 0f, myRaw = 0f, mx = 0f;
                bool  click = false;

                if (mouse != null)
                {
                    var pos = mouse.Position;
                    mxRaw = (pos.X / window.FramebufferSize.X) * 2f - 1f;
                    myRaw = -((pos.Y / window.FramebufferSize.Y) * 2f - 1f);
                    mx    = mxRaw * aspect;
                    click = mouse.IsButtonPressed(MouseButton.Left);
                }

                ESharpEngine.Registry.RegisterVar("T",      (double)sceneTime);
                ESharpEngine.Registry.RegisterVar("DT",     (double)dt);
                ESharpEngine.Registry.RegisterVar("MX",     (double)mx);
                ESharpEngine.Registry.RegisterVar("MY",     (double)myRaw);
                ESharpEngine.Registry.RegisterVar("MX_NDC", (double)mxRaw);
                ESharpEngine.Registry.RegisterVar("MY_NDC", (double)myRaw);
                ESharpEngine.Registry.RegisterVar("CLICK",  click ? 1.0 : 0.0);
                ESharpEngine.Registry.RegisterVar("FRAME",  (double)frameCount);

                scene.Update(dt);
            };

            // ── Render ────────────────────────────────────────────────────────
            window.Render += _ =>
            {
                scene?.Render();
            };

            // ── Resize ────────────────────────────────────────────────────────
            window.FramebufferResize += size =>
            {
                scene?.SetViewportSize(size.X, size.Y);
            };

            // ── Closing ───────────────────────────────────────────────────────
            window.Closing += () =>
            {
                scene?.Dispose();
                input?.Dispose();
                ctx?.Dispose();
            };

            // ── FPS в заголовке ───────────────────────────────────────────────
            double instantTimer = 0, avgTimer = 0;
            int    instantFrames = 0, avgFrames = 0;
            string baseTitle = "EurekaSharp [Vulkan]";

            window.Update += dt =>
            {
                instantFrames++; avgFrames++;
                instantTimer  += dt; avgTimer += dt;

                if (instantTimer >= 0.5)
                {
                    int fps = (int)(instantFrames / instantTimer);
                    int avg = avgTimer > 0 ? (int)(avgFrames / avgTimer) : 0;
                    window.Title   = $"{baseTitle} | FPS: {fps,5} | AVG: {avg,5}";
                    instantFrames  = 0; instantTimer = 0;
                }
                if (avgTimer >= 5.0) { avgFrames = 0; avgTimer = 0; }
            };

            window.Run();
        }

        // Список файлов сцен — доступен из замыканий
        private static string[]? _sceneFiles;
    }
}