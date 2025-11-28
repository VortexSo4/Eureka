using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using PhysicsSimulation.Base.Utilities;
using PhysicsSimulation.Rendering.SceneRendering;
using PhysicsSimulation.Rendering.TextRendering;
using SkiaSharp;

namespace PhysicsSimulation.Rendering.PrimitiveRendering
{
    // ------------------ EASING ------------------
    public enum EaseType { Linear, EaseIn, EaseOut, EaseInOut }

    public static class Easing
    {
        public static float Ease(EaseType type, float t) => type switch
        {
            EaseType.Linear => t,
            EaseType.EaseIn => t * t,
            EaseType.EaseOut => 1 - (1 - t) * (1 - t),
            EaseType.EaseInOut => t * t * (3 - 2 * t),
            _ => t
        };
    }

    // ------------------ PRIMITIVE BASE (optimized) ------------------
    public abstract class Primitive(
        float x = 0f,
        float y = 0f,
        bool filled = false,
        Vector3 color = default,
        float vx = 0f,
        float vy = 0f)
        : SceneObject
    {
        // transform state
        public float X { get; set; } = x;
        public float Y { get; set; } = y;
        public float Scale { get; set; } = 1f;
        public float Rotation { get; set; }
        public Vector3 Color { get; set; } = color == default ? Vector3.One : color;
        public float LineWidth { get; set; } = 1f;
        public bool Filled { get; set; } = filled;
        public bool Closed { get; set; } = true;

        // simple physics
        public float Vx { get; set; } = vx;
        public float Vy { get; set; } = vy;

        // animations
        protected readonly List<PropertyAnimation> Animations = [];
        protected ShapeAnimation? ShapeAnim { get; set; }

        // custom boundaries and morphing
        protected List<Vector2>? CustomBoundary { get; set; }
        protected List<Vector2>? BoundaryVerts { get; set; }

        protected void ScheduleOrExecute(Action action) =>
            (Scene.CurrentScene?.Recording == true ? () => Scene.CurrentScene.Schedule(action) : action)();

        public override void Update(float dt)
        {
            // physics
            X += Vx * dt;
            Y += Vy * dt;
            if (Math.Abs(X) > 1f) Vx = -Vx;
            if (Math.Abs(Y) > 1f) Vy = -Vy;

            // animations
            ProcessPropertyAnimations(dt);
            ProcessShapeAnimation(dt);
        }

        private void ProcessPropertyAnimations(float dt)
        {
            for (int i = Animations.Count - 1; i >= 0; i--)
            {
                var anim = Animations[i];
                anim.Elapsed += dt;
                float tRaw = Math.Min(1f, anim.Elapsed / Math.Max(1e-9f, anim.Duration));
                float t = Easing.Ease(anim.Ease, tRaw);

                anim.Apply(t);

                if (tRaw >= 1f) Animations.RemoveAt(i);
            }
        }

        private void ProcessShapeAnimation(float dt)
        {
            if (ShapeAnim == null) return;
            ShapeAnim.Elapsed += dt;
            float tRaw = Math.Min(1f, ShapeAnim.Elapsed / Math.Max(1e-9f, ShapeAnim.Duration));
            float t = Easing.Ease(ShapeAnim.Ease, tRaw);

            // Start и Target уже нормализованы (локальные) — просто lerp по парам
            var interpolated = ShapeAnim.Start.Zip(ShapeAnim.Target, (s, targ) =>
                float.IsNaN(s.X) || float.IsNaN(s.Y) ? new Vector2(float.NaN, float.NaN) : Vector2.Lerp(s, targ, t)
            ).ToList();

            // Записываем локальные вершины. TransformVerts применит X/Y, Rotation, Scale как обычно.
            BoundaryVerts = interpolated;

            if (tRaw >= 1f)
            {
                CustomBoundary = ShapeAnim.FinalTargetBoundary ?? ShapeAnim.Target;
                Filled = ShapeAnim.TargetFilled;
                // если был флаг скрытия target — восстановим его
                if (ShapeAnim.RestoreTargetOnFinish && ShapeAnim.TargetToRestore != null)
                {
                    var tgt = ShapeAnim.TargetToRestore;
                    tgt.CustomBoundary = ShapeAnim.OriginalTargetCustomBoundary;
                    tgt.Filled = ShapeAnim.TargetFilled;
                }

                ShapeAnim = null;
            }
        }

        public override void Render(int program, int vbo, int vao)
        {
            var source = ShapeAnim != null ? BoundaryVerts : CustomBoundary ?? GetBoundaryVerts();
            if (source == null || source.Count == 0) return;

            // Разбиваем source на сегменты по разделителю (NaN, NaN).
            var segments = new List<List<Vector2>>();
            var current = new List<Vector2>();
            foreach (var p in source)
            {
                if (float.IsNaN(p.X) || float.IsNaN(p.Y))
                {
                    if (current.Count > 0)
                    {
                        segments.Add(current);
                        current = [];
                    }
                    // пропускаем разделитель
                }
                else
                {
                    current.Add(p);
                }
            }

            if (current.Count > 0) segments.Add(current);

            // --- Предварительно узнаем, поддерживает ли шейдер uniform-трансформации ---
            GL.UseProgram(program);
            int locUTranslate = GL.GetUniformLocation(program, "u_translate");
            int locUCos = GL.GetUniformLocation(program, "u_cos");
            int locUSin = GL.GetUniformLocation(program, "u_sin");
            int locUScale = GL.GetUniformLocation(program, "u_scale");
            int locAspect = GL.GetUniformLocation(program, "aspectRatio");

            bool shaderHasTransform = locUTranslate >= 0 && locUCos >= 0 && locUSin >= 0 && locUScale >= 0;

            // если шейдер ожидает aspectRatio — посчитаем и установим
            if (locAspect >= 0)
            {
                // Получаем текущий вьюпорт (x,y,w,h)
                var vp = new int[4];
                GL.GetInteger(GetPName.Viewport, vp);
                float aspect = 1f;
                if (vp[2] != 0) aspect = vp[3] / (float)vp[2]; // height / width — так, как ты использовал раньше
                GL.Uniform1(locAspect, aspect);
            }

            foreach (var seg in segments)
            {
                if (seg.Count == 0) continue;

                List<Vector3> drawVerts;

                if (shaderHasTransform)
                {
                    // Отправляем локальные вершины (без CPU-transform), и устанавливаем униформы для трансформации
                    // 1) подготовим verts как Vector3 (z = 0)
                    var raw = seg.Select(v => new Vector3(v.X, v.Y, 0f)).ToList();

                    // 2) установим униформы трансформации для этого примитива
                    // translate = (X, Y)
                    GL.Uniform2(locUTranslate, X, Y);
                    // cos, sin от Rotation и Scale
                    float c = MathF.Cos(Rotation);
                    float s = MathF.Sin(Rotation);
                    GL.Uniform1(locUCos, c);
                    GL.Uniform1(locUSin, s);
                    GL.Uniform1(locUScale, Scale);

                    // 3) подготовим drawVerts (GPU применит трансформацию)
                    drawVerts = PrepareDrawVerts(raw);
                }
                else
                {
                    // Старый путь — применяем трансформы на CPU
                    var transformed = TransformVerts(seg);
                    drawVerts = PrepareDrawVerts(transformed);
                }

                // И наконец рендерим (RenderVerts сам загружает VBO/VAO и отрисовывает)
                RenderVerts(drawVerts, Filled, program, vbo, Color, LineWidth);
            }

            // отвязываемся от программы (чтобы не влиял на следующий draw)
            GL.UseProgram(0);
        }


        protected virtual List<Vector3> TransformVerts(List<Vector2> verts)
        {
            float cos = MathF.Cos(Rotation) * Scale;
            float sin = MathF.Sin(Rotation) * Scale;
            var outv = new List<Vector3>(verts.Count);
            foreach (var v in verts)
                outv.Add(new Vector3(v.X * cos - v.Y * sin + X, v.X * sin + v.Y * cos + Y, 0f));
            return outv;
        }

        protected List<Vector3> PrepareDrawVerts(List<Vector3> verts)
        {
            if (verts.Count < 2) return [];

            // Ensure the contour is closed
            var closedVerts = new List<Vector3>(verts);
            if (Closed && verts[0] != verts[^1])
                closedVerts.Add(verts[0]);

            if (filled)
            {
                if (closedVerts.Count < 3) return [];
                var centroid = closedVerts.Aggregate(Vector3.Zero, (s, v) => s + v) / closedVerts.Count;
                var fan = new List<Vector3>(closedVerts.Count + 1) { centroid };
                fan.AddRange(closedVerts);
                return fan;
            }
            return closedVerts;
        }

        // reuse VAO per VBO
        private static readonly Dictionary<int, int> VboToVao = new();

        protected static void RenderVerts(List<Vector3> verts, bool filled, int program, int vbo, Vector3 color,
            float lineWidth)
        {
            if (verts.Count == 0) return;

            GL.UseProgram(program);
            int colorLoc = GL.GetUniformLocation(program, "u_color");
            if (colorLoc >= 0) GL.Uniform3(colorLoc, color);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            // copy to temporary array - acceptable here; further optimization possible with persistent buffers
            var arr = verts.ToArray();
            GL.BufferData(BufferTarget.ArrayBuffer, arr.Length * Vector3.SizeInBytes, arr, BufferUsageHint.DynamicDraw);

            if (!VboToVao.TryGetValue(vbo, out int vao))
            {
                vao = GL.GenVertexArray();
                GL.BindVertexArray(vao);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
                VboToVao[vbo] = vao;
            }
            else GL.BindVertexArray(vao);

            if (!filled) GL.LineWidth(lineWidth);
            GL.DrawArrays(filled ? PrimitiveType.TriangleFan : PrimitiveType.LineStrip, 0, arr.Length);

            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        // --- Convenience animation helpers (delegate-based) ---
        public Primitive Animate(Action<float> setter, float start, float target, float duration = 1f,
            EaseType ease = EaseType.EaseInOut)
        {
            ScheduleOrExecute(() =>
            {
                Animations.Add(new PropertyAnimation
                {
                    Apply = t => setter(start + (target - start) * t),
                    Duration = duration,
                    Ease = ease,
                });
            });
            return this;
        }

        // New: lazy-start overload — computes start value when scheduled/executed
        public Primitive Animate(Func<float> startGetter, Action<float> setter, float target, float duration = 1f,
            EaseType ease = EaseType.EaseInOut)
        {
            ScheduleOrExecute(() =>
            {
                float start = startGetter();
                Animations.Add(new PropertyAnimation
                {
                    Apply = t => setter(start + (target - start) * t),
                    Duration = duration,
                    Ease = ease,
                });
            });
            return this;
        }

        public Primitive AnimateColor(Vector3 target, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            ScheduleOrExecute(() =>
            {
                Vector3 start = Color; // read at execution time
                Animations.Add(new PropertyAnimation
                {
                    Apply = t => Color = Vector3.Lerp(start, target, t),
                    Duration = duration,
                    Ease = ease
                });
            });
            return this;
        }

        public Primitive MoveTo(float x, float y, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Animate(() => X, v => X = v, x, duration, ease);
            Animate(() => Y, v => Y = v, y, duration, ease);
            return this;
        }

        public Primitive Resize(float targetScale, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Animate(() => Scale, v => Scale = v, targetScale, duration, ease);
            return this;
        }

        public Primitive RotateTo(float targetRotationDegrees, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            float targetRad = MathHelper.DegreesToRadians(targetRotationDegrees);
            Animate(() => Rotation, v => Rotation = v, targetRad, duration, ease);
            return this;
        }

        public Primitive SetLineWidth(float target, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Animate(() => LineWidth, v => LineWidth = v, target, duration, ease);
            return this;
        }

        public Primitive SetFilled(bool filled, float duration = 0f)
        {
            Filled = filled;
            return this;
        }

        public Primitive Draw(float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Scale = 0f;
            Animate(v => Scale = v, 0f, 1f, duration, ease);
            return this;
        }

        public Primitive MorphTo(Primitive target, float duration = 2f, EaseType ease = EaseType.EaseInOut,
            bool hideTargetDuringMorph = false)
        {
            // Помещаем всю логику внутрь ScheduleOrExecute, чтобы при Recording=true
            // создание ShapeAnim и регистрация property-анимаций происходили синхронно
            // в момент срабатывания по timeline.
            ScheduleOrExecute(() =>
            {
                // --- helper deep copy ---
                List<Vector2> DeepCopy(List<Vector2>? src) =>
                    src?.Select(v => new Vector2(v.X, v.Y)).ToList() ?? [];

                // pluck verts (deep copy чтобы избежать алиасинга на кеш CharMap и т.д.)
                var startVerts = DeepCopy(CustomBoundary ?? BoundaryVerts ?? GetBoundaryVerts());
                var targetVerts = DeepCopy(target.GetBoundaryVerts());

                // Получаем bounding centers (внутренняя утилита)
                var startBounds = GetBounds(startVerts);
                var targetBounds = GetBounds(targetVerts);

                // Нормализуем в локальные координаты (убираем центры) — теперь это локальные формы
                var normalizedStart = NormalizeVerts(startVerts, startBounds);
                var normalizedTarget = NormalizeVerts(targetVerts, targetBounds);

                // Подгоним длину списков как раньше
                if (normalizedStart.Count != normalizedTarget.Count)
                {
                    if (normalizedStart.Count < normalizedTarget.Count)
                        normalizedStart = Helpers.ResizeVertexList(normalizedStart, normalizedTarget.Count);
                    else
                        normalizedTarget = Helpers.ResizeVertexList(normalizedTarget, normalizedStart.Count);
                }

                // Опционально: скрыть target на время морфа (чтобы не было дублирования рендера)
                List<Vector2>? origTargetCustom = null;
                if (hideTargetDuringMorph)
                {
                    origTargetCustom = target.CustomBoundary;
                    target.CustomBoundary = []; // временно пусто — target не будет рендериться
                    target.Animations.Clear(); // приостанавливаем/убираем его property-анимации на время морфа
                    target.Filled = false;
                }

                // Сохраняем локальную целевую границу (deep copy)
                var finalLocalTarget = DeepCopy(normalizedTarget);

                ShapeAnim = new ShapeAnimation
                {
                    Start = normalizedStart,
                    Target = normalizedTarget,
                    Duration = duration,
                    Ease = ease,
                    TargetFilled = target.Filled,
                    FinalTargetBoundary = finalLocalTarget,
                    RestoreTargetOnFinish = hideTargetDuringMorph,
                    TargetToRestore = hideTargetDuringMorph ? target : null,
                    // если скрывали — сохраним оригинал для восстановления
                    OriginalTargetCustomBoundary = origTargetCustom
                };

                // BoundaryVerts держим в локальных координатах — TransformVerts прибавит X/Y/Scale/Rotation
                BoundaryVerts = normalizedStart;
            });

            return this;
        }

        private (Vector2 Min, Vector2 Max, Vector2 Center) GetBounds(List<Vector2> verts)
        {
            if (verts.Count == 0)
                return (Vector2.Zero, Vector2.Zero, Vector2.Zero);

            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;

            foreach (var v in verts)
            {
                if (float.IsNaN(v.X) || float.IsNaN(v.Y)) continue;
        
                if (v.X < minX) minX = v.X;
                if (v.X > maxX) maxX = v.X;
                if (v.Y < minY) minY = v.Y;
                if (v.Y > maxY) maxY = v.Y;
            }

            var center = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);
            return (new Vector2(minX, minY), new Vector2(maxX, maxY), center);
        }

        private List<Vector2> NormalizeVerts(List<Vector2> verts, (Vector2 Min, Vector2 Max, Vector2 Center) bounds)
        {
            var result = new List<Vector2>(verts.Count);
            foreach (var v in verts)
            {
                if (float.IsNaN(v.X) || float.IsNaN(v.Y))
                {
                    result.Add(new Vector2(float.NaN, float.NaN));
                }
                else
                {
                    // Центрируем вершины относительно центра bounding box'а
                    result.Add(v - bounds.Center);
                }
            }
            return result;
        }

        public abstract List<Vector2> GetBoundaryVerts();

        // --- Internal animation classes ---
        protected class PropertyAnimation
        {
            public Action<float> Apply = _ => { };
            public float Duration;
            public float Elapsed;
            public EaseType Ease;
        }

        protected class ShapeAnimation
        {
            public List<Vector2> Start = [];
            public List<Vector2> Target = [];
            public float Duration;
            public float Elapsed;
            public EaseType Ease;
            public bool TargetFilled;
            public List<Vector2>? FinalTargetBoundary;
            public bool RestoreTargetOnFinish;
            public Primitive? TargetToRestore;
            public List<Vector2>? OriginalTargetCustomBoundary;
        }
    }

