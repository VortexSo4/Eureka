﻿using System;
using System.Collections.Generic;
using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using PhysicsSimulation.Rendering.GPU;

namespace PhysicsSimulation.Rendering.PrimitiveRendering.GPU
{
    public abstract class SceneGpu : IDisposable
    {
        protected List<PrimitiveGpu> _primitives = new();
        protected AnimationEngine _animationEngine;
        protected GeometryArena _arena;

        // Background color (RGB) + animation
        private Vector3 _bgColor = new(0.1f, 0.1f, 0.1f);
        private Vector3 _targetBgColor = new(0.1f, 0.1f, 0.1f);
        private float _bgAnimDuration = 0f;
        private float _bgAnimElapsed = 0f;
        private Vector3 _bgStartColor;

        // Animation time
        private float _animTime = 0f;

        protected SceneGpu(GeometryArena arena)
        {
            _arena = arena ?? throw new ArgumentNullException(nameof(arena));
        }

        public Vector3 BackgroundColor => _bgColor;

        public void AddPrimitive(PrimitiveGpu p)
        {
            _primitives.Add(p);
        }

        public virtual void Initialize()
        {
            _animationEngine = new AnimationEngine(_arena, _primitives);
            _animationEngine.UploadGeometryFromPrimitives();
        }

        public void AnimateBackground(Vector3 targetColor, float duration)
        {
            _bgStartColor = _bgColor;
            _targetBgColor = targetColor;
            _bgAnimDuration = Math.Max(0.001f, duration);
            _bgAnimElapsed = 0f;
        }

        public virtual void Update(float deltaTime)
        {
            // Background color animation
            if (_bgAnimElapsed < _bgAnimDuration)
            {
                _bgAnimElapsed += deltaTime;
                float t = Math.Clamp(_bgAnimElapsed / _bgAnimDuration, 0f, 1f);
                _bgColor = Vector3.Lerp(_bgStartColor, _targetBgColor, t);
            }

            // Always advance animation time
            _animTime += deltaTime;

            // Update animations
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