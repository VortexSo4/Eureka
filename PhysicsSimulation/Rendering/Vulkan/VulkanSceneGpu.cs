// ============================================================
//  VulkanSceneGpu.cs
//  EurekaSharp — Vulkan Backend (v2)
//
//  Изменения:
//    1. Upload timing: FlushPendingUploads(CurrentFrame) вызывается
//       в Render() ПОСЛЕ BeginFrame() — т.е. после WaitForFences.
//       Гарантирует что GPU завершил работу с буфером этого slot'а
//       прежде чем CPU начинает писать в него.
//
//    2. Conditional geometry rebuild: полный rebuild (arena.Reset + re-register)
//       происходит ТОЛЬКО если хотя бы один IsDynamic примитив фактически
//       изменил геометрию (IsGeometryRegistered == false после InvalidateGeometry).
//       Если динамические примитивы есть, но их геометрия не изменилась
//       с прошлого кадра — пропускаем дорогой rebuild целиком.
//
//    3. RecordComputeCommands и RenderAll теперь принимают int frame
//       (вместо int imageIndex) — используют правильный per-frame ресурс.
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
        // ── Общие поля сцены ───────────────────────────────────────────────
        protected List<PrimitiveGpu> _primitives = [];
        protected GeometryArena _arena;
        // Кэшированный флаг — есть ли примитивы с CPU-геометрией (IsDynamic).
        // Обновляется при AddPrimitive. Избегает LINQ Any() + аллокации каждый кадр.
        private bool _hasDynamicGeometry;
        // Pre-allocated — не создаём new List<> каждый кадр.
        private readonly List<VulkanAnimationEngine.DynOverride> _dynOverrides = new(32);
        // Кэш: есть ли хоть один примитив с HasDynCallbacks.
        private bool _hasDynCallbacks;

        // ── Vulkan-специфичные поля ────────────────────────────────────────
        protected readonly VulkanContext _vkCtx;
        protected readonly VulkanMemoryAllocator _vma;
        protected VulkanAnimationEngine? _vkAnimationEngine;

        // ── Анимация фона и время ──────────────────────────────────────────
        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private float _animTime;
        public float T => _animTime;
        public Vector3 BackgroundColor => _bgColor;

        private readonly Queue<BackgroundAnimation> _bgAnimQueue = new();
        private BackgroundAnimation? _currentBgAnim;
        private Vector3 _bgStartColorAtCurrentAnim;

        private record struct BackgroundAnimation(Vector3 TargetColor, float StartTime, float EndTime);

        public VulkanSceneGpu(VulkanContext ctx, GeometryArena arena)
        {
            _vkCtx = ctx ?? throw new ArgumentNullException(nameof(ctx));
            _vma   = new VulkanMemoryAllocator(ctx);
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        }

        // ── Управление примитивами ─────────────────────────────────────────

        public virtual void AddPrimitive(PrimitiveGpu p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            p.EnsureGeometryRegistered(_arena);

            if (p.PrimitiveId == -1)
            {
                p.PrimitiveId = _primitives.Count;
                DebugManager.Scene($"VulkanSceneGpu.AddPrimitive: Assigned PrimitiveId {p.PrimitiveId} to '{p.Name}'");
            }

            _primitives.Add(p);
            if (p.IsDynamic)      _hasDynamicGeometry = true;
            if (p.HasDynCallbacks) _hasDynCallbacks    = true;
            DebugManager.Scene($"VulkanSceneGpu.AddPrimitive: Added '{p.Name}' (ID: {p.PrimitiveId}), Vertices: {p.VertexCount}, Offset: {p.VertexOffsetRaw}");
        }

        public virtual T Add<T>(T primitive) where T : PrimitiveGpu
        {
            AddPrimitive(primitive);
            return primitive;
        }

        public virtual T Add<T>(T primitive, Action<T> configure) where T : PrimitiveGpu
        {
            configure(primitive);
            AddPrimitive(primitive);
            return primitive;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────

        public virtual void Setup() { }

        public virtual void Initialize()
        {
            DebugManager.Scene("VulkanSceneGpu.Initialize: Creating VulkanAnimationEngine...");

            _vkAnimationEngine = new VulkanAnimationEngine(_vkCtx, _vma, _arena, _primitives);
            _vkAnimationEngine.UploadGeometryFromPrimitives();
            _vkAnimationEngine.RebuildAllDescriptors();

            DebugManager.Scene("VulkanSceneGpu.Initialize: Done.");
        }

        // ── Анимация фона ──────────────────────────────────────────────────

        public virtual void AnimateBackground(Vector3 targetColor, float startTime, float endTime)
        {
            if (endTime <= startTime)
            {
                DebugManager.Warn($"AnimateBackground: Invalid time [{startTime}, {endTime}]. Ignored.");
                return;
            }
            _bgAnimQueue.Enqueue(new BackgroundAnimation(targetColor, startTime, endTime));
            DebugManager.Scene($"AnimateBackground: QUEUED → {targetColor} @ [{startTime:F3}s → {endTime:F3}s]");
        }

        // ── Update ─────────────────────────────────────────────────────────
        // Update только обновляет CPU-состояние.
        // Никаких GPU-записей здесь — GPU может быть занят предыдущим кадром.

        public virtual void Update(float deltaTime)
        {
            _animTime += deltaTime;

            // Background animation (CPU only)
            if (_currentBgAnim == null && _bgAnimQueue.Count > 0)
            {
                var next = _bgAnimQueue.Peek();
                if (_animTime >= next.StartTime)
                {
                    _currentBgAnim             = _bgAnimQueue.Dequeue();
                    _bgStartColorAtCurrentAnim = _bgColor;
                }
            }

            if (_currentBgAnim is BackgroundAnimation current)
            {
                if (_animTime <= current.EndTime)
                {
                    float t = (_animTime - current.StartTime) / (current.EndTime - current.StartTime);
                    _bgColor = Vector3.Lerp(_bgStartColorAtCurrentAnim, current.TargetColor, Math.Clamp(t, 0f, 1f));
                }
                else
                {
                    _bgColor       = current.TargetColor;
                    _currentBgAnim = null;
                }
            }

            // Dynamic geometry update:
            // Только IsDynamic примитивы (PlotGpu) пересчитывают вершины на месте.
            // arena.Reset() + полная перерегистрация НЕ нужны — Resolution фиксирован,
            // VertexOffsetRaw стабилен между кадрами, меняются только XY координаты.
            if (_hasDynamicGeometry)
            {
                bool anyDirty = false;
                foreach (var p in _primitives)
                    if (p.IsDynamic && p.RefreshDynamicVertices()) anyDirty = true;

                if (anyDirty)
                    _vkAnimationEngine?.UploadGeometryFromPrimitives(rebuildIndex: false, dynamicOnly: true);
            }

            // Один проход по примитивам: pending anims + dyn callbacks.
            // _dynOverrides pre-allocated — нет new List<> каждый кадр.
            // UploadPendingAnimationsAndIndex встроен сюда, чтобы не итерировать список дважды.
            _dynOverrides.Clear();
            bool hasNewAnims = false;

            foreach (var p in _primitives)
            {
                // ── Pending animations (было UploadPendingAnimationsAndIndex) ──
                if (p.PendingAnimations.Count > 0)
                {
                    for (int i = 0; i < p.PendingAnimations.Count; i++)
                    {
                        var entry = p.PendingAnimations[i];
                        if (!entry.PendingOnGpu) continue;
                        if (entry.PrimitiveId < 0) continue;
                        _vkAnimationEngine?.EnqueueAnimEntry(entry);
                        entry.PendingOnGpu = false;
                        p.PendingAnimations[i] = entry;
                        hasNewAnims = true;
                    }
                }

                // ── Dyn callbacks ──────────────────────────────────────────
                if (!p.HasDynCallbacks) continue;

                if (!p.DynInitialized)
                {
                    p.DynPosX = p.Position.X; p.DynPosY = p.Position.Y;
                    p.DynRot  = p.Rotation;   p.DynSc   = p.Scale;
                    p.DynCR   = p.Color.X;    p.DynCG   = p.Color.Y;
                    p.DynCB   = p.Color.Z;    p.DynCA   = p.Color.W;
                    p.DynInitialized = true;
                }

                // try/catch убран из горячего пути: скомпилированные замыкания не бросают
                // исключений в нормальной работе. Ошибки будут видны как NaN/Infinity в кадре.
                bool hasPos = false, hasRot = false, hasScale = false, hasColor = false;
                if (p.DynX        != null) { p.DynPosX = (float)p.DynX();        hasPos   = true; }
                if (p.DynY        != null) { p.DynPosY = (float)p.DynY();        hasPos   = true; }
                if (p.DynRotation != null) { p.DynRot  = (float)p.DynRotation(); hasRot   = true; }
                if (p.DynScale    != null) { p.DynSc   = (float)p.DynScale();    hasScale = true; }
                if (p.DynR        != null) { p.DynCR   = (float)p.DynR();        hasColor = true; }
                if (p.DynG        != null) { p.DynCG   = (float)p.DynG();        hasColor = true; }
                if (p.DynB        != null) { p.DynCB   = (float)p.DynB();        hasColor = true; }
                if (p.DynA        != null) { p.DynCA   = (float)p.DynA();        hasColor = true; }

                _dynOverrides.Add(new VulkanAnimationEngine.DynOverride
                {
                    Pid      = p.PrimitiveId,
                    PosX     = p.DynPosX, PosY  = p.DynPosY,
                    Rotation = p.DynRot,  Scale  = p.DynSc,
                    Color    = new Vector4(p.DynCR, p.DynCG, p.DynCB, p.DynCA),
                    HasPos   = hasPos, HasRot = hasRot, HasScale = hasScale, HasColor = hasColor
                });
            }

            if (hasNewAnims)
                _vkAnimationEngine?.FlushEnqueuedAnimEntries();
            if (_dynOverrides.Count > 0)
                _vkAnimationEngine?.ApplyDynOverrides(_dynOverrides);
        }

        // ── Render ─────────────────────────────────────────────────────────

        public virtual void Render()
        {
            int imageIndex = _vkCtx.BeginFrame();
            if (imageIndex < 0)
            {
                _vkCtx.RecreateSwapchain();
                _vkAnimationEngine?.NotifySwapchainRecreated(_vkCtx);
                return;
            }

            // После BeginFrame() fence[CurrentFrame] уже сигнализирован —
            // GPU завершил этот frame slot. Теперь безопасно писать в его буферы.
            int frame = _vkCtx.CurrentFrame;
            _vkAnimationEngine?.FlushPendingUploads(frame);

            RecordCommandBuffer(imageIndex, frame);
            _vkCtx.EndFrame(imageIndex);
        }

        private unsafe void RecordCommandBuffer(int imageIndex, int frame)
        {
            var cmd = _vkCtx.CommandBuffers[imageIndex];

            var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };

            _vkCtx.Vk.ResetCommandBuffer(cmd, CommandBufferResetFlags.None);
            VulkanContext.Check(_vkCtx.Vk.BeginCommandBuffer(cmd, &beginInfo), "BeginCommandBuffer");

            var clearValue = new ClearValue
            {
                Color = new ClearColorValue(_bgColor.X, _bgColor.Y, _bgColor.Z, 1.0f)
            };
            var renderPassBegin = new RenderPassBeginInfo
            {
                SType           = StructureType.RenderPassBeginInfo,
                RenderPass      = _vkCtx.RenderPass,
                Framebuffer     = _vkCtx.Framebuffers[imageIndex],
                RenderArea      = new Rect2D { Offset = new Offset2D(0, 0), Extent = _vkCtx.SwapchainExtent },
                ClearValueCount = 1,
                PClearValues    = &clearValue
            };

            // Compute pass — до RenderPass, в одном command buffer (один submit → без QueueWaitIdle)
            _vkAnimationEngine?.RecordComputeCommands(cmd, frame, _animTime);

            // Graphics pass
            _vkCtx.Vk.CmdBeginRenderPass(cmd, &renderPassBegin, SubpassContents.Inline);
            _vkAnimationEngine?.RenderAll(cmd, frame);
            _vkCtx.Vk.CmdEndRenderPass(cmd);

            VulkanContext.Check(_vkCtx.Vk.EndCommandBuffer(cmd), "EndCommandBuffer");
        }

        // ── Resize ─────────────────────────────────────────────────────────

        public virtual void SetViewportSize(int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            _vkCtx.RecreateSwapchain();
            _vkAnimationEngine?.NotifySwapchainRecreated(_vkCtx);

            if (_vkAnimationEngine != null)
                _vkAnimationEngine.AspectRatio = (float)width / height;
        }

        // ── Dispose ────────────────────────────────────────────────────────

        public virtual void Dispose()
        {
            _vkCtx.Vk.DeviceWaitIdle(_vkCtx.Device);
            _vkAnimationEngine?.Dispose();
            _vma?.Dispose();
        }
    }
}