    public class CompositePrimitive : Primitive
    {
        // Настройки отдельного ребёнка (персональные overrides)
        public class ChildSettings
        {
            // если true — позиция ребёнка интерпретируется как глобальная (не умножается/не поворачивается композитом)
            public bool UseGlobalPosition = false;

            // если true — вращение ребёнка интерпретируется как глобальное (не прибавляется rotation композита)
            public bool UseGlobalRotation = false;

            // если true — масштаб ребёнка интерпретируется как глобальный (не умножается на Scale композита)
            public bool UseGlobalScale = false;

            // явные override'ы (если заданы, имеют приоритет)
            public Vector2? GlobalPositionOverride = null;
            public float? GlobalRotationOverride = null; // radians
            public float? GlobalScaleOverride = null;
        }

        private readonly List<Primitive> _children = [];
        private readonly List<ChildSettings> _settings = [];

        public IReadOnlyList<Primitive> Children => _children.AsReadOnly();

        public CompositePrimitive(float x = 0f, float y = 0f, bool filled = false, Vector3 color = default)
            : base(x, y, filled, color)
        {
        }

        // Добавление ребёнка (запрет на вложенные CompositePrimitive)
        public CompositePrimitive Add(Primitive child, ChildSettings? settings = null)
        {
            if (child == null) throw new ArgumentNullException(nameof(child));
            if (child is CompositePrimitive)
                throw new InvalidOperationException("CompositePrimitive cannot contain another CompositePrimitive.");

            _children.Add(child);
            _settings.Add(settings ?? new ChildSettings());
            return this;
        }

