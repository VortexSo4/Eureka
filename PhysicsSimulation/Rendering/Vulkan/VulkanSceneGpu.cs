// ============================================================
//  VulkanSceneGpu.cs
//  EurekaSharp — Vulkan Backend
//
//  Наследует SceneGpu и переопределяет все методы для работы с Vulkan.
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
    public class VulkanSceneGpu : SceneGpu
    {
        // ── Vulkan специфичные поля ─────────────────────────────────────────
        protected readonly VulkanContext           _vkCtx;
        protected readonly VulkanMemoryAllocator   _vma;
        protected VulkanAnimationEngine?           _vkAnimationEngine; // отдельное поле для Vulkan-версии

        private readonly Queue<BackgroundAnimation> _bgAnimQueue = new();
        private BackgroundAnimation? _currentBgAnim;
        private Vector3 _bgStartColorAtCurrentAnim;
        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private float _animTime;

        private record struct BackgroundAnimation(Vector3 TargetColor, float StartTime, float EndTime);

        public VulkanSceneGpu(VulkanContext ctx, GeometryArena arena) : base(arena)
        {
            _vkCtx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _vma = new VulkanMemoryAllocator(ctx);
        }

        public new float T => _animTime;
        public new Vector3 BackgroundColor => _bgColor;

        // ── Primitive management (используем базовый список _primitives) ────

        public override void AddPrimitive(PrimitiveGpu p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            p.EnsureGeometryRegistered(_arena);
            if (p.PrimitiveId == -1)
            {
                p.PrimitiveId = _primitives.Count;
                DebugManager.Scene($"VulkanSceneGpu.AddPrimitive: Assigned PrimitiveId {p.PrimitiveId} to '{p.Name}'");
            }
            _primitives.Add(p);
            DebugManager.Scene($"VulkanSceneGpu.AddPrimitive: Added '{p.Name}' (ID: {p.PrimitiveId}), Vertices: {p.VertexCount}, Offset: {p.VertexOffsetRaw}");
        }

        public override T Add<T>(T primitive)
        {
            AddPrimitive(primitive);
            return primitive;
        }

        public override T Add<T>(T primitive, Action<T> configure)
        {
            configure(primitive);
            AddPrimitive(primitive);
            return primitive;
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        public override void Setup()
        {
            // Может быть переопределён в наследуемых сценах
        }

        public override void Initialize()
        {
            DebugManager.Scene("VulkanSceneGpu.Initialize: Creating VulkanAnimationEngine...");

            _vkAnimationEngine = new VulkanAnimationEngine(_vkCtx, _vma, _arena, _primitives);
            _vkAnimationEngine.UploadGeometryFromPrimitives();
            _vkAnimationEngine.RebuildAllDescriptors();

            DebugManager.Scene("VulkanSceneGpu.Initialize: Done.");
        }

        // ── Background animation ────────────────────────────────────────────

        public override void AnimateBackground(Vector3 targetColor, float startTime, float endTime)
        {
            if (endTime <= startTime) return;
            _bgAnimQueue.Enqueue(new BackgroundAnimation(targetColor, startTime, endTime));
        }

        // ── Update ──────────────────────────────────────────────────────────

        public override void Update(float deltaTime)
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
                _vkAnimationEngine!.RebuildAllDescriptors();
            }

            _vkAnimationEngine!.UploadPendingAnimationsAndIndex();
            _vkAnimationEngine.UpdateAndDispatch(_animTime);

            // DynCallbacks
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
                _vkAnimationEngine.ApplyDynOverrides(dynOverrides);
        }

        // ── Render ──────────────────────────────────────────────────────────

        public override void Render()
        {
            int imageIndex = _vkCtx.BeginFrame();
            if (imageIndex < 0)
            {
                _vkCtx.RecreateSwapchain();
                _vkAnimationEngine?.NotifySwapchainRecreated(_vkCtx);
                return;
            }

            RecordCommandBuffer(imageIndex);
            _vkCtx.EndFrame(imageIndex);
        }

        private unsafe void RecordCommandBuffer(int imageIndex)
        {
            var cmd = _vkCtx.CommandBuffers[imageIndex];

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
            };

            _vkCtx.Vk.ResetCommandBuffer(cmd, CommandBufferResetFlags.None);
            VulkanContext.Check(
                _vkCtx.Vk.BeginCommandBuffer(cmd, &beginInfo),
                "BeginCommandBuffer");

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

            _vkAnimationEngine?.RenderAll(cmd, imageIndex);

            _vkCtx.Vk.CmdEndRenderPass(cmd);

            VulkanContext.Check(
                _vkCtx.Vk.EndCommandBuffer(cmd),
                "EndCommandBuffer");
        }

        // ── Resize ──────────────────────────────────────────────────────────

        public override void SetViewportSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            _vkCtx.RecreateSwapchain();
            _vkAnimationEngine?.NotifySwapchainRecreated(_vkCtx);

            if (_vkAnimationEngine != null)
                _vkAnimationEngine.AspectRatio = (float)width / height;
        }

        // ── IDisposable ─────────────────────────────────────────────────────

        public override void Dispose()
        {
            _vkCtx.Vk.DeviceWaitIdle(_vkCtx.Device);
            _vkAnimationEngine?.Dispose();
            _vma?.Dispose();
            base.Dispose();
        }
    }
}