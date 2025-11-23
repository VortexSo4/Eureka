// Scene.cs

using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
namespace PhysicsSimulation
{
    public abstract class SceneObject
    {
        public virtual void Update(float dt) { }
        public virtual void Render(int program, int vbo, int vao) { }
    }

    public class Scene
    {
        public static Scene? CurrentScene { get; set; }

        public List<SceneObject> Objects { get; } = new List<SceneObject>();
        private readonly List<(float time, Action action)> _actions = new List<(float, Action)>();
        public bool Recording { get; private set; } = true;
        private float _timelineOffset;
        public float CurrentTime { get; set; }

        // Background color animation
        private Vector3 _backgroundColor = new Vector3(0.12f, 0.12f, 0.12f);
        private Vector3 _backgroundStartColor;
        private Vector3 _backgroundTargetColor = new Vector3(0.12f, 0.12f, 0.12f);
        private float _backgroundAnimElapsed = 0f;
        private float _backgroundAnimDuration = 0f;
        private EaseType _backgroundEaseType = EaseType.Linear;
        private bool _backgroundAnimating = false;

        public Vector3 GetCurrentBackgroundColor()
        {
            return _backgroundColor;
        }

        public Vector3 BackgroundColor => _backgroundColor;

        public Scene()
        {
            CurrentScene = this;
            try
            {
                StartSlides();
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
        
        public virtual void Dispose()
        {
            DebugManager.Scene($"Disposing scene {GetType().Name}");
            Objects.Clear();
        }

        public virtual void Update(float dt)
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

        public virtual void Render(int program, int vbo, int vao)
        {
            foreach (var obj in Objects.ToArray())
            {
                obj.Render(program, vbo, vao);
            }
        }

        protected virtual void StartSlides()
        {
            DebugManager.Scene("Base Scene StartSlides called (empty)");
        }
    }
}