        public bool Remove(Primitive child)
        {
            int idx = _children.IndexOf(child);
            if (idx < 0) return false;
            _children.RemoveAt(idx);
            _settings.RemoveAt(idx);
            return true;
        }

        public ChildSettings GetSettingsFor(Primitive child)
        {
            int idx = _children.IndexOf(child);
            if (idx < 0) throw new ArgumentException("Child not found in this composite.", nameof(child));
            return _settings[idx];
        }

        public void SetSettingsFor(Primitive child, ChildSettings settings)
        {
            int idx = _children.IndexOf(child);
            if (idx < 0) throw new ArgumentException("Child not found in this composite.", nameof(child));
            _settings[idx] = settings ?? new ChildSettings();
        }

        // Обновляем composite (его собственные анимации/физику) и детей (их локальные анимации/физику)
        public override void Update(float dt)
        {
            base.Update(dt); // обновит X/Y по Vx/Vy и property-анимации composite'а
            // обновляем детей — их X/Y находятся в локальных координатах composite
            foreach (var ch in _children)
                ch.Update(dt);
        }

        // GetBoundaryVerts — flatten всех детей в локальные координаты composite,
        // разделяя каждый примитив NaN-сепаратором (как у базовых примитивов).
        public override List<Vector2> GetBoundaryVerts()
        {
            var all = new List<Vector2>();
            for (int i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                var verts = child.GetBoundaryVerts();
                if (verts == null || verts.Count == 0) continue;

                // Преобразуем вершины ребенка из его локального пространства в composite-local
                // (аналог Primitive.TransformVerts, но на месте)
                float cos = MathF.Cos(child.Rotation) * child.Scale;
                float sin = MathF.Sin(child.Rotation) * child.Scale;

                foreach (var v in verts)
                {
                    if (float.IsNaN(v.X) || float.IsNaN(v.Y))
                    {
                        all.Add(new Vector2(float.NaN, float.NaN));
                        continue;
                    }

                    float tx = v.X * cos - v.Y * sin + child.X;
                    float ty = v.X * sin + v.Y * cos + child.Y;
                    all.Add(new Vector2(tx, ty));
                }

                // разделитель между примитивами (если не последний)
                if (i < _children.Count - 1)
                    all.Add(new Vector2(float.NaN, float.NaN));
            }

            // Если пусто — вернуть пустой список
            return all;
        }

