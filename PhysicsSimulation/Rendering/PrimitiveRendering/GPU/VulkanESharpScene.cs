// ============================================================
//  VulkanESharpScene.cs
//  EurekaSharp — Vulkan Backend
//
//  Мост между ESharpEngine (DSL-движок) и VulkanSceneGpu.
//
//  Проблема: ESharpEngine.CurrentScene типизирован как SceneGpu
//  (OpenGL), но нам нужен VulkanSceneGpu.
//
//  Решение: VulkanESharpScene наследует SceneGpu только как
//  "заглушку" для ESharpEngine, но переопределяет ВСЕ методы
//  и делегирует их во внутренний _vkScene (VulkanSceneGpu).
//  GL-код из SceneGpu никогда не вызывается.
//
// ============================================================

using System;
using System.Collections.Generic;
using System.Numerics;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;
using PhysicsSimulation.Rendering.Vulkan;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    /// <summary>
    /// Наследует SceneGpu для совместимости с ESharpEngine.CurrentScene,
    /// но всю реальную работу делегирует VulkanSceneGpu внутри.
    /// </summary>
    public class VulkanESharpScene : SceneGpu
    {
        private readonly VulkanSceneGpu _vk;

        // Expose T и BackgroundColor из Vulkan-сцены
        public new float T => _vk.T;
        public new Vector3 BackgroundColor => _vk.BackgroundColor;

        public VulkanESharpScene(VulkanContext ctx, GeometryArena arena)
            : base(arena)   // SceneGpu конструктор — не вызывает GL, просто хранит arena
        {
            _vk = new VulkanSceneGpu(ctx, arena);
        }

        // ── Primitive API (делегируем в VulkanSceneGpu) ───────────────────────

        public new void AddPrimitive(PrimitiveGpu p) => _vk.AddPrimitive(p);

        public new T Add<T>(T primitive) where T : PrimitiveGpu
            => _vk.Add(primitive);

        public new T Add<T>(T primitive, Action<T> configure) where T : PrimitiveGpu
            => _vk.Add(primitive, configure);

        // ── Background animation ──────────────────────────────────────────────

        public new void AnimateBackground(Vector3 targetColor, float startTime, float endTime)
            => _vk.AnimateBackground(targetColor, startTime, endTime);

        // ── Lifecycle ─────────────────────────────────────────────────────────

        public override void Setup()   => _vk.Setup();

        public override void Initialize()
        {
            // НЕ вызываем base.Initialize() — там создаётся OpenGL AnimationEngine
            _vk.Initialize();
        }

        public override void Update(float deltaTime) => _vk.Update(deltaTime);

        public override void Render()  => _vk.Render();

        // ── Resize ────────────────────────────────────────────────────────────

        public new void SetViewportSize(int width, int height)
            => _vk.SetViewportSize(width, height);

        // ── IDisposable ───────────────────────────────────────────────────────

        public new void Dispose()
        {
            _vk.Dispose();
            // НЕ вызываем base.Dispose() — там GL.DeleteBuffer и т.д.
        }
    }
}