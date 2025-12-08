using System;
using System.Collections.Generic;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using PhysicsSimulation.Rendering.GPU;
using PhysicsSimulation.Base;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public class SceneGpu : IDisposable
    {
        protected List<PrimitiveGpu> _primitives = [];
        protected AnimationEngine _animationEngine;
        protected GeometryArena _arena;

        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private float _animTime;
        public float T => _animTime;

        private readonly Queue<BackgroundAnimation> _bgAnimQueue = new();
        private BackgroundAnimation? _currentBgAnim;
        private Vector3 _bgStartColorAtCurrentAnim;

        private record struct BackgroundAnimation(Vector3 TargetColor, float StartTime, float EndTime);

        public SceneGpu(GeometryArena arena)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        }

        public Vector3 BackgroundColor => _bgColor;

        public void AddPrimitive(PrimitiveGpu p)
        {
            if (p == null) throw new ArgumentNullException(nameof(p));
            p.EnsureGeometryRegistered(_arena);
            if (p.PrimitiveId == -1)
            {
                p.PrimitiveId = _primitives.Count;
                DebugManager.Scene($"SceneGpu.AddPrimitive: Assigned PrimitiveId {p.PrimitiveId} to '{p.Name}'");
            }
            _primitives.Add(p);
            DebugManager.Scene($"SceneGpu.AddPrimitive: Added '{p.Name}' (ID: {p.PrimitiveId}), Vertices: {p.VertexCount}, Offset: {p.VertexOffsetRaw}");
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

        public virtual void Setup() { }

        public virtual void Initialize()
        {
            DebugManager.Scene("SceneGpu.Initialize: Creating AnimationEngine...");
            _animationEngine = new AnimationEngine(_arena, _primitives);
            _animationEngine.UploadGeometryFromPrimitives();
            DebugManager.Scene("SceneGpu.Initialize: AnimationEngine created and geometry uploaded.");
        }

        public void AnimateBackground(Vector3 targetColor, float startTime, float endTime)
        {
            if (endTime <= startTime)
            {
                DebugManager.Warn($"AnimateBackground: Invalid time [{startTime}, {endTime}]. Ignored.");
                return;
            }

            var anim = new BackgroundAnimation(targetColor, startTime, endTime);
            _bgAnimQueue.Enqueue(anim);

            DebugManager.Scene($"AnimateBackground: QUEUED → {targetColor} @ [{startTime:F3}s → {endTime:F3}s] (will start from current color when time comes)");
        }

        public virtual void Update(float deltaTime)
        {
            _animTime += deltaTime;

            // === Анимация фона ===
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

            if (_primitives.Any(p => p.IsDynamic))
            {
                foreach (var p in _primitives.Where(p => p.IsDynamic))
                    p.InvalidateGeometry();

                _arena.Reset();

                foreach (var p in _primitives)
                    p.EnsureGeometryRegistered(_arena);

                _animationEngine.RebuildAllDescriptors();
            }

            // Затем стандартные анимации
            _animationEngine.UploadPendingAnimationsAndIndex();
            _animationEngine.UpdateAndDispatch(_animTime);
        }

        public virtual void Render()
        {
            GL.ClearColor(_bgColor.X, _bgColor.Y, _bgColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _animationEngine.RenderAll();
        }

        public void Dispose()
        {
            _animationEngine?.Dispose();
        }
    }
}