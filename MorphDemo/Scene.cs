namespace PhysicsSimulation
{
    public abstract class SceneObject
    {
        public virtual void Update(float dt) { }
        public virtual void Render(int program, int vbo) { }
    }

    public class Scene
    {
        public static Scene? CurrentScene { get; set; }

        public List<SceneObject> Objects { get; } = [];
        private readonly List<(float time, Action action)> _actions = [];
        public bool Recording { get; private set; } = true;
        private float _timelineOffset;
        public float CurrentTime { get; set; }

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
            Console.WriteLine($"Initializing Scene (timeline length: {_timelineOffset:F2}s)");
        }

        private void AddNow(params SceneObject[] objs)
        {
            Objects.AddRange(objs);
            Console.WriteLine($"Added {objs.Length} objects, total: {Objects.Count}");
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
            (Recording ? () => _actions.Add((_timelineOffset, action)) : action)();
            return this;
        }

        public virtual void Update(float dt)
        {
            CurrentTime += dt;
            foreach (var action in _actions.Where(a => a.time <= CurrentTime).ToArray())
            {
                try
                {
                    action.action();
                    _actions.Remove(action);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Scheduled action raised: {ex.Message}");
                }
            }

            foreach (var obj in Objects.ToArray())
            {
                try
                {
                    obj.Update(dt);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Object update error: {ex.Message}");
                }
            }
        }

        public virtual void Render(int program, int vbo)
        {
            foreach (var obj in Objects.ToArray())
            {
                obj.Render(program, vbo);
            }
        }

        protected virtual void StartSlides()
        {
            Console.WriteLine("Base Scene StartSlides called (empty)");
        }
    }
}