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
            // Rebuild descriptors now that geometry is registered and all offsets/counts are set
            _animationEngine.RebuildAllDescriptors();
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
                // Must invalidate ALL primitives before arena reset —
                // otherwise static primitives keep stale VertexOffsetRaw
                // and RebuildAllDescriptors builds wrong MorphDescs for them.
                foreach (var p in _primitives)
                    p.InvalidateGeometry();

                _arena.Reset();

                foreach (var p in _primitives)
                    p.EnsureGeometryRegistered(_arena);

                _animationEngine.RebuildAllDescriptors();
            }

            // Затем стандартные анимации
            _animationEngine.UploadPendingAnimationsAndIndex();
            _animationEngine.UpdateAndDispatch(_animTime);

            // Apply dynamic expression overrides (dynPos / dynRot / dynColor / dynScale)
            // These run AFTER the compute shader so they always win over keyframe animations.
            var dynOverrides = new System.Collections.Generic.List<PhysicsSimulation.Rendering.GPU.AnimationEngine.DynOverride>();
            foreach (var p in _primitives)
            {
                if (!p.HasDynCallbacks) continue;

                // Seed CPU mirror from primitive's initial values on first frame
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
                    Base.DebugManager.Warn($"dynExpr error on '{p.Name}': {ex.Message}");
                }

                dynOverrides.Add(new PhysicsSimulation.Rendering.GPU.AnimationEngine.DynOverride
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

        // Called by Program.cs on window resize — avoids per-frame GL.GetInteger
        public void SetViewportSize(int width, int height)
        {
            if (height > 0 && width > 0 && _animationEngine != null)
                _animationEngine.AspectRatio = (float)width / height;
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