        // Render: если морф или custom boundary — используем базовый Render (отрисовка одного контурa).
        // Иначе — рендерим детей, временно подменяя у них X/Y/Scale/Rotation на "финальные" глобальные,
        // затем восстанавливаем локальные значения.
        public override void Render(int program, int vbo, int vao)
        {
            // если composite в состоянии морфинга — отрисуем единый контур (base.Render использует this.GetBoundaryVerts())
            if (ShapeAnim != null || CustomBoundary != null)
            {
                base.Render(program, vbo, vao);
                return;
            }

            // предварительно вычислим cos/sin композита и масштаб — пригодится
            float cC = MathF.Cos(Rotation);
            float sC = MathF.Sin(Rotation);
            float sScale = Scale;

            for (int i = 0; i < _children.Count; i++)
            {
                var child = _children[i];
                var set = _settings[i];

                // сохранение оригинальных локальных значений
                float origX = child.X, origY = child.Y, origScale = child.Scale, origRot = child.Rotation;

                // вычисляем финальные значения (глобальные) для передачи в child.Render
                float finalX, finalY, finalScale, finalRot;

                // --- Позиция ---
                if (set.GlobalPositionOverride.HasValue)
                {
                    var p = set.GlobalPositionOverride.Value;
                    finalX = p.X;
                    finalY = p.Y;
                }
                else if (set.UseGlobalPosition)
                {
                    // child.X/Y уже заданы в глобальном пространстве — не участвуют в transform композита
                    finalX = child.X;
                    finalY = child.Y;
                }
                else
                {
                    // обычная композиция: сначала scale child local position, затем rotate composite, затем translate composite
                    float lx = child.X * sScale;
                    float ly = child.Y * sScale;
                    finalX = lx * cC - ly * sC + X;
                    finalY = lx * sC + ly * cC + Y;
                }

                // --- Rotation ---
                if (set.GlobalRotationOverride.HasValue)
                {
                    finalRot = set.GlobalRotationOverride.Value;
                }
                else if (set.UseGlobalRotation)
                {
                    finalRot = child.Rotation;
                }
                else
                {
                    finalRot = child.Rotation + Rotation;
                }

                // --- Scale ---
                if (set.GlobalScaleOverride.HasValue)
                {
                    finalScale = set.GlobalScaleOverride.Value;
                }
                else if (set.UseGlobalScale)
                {
                    finalScale = child.Scale;
                }
                else
                {
                    finalScale = child.Scale * Scale;
                }

                // подменяем временно значения (локальные в коде остаются прежними после восстановления)
                child.X = finalX;
                child.Y = finalY;
                child.Scale = finalScale;
                child.Rotation = finalRot;

                // рендерим ребёнка (он сам проверит шейдерные униформы и отрисует всё корректно)
                child.Render(program, vbo, vao);

                // восстанавливаем
                child.X = origX;
                child.Y = origY;
                child.Scale = origScale;
                child.Rotation = origRot;
            }
        }

