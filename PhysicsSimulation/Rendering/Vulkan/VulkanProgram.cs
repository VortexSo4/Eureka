// ============================================================
//  VulkanProgram.cs
//  EurekaSharp — Vulkan Backend
//
//  Заменяет Helpers.InitOpenTkWindow + старый Program.cs.
//  Использует Silk.NET.Windowing — кроссплатформенно
//  (Windows, Linux, Android через MAUI.Essentials или ANativeWindow).
//
//  Использование:
//    VulkanProgram.Run<MyScene>(title: "My App");
//
//  MyScene должен наследовать VulkanSceneGpu.
//
// ============================================================

using System;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace PhysicsSimulation.Rendering.Vulkan
{
    public static class VulkanProgram
    {
        /// <summary>
        /// Точка входа для Vulkan-приложения.
        /// Создаёт окно Silk.NET, VulkanContext, и запускает сцену.
        ///
        /// Пример:
        ///   VulkanProgram.Run&lt;MyEurekaScene&gt;("Physics Demo");
        /// </summary>
        public static void Run<TScene>(
            string title      = "EurekaSharp",
            int    width      = 1920,
            int    height     = 1080,
            bool   fullscreen = false,
            bool   validation = true)
            where TScene : VulkanSceneGpu, new()
        {
            Run(
                factory: (ctx, arena) => (TScene)Activator.CreateInstance(typeof(TScene), ctx, arena)!,
                title: title, width: width, height: height,
                fullscreen: fullscreen, validation: validation);
        }

        /// <summary>
        /// Перегрузка с явной фабрикой — не использует рефлексию, совместима с AOT.
        ///
        /// Пример:
        ///   VulkanProgram.Run((ctx, arena) => new MyScene(ctx, arena), "Demo");
        /// </summary>
        public static void Run<TScene>(
            Func<VulkanContext, GeometryArena, TScene> factory,
            string title      = "EurekaSharp",
            int    width      = 1920,
            int    height     = 1080,
            bool   fullscreen = false,
            bool   validation = true)
            where TScene : VulkanSceneGpu
        {
            // Конфигурация окна Silk.NET
            var options = WindowOptions.DefaultVulkan with
            {
                Title            = title,
                Size             = new Vector2D<int>(width, height),
                WindowState      = fullscreen ? WindowState.Fullscreen : WindowState.Normal,
                VSync            = Program.V_SYNC,                     // аналог VSyncMode.Off
                ShouldSwapAutomatically = false,              // мы сами контролируем present
                API              = GraphicsAPI.DefaultVulkan
            };

            using var window = Window.Create(options);

            VulkanContext?  ctx     = null;
            VulkanSceneGpu? scene   = null;
            GeometryArena   arena   = new GeometryArena();

            double instantTimer = 0, avgTimer = 0;
            int    instantFrames = 0, avgFrames = 0;

            // ── Load: создаём Vulkan и сцену ─────────────────────────────────
            window.Load += () =>
            {
                ctx   = new VulkanContext(window, enableValidation: validation);
                scene = factory(ctx, arena);
                scene.Setup();
                scene.Initialize();

                Console.WriteLine($"[EurekaSharp] Vulkan инициализирован. GPU готов.");
            };

            // ── Update ────────────────────────────────────────────────────────
            window.Update += dt =>
            {
                scene?.Update((float)dt);

                // FPS в заголовке окна (аналог Helpers.UpdateFps)
                instantFrames++; avgFrames++;
                instantTimer  += dt; avgTimer += dt;

                if (instantTimer >= 0.5)
                {
                    double fps    = instantFrames / instantTimer;
                    double avgFps = avgFrames     / avgTimer;
                    window.Title  = $"{title} | FPS: {(int)fps,5} | AVG: {(int)avgFps,5}";
                    instantFrames = 0; instantTimer = 0;
                }
                if (avgTimer >= 5.0)
                {
                    avgFrames = 0; avgTimer = 0;
                }
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

            // ── Cleanup ───────────────────────────────────────────────────────
            window.Closing += () =>
            {
                scene?.Dispose();
                ctx?.Dispose();
            };

            window.Run();
        }
    }
}