// File: Scene.cs

using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using PhysicsSimulation.Rendering.GPU;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public abstract class SceneGpu : IDisposable
    {
        protected List<PrimitiveGpu> _primitives = [];
        protected AnimationEngine _animationEngine;
        protected GeometryArena _arena;

        // Background color (RGB) and optional animation
        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private Vector3 _targetBgColor = new(0.1f, 0.1f, 0.1f);
        private float _bgAnimDuration = 0f;
        private float _bgAnimElapsed = 0f;
        private Vector3 _bgStartColor;

        protected SceneGpu(GeometryArena arena)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        }

        /// <summary>
        /// Adds a primitive to the scene
        /// </summary>
        public void AddPrimitive(PrimitiveGpu p)
        {
            _primitives.Add(p);
        }

        /// <summary>
        /// Initialize AnimationEngine and upload geometry
        /// </summary>
        public void Initialize()
        {
            _animationEngine = new AnimationEngine(_arena, _primitives);
            _animationEngine.UploadGeometryFromPrimitives();
        }

        /// <summary>
        /// Animate background color over time
        /// </summary>
        public void AnimateBackground(Vector3 targetColor, float duration)
        {
            _bgStartColor = _bgColor;
            _targetBgColor = targetColor;
            _bgAnimDuration = Math.Max(0.001f, duration);
            _bgAnimElapsed = 0f;
        }

        /// <summary>
        /// Call once per frame
        /// </summary>
        public virtual void Update(float deltaTime)
        {
            // update background animation
            if (_bgAnimElapsed < _bgAnimDuration)
            {
                _bgAnimElapsed += deltaTime;
                float t = Math.Clamp(_bgAnimElapsed / _bgAnimDuration, 0f, 1f);
                _bgColor = Vector3.Lerp(_bgStartColor, _targetBgColor, t);
            }

            // update AnimationEngine
            _animationEngine.UploadPendingAnimationsAndIndex();
            _animationEngine.UpdateAndDispatch(_bgAnimElapsed); // use elapsed for time; optionally pass global time
        }

        /// <summary>
        /// Render all primitives
        /// </summary>
        public virtual void Render()
        {
            // set background color
            GL.ClearColor(_bgColor.X, _bgColor.Y, _bgColor.Z, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | 
                     ClearBufferMask.DepthBufferBit);

            // render primitives
            _animationEngine.RenderAll();
        }

        public void Dispose()
        {
            _animationEngine?.Dispose();
        }
    }
}