        // Простейшие вспомогательные методы для удобства
        public void SetChildUseGlobalRotation(Primitive child, bool useGlobal)
        {
            GetSettingsFor(child).UseGlobalRotation = useGlobal;
        }

        public void SetChildUseGlobalPosition(Primitive child, bool useGlobal)
        {
            GetSettingsFor(child).UseGlobalPosition = useGlobal;
        }

        public void SetChildUseGlobalScale(Primitive child, bool useGlobal)
        {
            GetSettingsFor(child).UseGlobalScale = useGlobal;
        }

        public void SetChildGlobalRotationOverride(Primitive child, float? radians)
        {
            GetSettingsFor(child).GlobalRotationOverride = radians;
        }

        public void SetChildGlobalPositionOverride(Primitive child, Vector2? pos)
        {
            GetSettingsFor(child).GlobalPositionOverride = pos;
        }

        public void SetChildGlobalScaleOverride(Primitive child, float? scale)
        {
            GetSettingsFor(child).GlobalScaleOverride = scale;
        }
    }

    // ------------------ PRIMITIVES ------------------

    public class Polygon : Primitive
    {
        public List<Vector2> Points { get; private set; } = [];

        // --- Dash parameters ---
        public bool DashEnabled { get; set; } = false;
        public float DashOn { get; set; } = 0.05f;
        public float DashOff { get; set; } = 0.03f;
        public float DashPhase { get; set; } = 0f;

        public Polygon(IEnumerable<Vector2>? points = null, bool closed = true, bool filled = false,
            Vector3 color = default)
            : base(0f, 0f, filled, color)
        {
            Closed = closed;
            if (points != null) Points.AddRange(points);
        }

        public void SetPoints(IEnumerable<Vector2> points)
        {
            Points.Clear();
            Points.AddRange(points);
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            return !DashEnabled ? [..Points] : BuildDashedPolygon();
        }

        private List<Vector2> BuildDashedPolygon()
        {
            var result = new List<Vector2>();
            int count = Points.Count;
            if (count < 2) return result;

            for (int i = 0; i < count; i++)
            {
                Vector2 start = Points[i];
                Vector2 end = Points[(i + 1) % count];
                if (!Closed && i == count - 1) break;

                var segment = BuildDashesForSegment(start, end, DashOn, DashOff, DashPhase);
                if (result.Count > 0 && segment.Count > 0 && result[^1] == segment[0])
                    segment.RemoveAt(0);

                result.AddRange(segment);
            }

            return result;
        }

