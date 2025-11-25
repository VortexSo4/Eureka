using OpenTK.Mathematics;
using PhysicsSimulation.Base;
using PhysicsSimulation.Rendering.PrimitiveRendering;

namespace PhysicsSimulation.Rendering.SceneRendering
{
    public abstract class SceneObject
    {
        public virtual void Update(float dt) { }
        public virtual void Render(int program, int vbo, int vao) { }
    }

    public class Scene
    {
        public static Scene? CurrentScene { get; set; }

        public List<SceneObject> Objects { get; } = [];
        private readonly List<(float time, Action action)> _actions = [];
        public bool Recording { get; private set; }
        private float _timelineOffset;
        public float CurrentTime { get; set; }

        private Vector3 _backgroundColor;
        private Vector3 _backgroundStartColor;
        private Vector3 _backgroundTargetColor;
        private float _backgroundAnimElapsed;
        private float _backgroundAnimDuration;
        private EaseType _backgroundEaseType = EaseType.Linear;
        private bool _backgroundAnimating;

        public Vector3 GetCurrentBackgroundColor()
        {
            return _backgroundColor;
        }

        public Vector3 BackgroundColor => _backgroundColor;

        public Scene()
        {
            Recording = true;
            CurrentScene = this;
            try
            {
                StartSlidesBase();
            }
            finally
            {
                Recording = false;
                CurrentScene = null;
            }
            DebugManager.Scene($"Initializing Scene (timeline length: {_timelineOffset:F2}s)");
        }

        private void AddNow(params SceneObject[] objs)
        {
            Objects.AddRange(objs);
            DebugManager.Scene($"Added {objs.Length} objects, total: {Objects.Count}");
        }

        public Scene Add(params SceneObject[] objs)
        {
            if (Recording)
                _actions.Add((_timelineOffset, () => AddNow(objs)));
            else
                AddNow(objs);
            return this;
        }

        public Scene Wait(float duration = 1.0f)
        {
            if (Recording) _timelineOffset += duration; else CurrentTime += duration;
            return this;
        }

        public Scene Schedule(Action action)
        {
            if (Recording)
                _actions.Add((_timelineOffset, action));
            else
                action();
            return this;
        }

        public Scene AnimateBackgroundColor(Vector3 targetColor, float duration = 1f, EaseType ease = EaseType.Linear)
        {
            ScheduleOrExecute(() =>
            {
                _backgroundStartColor = _backgroundColor;          // сохраняем стартовый цвет
                _backgroundTargetColor = targetColor;             // целевой цвет
                _backgroundAnimElapsed = 0f;                      // сброс времени анимации
                _backgroundAnimDuration = Math.Max(duration, 0.01f); // защита от нуля
                _backgroundEaseType = ease;                       // easing
                _backgroundAnimating = true;                      // включаем анимацию
            });
            return this;
        }

        private void ScheduleOrExecute(Action action)
        {
            if (Recording)
                _actions.Add((_timelineOffset, action));
            else
                action();
        }
        
        public void Dispose()
        {
            DebugManager.Scene($"Disposing scene {GetType().Name}");
            Objects.Clear();
        }

        public void Update(float dt)
        {
            CurrentTime += dt;

            // Обновление анимации фона
            if (_backgroundAnimating)
            {
                _backgroundAnimElapsed += dt;
                float tRaw = Math.Min(1f, _backgroundAnimElapsed / _backgroundAnimDuration);
                float t = Easing.Ease(_backgroundEaseType, tRaw);

                // Интерполяция от стартового к целевому цвету
                _backgroundColor = Vector3.Lerp(_backgroundStartColor, _backgroundTargetColor, t);

                if (tRaw >= 1f)
                {
                    _backgroundColor = _backgroundTargetColor;
                    _backgroundAnimating = false;
                }
            }

            // Выполнение отложенных действий
            var actionsToExecute = _actions.Where(a => a.time <= CurrentTime).ToArray();
            foreach (var action in actionsToExecute)
            {
                try
                {
                    action.action();
                    _actions.Remove(action);
                }
                catch (Exception ex)
                {
                    DebugManager.Scene($"Scheduled action raised: {ex.Message}");
                }
            }

            // Обновление объектов сцены
            foreach (var obj in Objects.ToArray())
            {
                try
                {
                    obj.Update(dt);
                }
                catch (Exception ex)
                {
                    DebugManager.Scene($"Object update error: {ex.Message}");
                }
            }
        }

        public void Render(int program, int vbo, int vao)
        {
            foreach (var obj in Objects.ToArray())
            {
                obj.Render(program, vbo, vao);
            }
        }

        protected void StartSlidesBase() { StartSlides(); }
        protected virtual void StartSlides() { DebugManager.Scene("Base Scene StartSlides called (empty)"); }
    }
}