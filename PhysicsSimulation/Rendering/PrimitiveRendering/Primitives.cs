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
            X += Vx * dt; Y += Vy * dt;
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
            drawVerts = PrepareDrawVerts(raw, Filled);
        }
        else
        {
            // Старый путь — применяем трансформы на CPU
            var transformed = TransformVerts(seg);
            drawVerts = PrepareDrawVerts(transformed, Filled);
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

        protected static List<Vector3> PrepareDrawVerts(List<Vector3> verts, bool filled)
        {
            if (verts.Count < 2) return [];

            // Ensure the contour is closed
            var closedVerts = new List<Vector3>(verts);
            if (verts[0] != verts[^1])
                closedVerts.Add(verts[0]);

            if (filled)
            {
                if (closedVerts.Count < 3) return [];
                var centroid = closedVerts.Aggregate(Vector3.Zero, (s, v) => s + v) / closedVerts.Count;
                var fan = new List<Vector3>(closedVerts.Count + 1) { centroid };
                fan.AddRange(closedVerts);
                return fan;
            }
            else
            {
                return closedVerts;
            }
        }

        // reuse VAO per VBO
        private static readonly Dictionary<int, int> VboToVao = new();

        protected static void RenderVerts(List<Vector3> verts, bool filled, int program, int vbo, Vector3 color, float lineWidth)
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
        public Primitive Animate(Action<float> setter, float start, float target, float duration = 1f, EaseType ease = EaseType.EaseInOut)
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
        public Primitive Animate(Func<float> startGetter, Action<float> setter, float target, float duration = 1f, EaseType ease = EaseType.EaseInOut)
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

        public Primitive SetFilled(bool filled, float duration = 0f) { Filled = filled; return this; }

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

                // Анимируем трансформ/цветы исходного объекта к параметрам target (одновременно)
                AnimateColor(target.Color, duration, ease);
                MoveTo(target.X, target.Y, duration, ease);
                Resize(target.Scale == 0f ? 1f : target.Scale, duration, ease);
                RotateTo(target.Rotation, duration, ease);
                SetLineWidth(target.LineWidth, duration, ease);
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

    // ------------------ PRIMITIVES ------------------
    public class Circle : Primitive
    {
        public float Radius { get; set; }
        public int Segments { get; set; }

        public Circle(
            float x = 0f,
            float y = 0f,
            float radius = 0.1f,
            bool filled = false,
            Vector3 color = default,
            int segments = 80)
            : base(x, y, filled, color)
        {
            Radius = radius;
            Segments = segments;
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            var list = new List<Vector2>(Segments);
            for (int i = 0; i < Segments; i++)
            {
                float a = 2 * MathF.PI * i / Segments;
                list.Add(new Vector2(Radius * MathF.Cos(a), Radius * MathF.Sin(a)));
            }
            return list;
        }
    }

    public class Rectangle(
        float x = 0f,
        float y = 0f,
        float width = 0.2f,
        float height = 0.2f,
        bool filled = false,
        Vector3 color = default)
        : Primitive(x, y, filled, color)
    {
        public float Width { get; set; } = width;
        public float Height { get; set; } = height;

        public override List<Vector2> GetBoundaryVerts()
        {
            float hw = Width / 2, hh = Height / 2;
            return
            [
                new Vector2(-hw, -hh),
                new Vector2(hw, -hh),
                new Vector2(hw, hh),
                new Vector2(-hw, hh)
            ];
        }
    }

    // ------------------ TEXT (with per-char caching) ------------------
    public class Text : Primitive
    {
        public object? Font { get; set; }
        public enum HorizontalAlignment { Left, Center, Right }
        public enum VerticalAlignment { Top, Center, Bottom }

        private string _textContent;

        public float FontSize { get; set; }

        public float LetterPadding { get; set; }
        public float VerticalPadding { get; set; }

        public HorizontalAlignment Horizontal { get; set; }
        public VerticalAlignment Vertical { get; set; }

        public float Width { get; private set; }
        public float Height { get; private set; }

        private readonly SKTypeface _typeface;

        // caches
        private readonly Dictionary<int, CachedChar> _charCache = new();

        // NEW: mesh
        private TextMesh? _mesh;

        public Text(string text = "Empty text", float x = 0f, float y = 0f, float fontSize = 0.1f, float letterPadding = 0.05f,
            float verticalPadding = 0.1f, Vector3 color = default,
            HorizontalAlignment horizontal = HorizontalAlignment.Center,
            VerticalAlignment vertical = VerticalAlignment.Center, bool filled = false,
            object? font = null)
            : base(x, y, filled, color)
        {
            _textContent = text;

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

            // compute sizes and build mesh now that _typeface is available
            RecalculateWidthHeight();
            _mesh = TextMesh.CreateFromText(_textContent, _typeface, FontSize, LetterPadding, VerticalPadding, Filled, Horizontal, Vertical);
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

            Height = lines.Length * FontSize + (lines.Length > 0 ? (lines.Length - 1) * (VerticalPadding * FontSize) : 0f);
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

                    offsetXLocal += CharMap.GetGlyphAdvance(c, FontSize, _typeface) + (i < line.Length - 1 ? LetterPadding * FontSize : 0f);
                }
            }

            if (all2.Count > 0 && float.IsNaN(all2[^1].X)) all2.RemoveAt(all2.Count - 1);
            return all2;
        }
    }
}