        private static List<Vector2> BuildDashesForSegment(Vector2 a, Vector2 b, float dashOn, float dashOff,
            float phase)
        {
            var res = new List<Vector2>();
            var dir = b - a;
            float segLen = dir.Length;
            if (segLen < 1e-6f) return [a, b];

            var ndir = dir / segLen;
            float pos = -phase;
            bool draw = true;

            while (pos < segLen)
            {
                float len = draw ? dashOn : dashOff;
                if (pos + len > segLen) len = segLen - pos;

                if (len > 1e-6f && draw)
                {
                    var p0 = a + ndir * MathF.Max(pos, 0f);
                    var p1 = a + ndir * MathF.Min(pos + len, segLen);
                    res.Add(p0);
                    res.Add(p1);
                    res.Add(new Vector2(float.NaN, float.NaN)); // NaN разделитель
                }

                pos += len;
                draw = !draw;
            }

            if (res.Count > 0 && float.IsNaN(res[^1].X)) res.RemoveAt(res.Count - 1);

            return res;
        }

        public Primitive AnimateDash(bool enable, float targetDashOn = 0.05f, float targetDashOff = 0.03f,
            float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            ScheduleOrExecute(() =>
            {
                DashEnabled = true;
                if (enable)
                {
                    Animate(() => DashOn, v => DashOn = v, targetDashOn, duration, ease);
                    Animate(() => DashOff, v => DashOff = v, targetDashOff, duration, ease);
                }
                else
                {
                    Animate(() => DashOn, v => DashOn = v, 0f, duration, ease);
                    Animate(() => DashOff, v => DashOff = v, 0f, duration, ease);
                    Animate(_ => { DashEnabled = false; }, 0f, 0f, 0f);
                }
            });
            return this;
        }
    }

    public class Circle : Polygon
    {
        public Circle(float x = 0f, float y = 0f, float radius = 0.1f, bool filled = false,
            Vector3 color = default, int segments = 80)
            : base(GenerateCircleVerts(radius, segments), closed: true, filled, color)
        {
            X = x;
            Y = y;
        }

        private static List<Vector2> GenerateCircleVerts(float radius, int segments)
        {
            var verts = new List<Vector2>(segments);
            for (int i = 0; i < segments; i++)
            {
                float a = 2 * MathF.PI * i / segments;
                verts.Add(new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius));
            }

