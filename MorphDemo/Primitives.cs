// Optimized PhysicsSimulation framework (single-file reference implementation)
// - Uses delegate-based animations (no reflection)
// - Cached contour glyphs (CharMap) and per-char transform caching
// - Cached VAO per VBO, minimal allocations
// NOTE: Replace CharMap.GetCharContours with a proper font-to-contours implementation

using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace PhysicsSimulation
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
    public abstract class Primitive : SceneObject
    {
        // transform state
        public float X { get; set; }
        public float Y { get; set; }
        public float Scale { get; set; } = 1f;
        public float Rotation { get; set; }
        public Vector3 Color { get; set; } = Vector3.One;
        public float LineWidth { get; set; } = 1f;
        public bool Filled { get; set; }

        // simple physics
        public float Vx { get; set; }
        public float Vy { get; set; }

        // animations
        protected readonly List<PropertyAnimation> Animations = new();
        protected ShapeAnimation? ShapeAnim { get; set; }

        // custom boundaries and morphing
        protected List<Vector2>? CustomBoundary { get; set; }
        protected List<Vector2>? BoundaryVerts { get; set; }

        protected Primitive(float x = 0f, float y = 0f, bool filled = false, Vector3 color = default,
            float vx = 0f, float vy = 0f)
        {
            X = x; Y = y; Filled = filled; Color = color == default ? Vector3.One : color;
            Vx = vx; Vy = vy; Scale = 1f;
        }

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

            BoundaryVerts = ShapeAnim.Start.Zip(ShapeAnim.Target, (s, targ) => Vector2.Lerp(s, targ, t)).ToList();

            if (tRaw >= 1f)
            {
                CustomBoundary = BoundaryVerts;
                Filled = ShapeAnim.TargetFilled;
                ShapeAnim = null;
            }
        }

        public override void Render(int program, int vbo)
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
                        current = new List<Vector2>();
                    }
                    // пропускаем разделитель
                }
                else
                {
                    current.Add(p);
                }
            }
            if (current.Count > 0) segments.Add(current);

            // Рендерим каждый сегмент отдельно (это убирает "мосты" между сегментами)
            foreach (var seg in segments)
            {
                if (seg == null || seg.Count == 0) continue;
                var transformed = TransformVerts(seg);
                var draw = PrepareDrawVerts(transformed, Filled);
                RenderVerts(draw, Filled, program, vbo, Color, LineWidth);
            }
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
            if (verts.Count < 2) return new List<Vector3>();

            // Ensure the contour is closed
            var closedVerts = new List<Vector3>(verts);
            if (verts[0] != verts[^1])
                closedVerts.Add(verts[0]);

            if (filled)
            {
                if (closedVerts.Count < 3) return new List<Vector3>();
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
            if (verts == null || verts.Count == 0) return;

            GL.UseProgram(program);
            int colorLoc = GL.GetUniformLocation(program, "color");
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
        public Primitive Animate(Action<float> setter, float start, float target, float duration = 1f, EaseType ease = EaseType.Linear)
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

        public Primitive AnimateColor(Vector3 target, float duration = 1f, EaseType ease = EaseType.Linear)
        {
            Vector3 start = Color;
            ScheduleOrExecute(() =>
            {
                Animations.Add(new PropertyAnimation
                {
                    Apply = t => Color = Vector3.Lerp(start, target, t),
                    Duration = duration,
                    Ease = ease
                });
            });
            return this;
        }

        public Primitive MoveTo(float x, float y, float duration = 1f, EaseType ease = EaseType.Linear)
        {
            Animate(v => X = v, X, x, duration, ease);
            Animate(v => Y = v, Y, y, duration, ease);
            return this;
        }

        public Primitive Resize(float targetScale, float duration = 1f, EaseType ease = EaseType.Linear) => Animate(v => Scale = v, Scale, targetScale, duration, ease);
        public Primitive RotateTo(float targetRotation, float duration = 1f, EaseType ease = EaseType.Linear) => Animate(v => Rotation = v, Rotation, targetRotation, duration, ease);
        public Primitive SetLineWidth(float target, float duration = 1f, EaseType ease = EaseType.Linear) => Animate(v => LineWidth = v, LineWidth, target, duration, ease);

        public Primitive SetFilled(bool filled, float _duration = 0f) { Filled = filled; return this; }

        public Primitive Draw(float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Scale = 0f;
            Animate(v => Scale = v, 0f, 1f, duration, ease);
            return this;
        }

        public Primitive MorphTo(Primitive target, float duration = 2f, EaseType ease = EaseType.EaseInOut)
        {
            var startVerts = CustomBoundary ?? BoundaryVerts ?? GetBoundaryVerts();
            var targetVerts = target.GetBoundaryVerts();

            if (startVerts.Count != targetVerts.Count)
            {
                if (startVerts.Count < targetVerts.Count)
                    startVerts = Helpers.PadWithDuplicates(startVerts, targetVerts.Count);
                else
                    targetVerts = Helpers.PadWithDuplicates(targetVerts, startVerts.Count);
            }

            ShapeAnim = new ShapeAnimation
            {
                Start = startVerts,
                Target = targetVerts,
                Duration = duration,
                Ease = ease,
                TargetFilled = target.Filled
            };

            BoundaryVerts = startVerts;

            AnimateColor(target.Color, duration, ease);
            MoveTo(target.X, target.Y, duration, ease);
            Resize(target.Scale == 0f ? 1f : target.Scale, duration, ease);
            RotateTo(target.Rotation, duration, ease);
            SetLineWidth(target.LineWidth, duration, ease);
            return this;
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
            public List<Vector2> Start = new();
            public List<Vector2> Target = new();
            public float Duration;
            public float Elapsed;
            public EaseType Ease;
            public bool TargetFilled;
        }
    }

    // ------------------ PRIMITIVES ------------------
    public class Circle : Primitive
    {
        public float Radius { get; set; }
        public Circle(float x = 0f, float y = 0f, float radius = 0.1f, bool filled = false, Vector3 color = default)
            : base(x, y, filled, color) { Radius = radius; }

        public override List<Vector2> GetBoundaryVerts()
        {
            const int steps = 80;
            var list = new List<Vector2>(steps);
            for (int i = 0; i < steps; i++)
            {
                float a = 2 * MathF.PI * i / steps;
                list.Add(new Vector2(Radius * MathF.Cos(a), Radius * MathF.Sin(a)));
            }
            return list;
        }
    }

    public class Rectangle : Primitive
    {
        public float Width { get; set; }
        public float Height { get; set; }
        public Rectangle(float x = 0f, float y = 0f, float width = 0.2f, float height = 0.2f, bool filled = false, Vector3 color = default)
            : base(x, y, filled, color) { Width = width; Height = height; }

        public override List<Vector2> GetBoundaryVerts()
        {
            float hw = Width / 2, hh = Height / 2;
            return new List<Vector2>
            {
                new(-hw, -hh),
                new(hw, -hh),
                new(hw, hh),
                new(-hw, hh)
            };
        }
    }

    // ------------------ TEXT (with per-char caching) ------------------
    public class Text : Primitive
    {
        public string TextContent { get; set; }
        public float FontSize { get; set; }

        /// <summary>Пиксельно/мировой padding между буквами (абсолютный). По умолчанию 0.01</summary>
        public float LetterPadding { get; set; } = 0.01f;

        public float Width { get; private set; }
        public float Height { get; private set; }

        // cache raw contours per char+size to avoid repeated CharMap calls
        private readonly Dictionary<(char ch, float size), List<List<Vector2>>> _contourCache = new();

        // cache of per-char transformed vertices to avoid recomputing when not changed
        private readonly Dictionary<int, CachedChar> _charCache = new();

        public Text(string text, float x = 0f, float y = 0f, float fontSize = 0.1f, float letterPadding = 0.01f,
            Vector3 color = default)
            : base(x, y, false, color)
        {
            TextContent = text;
            FontSize = fontSize;
            LetterPadding = letterPadding;
            Height = fontSize;
            RecalculateWidth();
        }

        private void RecalculateWidth()
        {
            float total = 0f;
            for (int i = 0; i < TextContent.Length; i++)
            {
                char c = TextContent[i];
                float adv = CharMap.GetGlyphAdvance(c, FontSize);
                total += adv;
                if (i < TextContent.Length - 1)
                    total += LetterPadding;
            }

            Width = total;
        }

        private class CachedChar
        {
            public char C;
            public float LastParentX;
            public float LastParentY;
            public float LastParentScale;
            public float LastParentRotation;
            public Vector3[] CachedVerts = Array.Empty<Vector3>();
        }

        public override void Render(int program, int vbo)
        {
            if (CustomBoundary != null || ShapeAnim != null)
            {
                base.Render(program, vbo);
                return;
            }

            // отрисовываем символы слева-направо, выравнено по центру (Width/2)
            float offsetX = -Width / 2f;
            for (int i = 0; i < TextContent.Length; i++)
            {
                char c = TextContent[i];
                var contours = GetCharContours(c, offsetX);
                // render each contour using per-char cache for transformed vertices
                RenderContoursWithCharCache(c, i, contours, program, vbo);

                // advance курсора: ширина глифа + padding (padding не добавляем после последнего символа)
                float adv = CharMap.GetGlyphAdvance(c, FontSize);
                offsetX += adv + (i < TextContent.Length - 1 ? LetterPadding : 0f);
            }
        }

        private void RenderContoursWithCharCache(char c, int index, List<List<Vector2>> contours, int program, int vbo)
        {
            if (contours == null || contours.Count == 0)
                return;

            // key is index to separate identical characters in different positions
            int key = index;
            if (!_charCache.TryGetValue(key, out var cache))
            {
                cache = new CachedChar { C = c };
                _charCache[key] = cache;
            }

            bool dirty = cache.C != c ||
                         cache.LastParentX != X ||
                         cache.LastParentY != Y ||
                         cache.LastParentScale != Scale ||
                         cache.LastParentRotation != Rotation;

            if (dirty)
            {
                // rebuild cached vertices for each contour separately
                var allTransformed = new List<Vector3>();
                float cos = MathF.Cos(Rotation) * Scale;
                float sin = MathF.Sin(Rotation) * Scale;

                foreach (var contour in contours)
                {
                    foreach (var v in contour)
                    {
                        allTransformed.Add(new Vector3(v.X * cos - v.Y * sin + X, v.X * sin + v.Y * cos + Y, 0f));
                    }
                }

                cache.CachedVerts = allTransformed.ToArray();
                cache.C = c;
                cache.LastParentX = X;
                cache.LastParentY = Y;
                cache.LastParentScale = Scale;
                cache.LastParentRotation = Rotation;
            }

            // Render each contour separately
            float cosT = MathF.Cos(Rotation) * Scale;
            float sinT = MathF.Sin(Rotation) * Scale;

            foreach (var contour in contours)
            {
                if (contour.Count < 2)
                    continue;

                var transformed = contour
                    .Select(v => new Vector3(v.X * cosT - v.Y * sinT + X, v.X * sinT + v.Y * cosT + Y, 0f))
                    .ToList();

                var drawVerts = PrepareDrawVerts(transformed, Filled);
                RenderVerts(drawVerts, Filled, program, vbo, Color, LineWidth);
            }
        }

        private void RenderContours(List<List<Vector2>> contours, int program, int vbo)
        {
            foreach (var contour in contours)
            {
                if (contour.Count < 2) continue;
                var transformed = TransformVerts(contour);
                var drawVerts = PrepareDrawVerts(transformed, Filled);
                RenderVerts(drawVerts, Filled, program, vbo, Color, LineWidth);
            }
        }

        // теперь кэш учитывает размер шрифта
        public List<List<Vector2>> GetCharContours(char c, float offsetX)
        {
            var key = (c, FontSize);
            if (!_contourCache.TryGetValue(key, out var contours))
            {
                contours = CharMap.GetCharContours(c, 0f, FontSize);
                _contourCache[key] = contours;
            }

            // apply offset (return deep copy with applied offset)
            return contours.Select(contour => contour.Select(v => new Vector2(v.X + offsetX, v.Y)).ToList()).ToList();
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            var all = new List<Vector2>();

            float cursorX = 0;
            for (int i = 0; i < TextContent.Length; i++)
            {
                char c = TextContent[i];
                var charContours = CharMap.GetCharContours(c, cursorX, FontSize);

                foreach (var contour in charContours)
                {
                    all.AddRange(contour);
                    // разделитель между контурами (чтобы не было мостов между внутренними и внешними)
                    all.Add(new Vector2(float.NaN, float.NaN));
                }

                float adv = CharMap.GetGlyphAdvance(c, FontSize);
                cursorX += adv + (i < TextContent.Length - 1 ? LetterPadding : 0f);
            }

            return all;
        }

        // CharPrimitive nested (still useful if user wants per-char primitives)
        public class CharPrimitive : Primitive
        {
            public char Char { get; set; }
            private readonly Text Parent;

            public CharPrimitive(char c, Text parent, float x = 0f, float y = 0f, Vector3 color = default) : base(x, y,
                false, color)
            {
                Char = c;
                Parent = parent;
            }

            public override List<Vector2> GetBoundaryVerts()
            {
                var contours = Parent.GetCharContours(Char, X);
                return contours.SelectMany(c => c).ToList();
            }

            public override void Render(int program, int vbo)
            {
                var contours = Parent.GetCharContours(Char, X);
                Parent.RenderContours(contours, program, vbo);
            }

            public void SetShapeAnimation(List<Vector2> startVerts, List<Vector2> targetVerts, float duration,
                EaseType ease, bool targetFilled)
            {
                ShapeAnim = new ShapeAnimation
                {
                    Start = startVerts,
                    Target = targetVerts,
                    Duration = duration,
                    Ease = ease,
                    TargetFilled = targetFilled
                };
            }

            public void SetBoundaryVerts(List<Vector2> verts) => BoundaryVerts = verts;
        }

        public class GroupPrimitive : Primitive
        {
            private readonly List<Vector2> _initialVerts;

            public GroupPrimitive(List<Vector2> verts, float x = 0f, float y = 0f, Vector3 color = default,
                bool filled = false) : base(x, y, filled, color)
            {
                _initialVerts = verts;
                CustomBoundary = verts;
            }

            public override List<Vector2> GetBoundaryVerts() => CustomBoundary ?? _initialVerts;
        }

        public class TextSlice
        {
            private readonly Text _parent;
            public List<CharPrimitive> Chars { get; private set; }

            public TextSlice(Text parent, List<CharPrimitive>? chars = null)
            {
                _parent = parent;
                Chars = chars ?? new List<CharPrimitive>();
            }

            public void Morph(Primitive target, float duration = 2f, EaseType ease = EaseType.EaseInOut)
            {
                if (Chars.Count == 0 || target == null) return;

                // flatten vertices from chars into one list
                var startVerts = Chars.SelectMany(c => c.GetBoundaryVerts()).ToList();
                var gp = new GroupPrimitive(startVerts, _parent.X, _parent.Y, _parent.Color, _parent.Filled);
                Scene.CurrentScene?.Add(gp);

                foreach (var c in Chars)
                    c.Animate(v => c.Scale = v, c.Scale, 0f, 0f, ease); // quickly hide chars

                gp.MorphTo(target, duration, ease);
            }

            public TextSlice Move(float dx, float dy, float duration = 1f, EaseType ease = EaseType.Linear)
            {
                foreach (var c in Chars) c.MoveTo(c.X + dx, c.Y + dy, duration, ease);
                return this;
            }

            public TextSlice Resize(float targetScale, float duration = 1f, EaseType ease = EaseType.Linear)
            {
                foreach (var c in Chars) c.Animate(v => c.Scale = v, c.Scale, targetScale, duration, ease);
                return this;
            }

            public TextSlice Rotate(float targetRotation, float duration = 1f, EaseType ease = EaseType.Linear)
            {
                foreach (var c in Chars) c.Animate(v => c.Rotation = v, c.Rotation, targetRotation, duration, ease);
                return this;
            }

            public TextSlice AnimateColor(Vector3 target, float duration = 1f, EaseType ease = EaseType.Linear)
            {
                foreach (var c in Chars) c.AnimateColor(target, duration, ease);
                return this;
            }

            public TextSlice SetFilled(bool filled, float _duration = 0f)
            {
                foreach (var c in Chars) c.SetFilled(filled, _duration);
                return this;
            }
        }

        // helper: get a TextSlice referencing chars by range
        public TextSlice GetSlice(int startIndex, int length)
        {
            if (startIndex < 0 || startIndex >= TextContent.Length || startIndex + length > TextContent.Length)
                return new TextSlice(this);

            var chars = new List<CharPrimitive>(length);
            // Recompute starting offset for character i
            float baseOffset = -Width / 2f;
            for (int i = 0; i < startIndex; i++)
            {
                baseOffset += CharMap.GetGlyphAdvance(TextContent[i], FontSize) + LetterPadding;
            }

            for (int i = startIndex; i < startIndex + length; i++)
            {
                char c = TextContent[i];
                float offsetX = baseOffset;
                chars.Add(new CharPrimitive(c, this, offsetX, 0f, Color));
                baseOffset += CharMap.GetGlyphAdvance(c, FontSize) + LetterPadding;
            }

            return new TextSlice(this, chars);
        }
    }

    // ------------------ EXTENSIONS ------------------
    public static class PrimitiveExtensions
    {
        public static T Do<T>(this T p, Action<T> action) where T : Primitive { action(p); return p; }

        // convenience color animation for primitives
        public static Primitive AnimateColor(this Primitive p, Vector3 target, float duration = 1f, EaseType ease = EaseType.Linear)
        {
            p.Animate(t => p.Color = Vector3.Lerp(p.Color, target, t), 0f, 1f, duration, ease);
            return p;
        }
    }
}
