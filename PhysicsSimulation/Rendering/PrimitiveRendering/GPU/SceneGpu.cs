using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using PhysicsSimulation.Rendering.GPU;
using PhysicsSimulation.Base;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public abstract class SceneGpu : IDisposable
    {
        protected List<PrimitiveGpu> _primitives = [];
        protected AnimationEngine _animationEngine;
        protected GeometryArena _arena;

        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private float _animTime;

        private readonly Queue<BackgroundAnimation> _bgAnimQueue = new();
        private BackgroundAnimation? _currentBgAnim;
        private Vector3 _bgStartColorAtCurrentAnim;

        private record struct BackgroundAnimation(Vector3 TargetColor, float StartTime, float EndTime);

        protected SceneGpu(GeometryArena arena)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        }

        public Vector3 BackgroundColor => _bgColor;

        public void AddPrimitive(PrimitiveGpu p) => _primitives.Add(p);

        public virtual void Initialize()
        {
            DebugManager.Gpu("SceneGpu.Initialize: Creating AnimationEngine...");
            _animationEngine = new AnimationEngine(_arena, _primitives);
            _animationEngine.UploadGeometryFromPrimitives();
            DebugManager.Gpu("SceneGpu.Initialize: AnimationEngine created and geometry uploaded.");
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

            DebugManager.Gpu($"AnimateBackground: QUEUED → {targetColor} @ [{startTime:F3}s → {endTime:F3}s] (will start from current color when time comes)");
        }

        public virtual void Update(float deltaTime)
        {
            _animTime += deltaTime;

            if (_currentBgAnim == null && _bgAnimQueue.Count > 0)
            {
                var next = _bgAnimQueue.Peek();
                if (_animTime >= next.StartTime)
                {
                    _currentBgAnim = _bgAnimQueue.Dequeue();
                    _bgStartColorAtCurrentAnim = _bgColor;
                    DebugManager.Gpu($"AnimateBackground: STARTED → {_currentBgAnim.Value.TargetColor} " +
                                     $"from {_bgStartColorAtCurrentAnim} @ t={_animTime:F3}s " +
                                     $"[{_currentBgAnim.Value.StartTime} → {_currentBgAnim.Value.EndTime}]");
                }
            }

            // === Обновление текущей анимации ===
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
                    DebugManager.Gpu($"AnimateBackground: FINISHED → {current.TargetColor} @ t={_animTime:F3}s");
                    _currentBgAnim = null;
                }
            }

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