            return verts;
        }
    }

    public class Rectangle : Polygon
    {
        public Rectangle(float x = 0f, float y = 0f, float width = 0.2f, float height = 0.2f,
            bool filled = false, Vector3 color = default)
            : base(GenerateRectVerts(width, height), closed: true, filled, color)
        {
            X = x;
            Y = y;
        }

        private static List<Vector2> GenerateRectVerts(float w, float h)
            =>
            [
                new(-w / 2, -h / 2),
                new(w / 2, -h / 2),
                new(w / 2, h / 2),
                new(-w / 2, h / 2)
            ];
    }

    public class Line : Polygon
    {
        public Line(float x1 = 0f, float y1 = 0f, float x2 = 0.2f, float y2 = 0f,
            Vector3 color = default, float lineWidth = 1f)
            : base([Vector2.Zero, new Vector2(x2 - x1, y2 - y1)], closed: false, filled: false, color)
        {
            X = x1;
            Y = y1;
            LineWidth = lineWidth;
        }
    }

    public class Triangle : Polygon
    {
        private static readonly Vector2 DefaultA = new(-0.1f, -0.1f);
        private static readonly Vector2 DefaultB = new( 0.1f, -0.1f);
        private static readonly Vector2 DefaultC = new( 0.0f,  0.1f);
        public Triangle(
            float x = 0f,
            float y = 0f,
            Vector2 a = default,
            Vector2 b = default,
            Vector2 c = default,
            bool filled = true,
            Vector3 color = default)
            : base(GenerateVerts(a, b, c), closed: true, filled, color)
        {
            X = x;
            Y = y;
        }

        private static List<Vector2> GenerateVerts(Vector2 a, Vector2 b, Vector2 c)
        {
            if (a == Vector2.Zero && b == Vector2.Zero && c == Vector2.Zero)
            {
                return [DefaultA, DefaultB, DefaultC];
            }

            return
            [
                a == Vector2.Zero ? DefaultA : a,
                b == Vector2.Zero ? DefaultB : b,
                c == Vector2.Zero ? DefaultC : c
            ];
        }
    }

    public class Plot : Polygon
    {
        public Func<float, float> Func { get; }
        public float XMin { get; set; }
        public float XMax { get; set; }
        public int Resolution { get; set; }

        public Plot(
            Func<float, float> func,
            float xMin = -1f,
            float xMax = 1f,
            int resolution = 300,
            Vector3 color = default,
            bool dashed = false,
            bool closed = false,
            float dashOn = 0.05f,
            float dashOff = 0.03f)
            : base(GenerateVerts(func, xMin, xMax, resolution), closed: false, filled: false, color)
        {
            Func = func ?? throw new ArgumentNullException(nameof(func));
            XMin = xMin;
            XMax = xMax;
            Resolution = Math.Max(2, resolution);
            Closed = closed;

            if (dashed)
            {
                DashEnabled = true;
                DashOn = dashOn;
                DashOff = dashOff;
            }

        }

        private static List<Vector2> GenerateVerts(Func<float, float> f, float min, float max, int res)
        {
            var verts = new List<Vector2>(res + 1);
            for (int i = 0; i <= res; i++)
            {
                float t = i / (float)res;
                float x = MathHelper.Lerp(min, max, t);
                float y = f(x);
                verts.Add(new Vector2(x, y));
            }

            return verts;
        }

        // Пересчитываем график при изменении параметров (например, в анимации)
        public void UpdatePlot()
        {
            SetPoints(GenerateVerts(Func, XMin, XMax, Resolution));
        }

        // Удобные методы для анимации области и разрешения
        public Plot WithRange(float xMin, float xMax, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Animate(() => XMin, v =>
            {
                XMin = v;
                UpdatePlot();
            }, xMin, duration, ease);
            Animate(() => XMax, v =>
            {
                XMax = v;
                UpdatePlot();
            }, xMax, duration, ease);
            return this;
        }

        public Plot WithResolution(int resolution, float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Animate(() => Resolution, v =>
            {
                Resolution = (int)v;
                UpdatePlot();
            }, resolution, duration, ease);
            return this;
        }
    }


    // ------------------ TEXT (with per-char caching) ------------------
    public class Text : Primitive
    {
        public object? Font { get; set; }

        public enum HorizontalAlignment
        {
            Left,
            Center,
            Right
        }

        public enum VerticalAlignment
        {
            Top,
            Center,
            Bottom
        }

        private string _textContent;

        public float FontSize { get; set; }

        public float LetterPadding { get; set; }
        public float VerticalPadding { get; set; }

        public HorizontalAlignment Horizontal { get; set; }
        public VerticalAlignment Vertical { get; set; }

        public float Width { get; private set; }
        public float Height { get; private set; }

        public string TextContent => _textContent;

        private Func<string>? _dynamicTextFunc;
        private string? _lastRenderedText;

        private readonly SKTypeface _typeface;

        // caches
        private readonly Dictionary<int, CachedChar> _charCache = new();

        // NEW: mesh
        private TextMesh? _mesh;

        public Text(string text = "Empty text", float x = 0f, float y = 0f, float fontSize = 0.1f,
            float letterPadding = 0.05f,
            float verticalPadding = 0.1f, Vector3 color = default,
            HorizontalAlignment horizontal = HorizontalAlignment.Center,
            VerticalAlignment vertical = VerticalAlignment.Center, bool filled = false,
            object? font = null, Func<string>? dynamicTextFunc = null)
            : base(x, y, filled, color)
        {
            if (dynamicTextFunc != null)
            {
                _dynamicTextFunc = dynamicTextFunc;
                _textContent = _dynamicTextFunc();
            }
            else
            {
                _textContent = text ?? "Empty text";
            }

            FontSize = fontSize;
            LetterPadding = letterPadding;
            VerticalPadding = verticalPadding;
            Horizontal = horizontal;
            Vertical = vertical;
            Font = font ?? FontFamily.Arial;

            string fontKey = Font switch
            {
                FontFamily ff => FontManager.GetNameFromFamily(ff),
                string s => s,
                _ => "Arial"
            };

            _typeface = FontManager.GetTypeface(null, fontKey);

            RecalculateWidthHeight();
            _mesh = TextMesh.CreateFromText(_textContent, _typeface, FontSize, LetterPadding, VerticalPadding, Filled,
                Horizontal, Vertical);
        }

        private void RecalculateWidthHeight()
        {
            var lines = _textContent.Split(["\r\n", "\n"], StringSplitOptions.None);
            Width = 0f;
            foreach (var line in lines)
            {
                float lineWidth = 0f;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    lineWidth += CharMap.GetGlyphAdvance(c, FontSize, _typeface);
                    if (i < line.Length - 1)
                        lineWidth += LetterPadding * FontSize;
                }

                if (lineWidth > Width)
                    Width = lineWidth;
            }

            Height = lines.Length * FontSize +
                     (lines.Length > 0 ? (lines.Length - 1) * (VerticalPadding * FontSize) : 0f);
        }

        private class CachedChar
        {
            public char C;
            public float LastParentX;
            public float LastParentY;
            public float LastParentScale;
            public float LastParentRotation;
            public float LastOffsetX;
            public float LastOffsetY;
            public List<List<Vector3>> CachedContours = [];
        }

        // keep these helpers for compatibility (may be used elsewhere)
        private void RenderContoursWithCharCache(char c, int index, List<List<Vector2>> contours, float offsetX,
            float offsetY, int program, int vbo)
        {
            if (contours.Count == 0) return;

            int key = index;
            if (!_charCache.TryGetValue(key, out var cache))
            {
                cache = new CachedChar { C = c, CachedContours = [] };
                _charCache[key] = cache;
            }

            bool dirty =
                cache.C != c ||
                !Helpers.AlmostEqual(cache.LastParentX, X) ||
                !Helpers.AlmostEqual(cache.LastParentY, Y) ||
                !Helpers.AlmostEqual(cache.LastParentScale, Scale) ||
                !Helpers.AlmostEqual(cache.LastParentRotation, Rotation) ||
                !Helpers.AlmostEqual(cache.LastOffsetX, offsetX) ||
                !Helpers.AlmostEqual(cache.LastOffsetY, offsetY);

            if (dirty)
            {
                cache.CachedContours.Clear();
                float cos = MathF.Cos(Rotation) * Scale;
                float sin = MathF.Sin(Rotation) * Scale;

                foreach (var contour in contours)
                {
                    var transformed = new List<Vector3>(contour.Count);
                    foreach (var v in contour)
                        transformed.Add(new Vector3(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos, 0f));
                    cache.CachedContours.Add(transformed);
                }

                cache.C = c;
                cache.LastParentX = X;
                cache.LastParentY = Y;
                cache.LastParentScale = Scale;
                cache.LastParentRotation = Rotation;
                cache.LastOffsetX = offsetX;
                cache.LastOffsetY = offsetY;
            }

            foreach (var contour in cache.CachedContours)
            {
                if (contour.Count < 2) continue;
                var finalContours = contour.Select(v => new Vector3(v.X + X, v.Y + Y, v.Z)).ToList();
                // This call may be replaced by mesh rendering — kept for compatibility
                RenderVerts(finalContours, Filled, program, vbo, Color, LineWidth);
            }
        }

        public override void Render(int program, int vbo, int vao)
        {
            if (CustomBoundary != null || ShapeAnim != null)
            {
                base.Render(program, vbo, vao);
                return;
            }

            // If mesh exists, use it (fast path)
            if (_mesh != null)
            {
                _mesh.Render(program, X, Y, Scale, Rotation, Color, LineWidth);
                return;
            }

            // Fallback to original per-char rendering (if mesh is missing)
            var lines = _textContent.Replace("\r", "").Split('\n');

            float step = FontSize + VerticalPadding * FontSize;
            float line0YRelativeToCenter = Height / 2f - FontSize / 2f;

            float centerOffset = Vertical switch
            {
                VerticalAlignment.Top => Height / 2f,
                VerticalAlignment.Bottom => -Height / 2f,
                _ => 0f
            };

            int globalCharIndex = 0;

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                float cursorY = line0YRelativeToCenter - lineIdx * step - centerOffset;

                float lineWidth = 0f;
                for (int i = 0; i < line.Length; i++)
                    lineWidth += CharMap.GetGlyphAdvance(line[i], FontSize, _typeface) +
                                 (i < line.Length - 1 ? LetterPadding * FontSize : 0f);

                float offsetXLocal = Horizontal switch
                {
                    HorizontalAlignment.Left => 0f,
                    HorizontalAlignment.Right => -lineWidth,
                    _ => -lineWidth / 2f
                };

                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    var contours = CharMap.GetCharContours(c, offsetXLocal, cursorY, FontSize, _typeface);
                    RenderContoursWithCharCache(c, globalCharIndex, contours, offsetXLocal, cursorY, program, vbo);

                    float adv = CharMap.GetGlyphAdvance(c, FontSize, _typeface);
                    offsetXLocal += adv + (i < line.Length - 1 ? LetterPadding * FontSize : 0f);

                    globalCharIndex++;
                }
            }
        }

        public override void Update(float dt)
        {
            base.Update(dt);
            if (_dynamicTextFunc == null) return;

            string newText = _dynamicTextFunc();
            if (newText == _lastRenderedText) return;

            _textContent = newText;
            _lastRenderedText = newText;

            RecalculateWidthHeight();
            _mesh = TextMesh.CreateFromText(_textContent, _typeface, FontSize, LetterPadding, VerticalPadding, Filled,
                Horizontal, Vertical);
            _charCache.Clear();
        }

        public void SetDynamicText(Func<string> dynamicTextFunc)
        {
            _dynamicTextFunc = dynamicTextFunc;
            _textContent = _dynamicTextFunc();
            _lastRenderedText = _textContent;
            RecalculateWidthHeight();
            _mesh = TextMesh.CreateFromText(_textContent, _typeface, FontSize, LetterPadding, VerticalPadding, Filled,
                Horizontal, Vertical);
            _charCache.Clear();
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            var all2 = new List<Vector2>();
            var lines = _textContent.Replace("\r", "").Split('\n');

            float step = FontSize + VerticalPadding * FontSize;
            float line0YRelativeToCenter = Height / 2f - FontSize / 2f;

            float centerOffset = Vertical switch
            {
                VerticalAlignment.Top => Height / 2f,
                VerticalAlignment.Bottom => -Height / 2f,
                _ => 0f
            };

            for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
            {
                var line = lines[lineIdx];
                float cursorY = line0YRelativeToCenter - lineIdx * step - centerOffset;

                float lineWidth = 0f;
                for (int i = 0; i < line.Length; i++)
                    lineWidth += CharMap.GetGlyphAdvance(line[i], FontSize, _typeface) +
                                 (i < line.Length - 1 ? LetterPadding * FontSize : 0f);

                float offsetXLocal = Horizontal switch
                {
                    HorizontalAlignment.Left => 0f,
                    HorizontalAlignment.Right => -lineWidth,
                    _ => -lineWidth / 2f
                };

                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    var contours = CharMap.GetCharContours(c, offsetXLocal, cursorY, FontSize, _typeface);
                    foreach (var contour in contours)
                    {
                        all2.AddRange(contour);
                        all2.Add(new Vector2(float.NaN, float.NaN));
                    }

                    offsetXLocal += CharMap.GetGlyphAdvance(c, FontSize, _typeface) +
                                    (i < line.Length - 1 ? LetterPadding * FontSize : 0f);
                }
            }

            if (all2.Count > 0 && float.IsNaN(all2[^1].X)) all2.RemoveAt(all2.Count - 1);
            return all2;
        }
    }

}
