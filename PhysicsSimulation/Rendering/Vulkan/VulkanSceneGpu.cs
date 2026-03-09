// ============================================================
//  VulkanSceneGpu.cs
//  EurekaSharp — Vulkan Backend
//
//  Drop-in замена SceneGpu.cs — наследует тот же интерфейс,
//  но вместо GL.* вызовов использует VulkanContext.
//
//  Что изменилось vs SceneGpu:
//    ─ GL.ClearColor → vkCmdBeginRenderPass с VkClearValue
//    ─ AnimationEngine (OpenGL) → VulkanAnimationEngine
//    ─ SetViewportSize → RecreateSwapchain
//    ─ Render() → BeginFrame / RecordCommandBuffer / EndFrame
//
//  Что НЕ изменилось (намеренно):
//    ─ AddPrimitive / Add<T> — тот же API
//    ─ AnimateBackground — та же очередь
//    ─ DynCallbacks — та же логика
//    ─ SceneGpu.Setup() / Initialize() — переопределяются в твоих сценах
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using Silk.NET.Vulkan;

namespace PhysicsSimulation.Rendering.Vulkan
{
    public class VulkanSceneGpu : IDisposable
    {
        // ── State (идентично SceneGpu) ────────────────────────────────────────
        protected List<PrimitiveGpu>    _primitives = [];
        protected VulkanAnimationEngine? _animationEngine;
        protected GeometryArena          _arena;

        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private float   _animTime;
        public  float   T => _animTime;

        private readonly Queue<BackgroundAnimation> _bgAnimQueue = new();
        private BackgroundAnimation? _currentBgAnim;
        private Vector3 _bgStartColorAtCurrentAnim;

        private record struct BackgroundAnimation(Vector3 TargetColor, float StartTime, float EndTime);

        // ── Vulkan ────────────────────────────────────────────────────────────
        protected readonly VulkanContext           _vkCtx;
        protected readonly VulkanMemoryAllocator   _vma;

        public VulkanSceneGpu(VulkanContext ctx, GeometryArena arena)
        {
            _vkCtx = ctx  ?? throw new ArgumentNullException(nameof(ctx));
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
            _vma   = new VulkanMemoryAllocator(ctx);
        }

        public Vector3 BackgroundColor => _bgColor;

        // ── Primitive management (API идентичен SceneGpu) ────────────────────

        public void AddPrimitive(PrimitiveGpu p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            p.EnsureGeometryRegistered(_arena);
            if (p.PrimitiveId == -1)
            {
                p.PrimitiveId = _primitives.Count;
                DebugManager.Scene($"VulkanSceneGpu.AddPrimitive: Assigned PrimitiveId {p.PrimitiveId} to '{p.Name}'");
            }
            _primitives.Add(p);
        }

        public T Add<T>(T primitive) where T : PrimitiveGpu
        {
            AddPrimitive(primitive);
            return primitive;
        }

        public T Add<T>(T primitive, Action<T> configure) where T : PrimitiveGpu
        {
            configure(primitive);
            AddPrimitive(primitive);
            return primitive;
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public virtual void Setup() { }

        public virtual void Initialize()
        {
            DebugManager.Scene("VulkanSceneGpu.Initialize: Creating VulkanAnimationEngine...");

            _animationEngine = new VulkanAnimationEngine(_vkCtx, _vma, _arena, _primitives);
            _animationEngine.UploadGeometryFromPrimitives();
            _animationEngine.RebuildAllDescriptors();

            DebugManager.Scene("VulkanSceneGpu.Initialize: Done.");
        }

        // ── Background animation (без изменений) ──────────────────────────────

        public void AnimateBackground(Vector3 targetColor, float startTime, float endTime)
        {
            if (endTime <= startTime) return;
            _bgAnimQueue.Enqueue(new BackgroundAnimation(targetColor, startTime, endTime));
        }

        // ── Update (идентичен SceneGpu.Update) ───────────────────────────────

        public virtual void Update(float deltaTime)
        {
            _animTime += deltaTime;

            // Фоновая анимация
            if (_currentBgAnim == null && _bgAnimQueue.Count > 0)
            {
                var next = _bgAnimQueue.Peek();
                if (_animTime >= next.StartTime)
                {
                    _currentBgAnim = _bgAnimQueue.Dequeue();
                    _bgStartColorAtCurrentAnim = _bgColor;
                }
            }

            if (_currentBgAnim is BackgroundAnimation current)
            {
                if (_animTime <= current.EndTime)
                {
                    float t = (_animTime - current.StartTime) / (current.EndTime - current.StartTime);
                    t = Math.Clamp(t, 0f, 1f);
                    _bgColor = Vector3.Lerp(_bgStartColorAtCurrentAnim, current.TargetColor, t);
                }
                else
                {
                    _bgColor = current.TargetColor;
                    _currentBgAnim = null;
                }
            }

            // Dynamic primitives — пересоздаём геометрию
            if (_primitives.Any(p => p.IsDynamic))
            {
                foreach (var p in _primitives) p.InvalidateGeometry();
                _arena.Reset();
                foreach (var p in _primitives) p.EnsureGeometryRegistered(_arena);
                _animationEngine!.RebuildAllDescriptors();
            }

            _animationEngine!.UploadPendingAnimationsAndIndex();
            _animationEngine.UpdateAndDispatch(_animTime);

            // DynCallbacks — идентично SceneGpu
            var dynOverrides = new List<VulkanAnimationEngine.DynOverride>();
            foreach (var p in _primitives)
            {
                if (!p.HasDynCallbacks) continue;

                if (!p.DynInitialized)
                {
                    p.DynPosX = p.Position.X; p.DynPosY = p.Position.Y;
                    p.DynRot  = p.Rotation;   p.DynSc   = p.Scale;
                    p.DynCR   = p.Color.X;    p.DynCG   = p.Color.Y;
                    p.DynCB   = p.Color.Z;    p.DynCA   = p.Color.W;
                    p.DynInitialized = true;
                }

                bool hasPos = false, hasRot = false, hasScale = false, hasColor = false;
                try
                {
                    if (p.DynX        != null) { p.DynPosX = (float)p.DynX();        hasPos   = true; }
                    if (p.DynY        != null) { p.DynPosY = (float)p.DynY();        hasPos   = true; }
                    if (p.DynRotation != null) { p.DynRot  = (float)p.DynRotation(); hasRot   = true; }
                    if (p.DynScale    != null) { p.DynSc   = (float)p.DynScale();    hasScale = true; }
                    if (p.DynR        != null) { p.DynCR   = (float)p.DynR();        hasColor = true; }
                    if (p.DynG        != null) { p.DynCG   = (float)p.DynG();        hasColor = true; }
                    if (p.DynB        != null) { p.DynCB   = (float)p.DynB();        hasColor = true; }
                    if (p.DynA        != null) { p.DynCA   = (float)p.DynA();        hasColor = true; }
                }
                catch (Exception ex)
                {
                    DebugManager.Warn($"dynExpr error on '{p.Name}': {ex.Message}");
                }

                dynOverrides.Add(new VulkanAnimationEngine.DynOverride
                {
                    Pid      = p.PrimitiveId,
                    PosX     = p.DynPosX, PosY  = p.DynPosY,
                    Rotation = p.DynRot,  Scale  = p.DynSc,
                    Color    = new Vector4(p.DynCR, p.DynCG, p.DynCB, p.DynCA),
                    HasPos   = hasPos, HasRot = hasRot, HasScale = hasScale, HasColor = hasColor
                });
            }
            if (dynOverrides.Count > 0)
                _animationEngine.ApplyDynOverrides(dynOverrides);
        }

        // ── Render — ГЛАВНОЕ ОТЛИЧИЕ от SceneGpu ─────────────────────────────

        public virtual void Render()
        {
            // 1. Запрашиваем следующий swapchain image
            int imageIndex = _vkCtx.BeginFrame();
            if (imageIndex < 0)
            {
                // Swapchain устарел (resize) — пропускаем кадр
                _vkCtx.RecreateSwapchain();
                _animationEngine?.NotifySwapchainRecreated(_vkCtx);
                return;
            }

            // 2. Записываем команды в command buffer этого image
            RecordCommandBuffer(imageIndex);

            // 3. Submit + Present
            _vkCtx.EndFrame(imageIndex);
        }

        private unsafe void RecordCommandBuffer(int imageIndex)
        {
            var cmd = _vkCtx.CommandBuffers[imageIndex];

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                // Без OneTimeSubmit — буферы перезаписываются каждый кадр
            };

            // Сброс и начало записи
            _vkCtx.Vk.ResetCommandBuffer(cmd, CommandBufferResetFlags.None);
            VulkanContext.Check(
                _vkCtx.Vk.BeginCommandBuffer(cmd, &beginInfo),
                "BeginCommandBuffer");

            // Clear color = _bgColor (аналог GL.ClearColor + GL.Clear)
            var clearValue = new ClearValue
            {
                Color = new ClearColorValue(_bgColor.X, _bgColor.Y, _bgColor.Z, 1.0f)
            };

            var renderPassBegin = new RenderPassBeginInfo
            {
                SType           = StructureType.RenderPassBeginInfo,
                RenderPass      = _vkCtx.RenderPass,
                Framebuffer     = _vkCtx.Framebuffers[imageIndex],
                RenderArea      = new Rect2D
                {
                    Offset = new Offset2D(0, 0),
                    Extent = _vkCtx.SwapchainExtent
                },
                ClearValueCount = 1,
                PClearValues    = &clearValue
            };

            _vkCtx.Vk.CmdBeginRenderPass(cmd, &renderPassBegin, SubpassContents.Inline);

            // Основная отрисовка через VulkanAnimationEngine
            _animationEngine?.RenderAll(cmd, imageIndex);

            _vkCtx.Vk.CmdEndRenderPass(cmd);

            VulkanContext.Check(
                _vkCtx.Vk.EndCommandBuffer(cmd),
                "EndCommandBuffer");
        }

        // ── Resize (аналог SceneGpu.SetViewportSize) ──────────────────────────

        public void SetViewportSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            _vkCtx.RecreateSwapchain();
            _animationEngine?.NotifySwapchainRecreated(_vkCtx);

            if (_animationEngine != null)
                _animationEngine.AspectRatio = (float)width / height;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        public void Dispose()
        {
            _vkCtx.Vk.DeviceWaitIdle(_vkCtx.Device);
            _animationEngine?.Dispose();
            _vma?.Dispose();
        }
    }
}
