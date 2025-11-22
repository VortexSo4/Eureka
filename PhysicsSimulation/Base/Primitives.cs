// Optimized PhysicsSimulation framework (single-file reference implementation)
// - Uses delegate-based animations (no reflection)
// - Cached contour glyphs (CharMap) and per-char transform caching
// - Cached VAO per VBO, minimal allocations
// NOTE: Replace CharMap.GetCharContours with a proper font-to-contours implementation

using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SkiaSharp;

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
        
        public bool Visible { get; set; } = true;

        // simple physics
        public float Vx { get; set; }
        public float Vy { get; set; }

        // animations
        protected readonly List<PropertyAnimation> Animations = new();
        protected ShapeAnimation? ShapeAnim { get; set; }
        
        protected GpuMorph? GpuMorphInstance { get; set; }
        protected float MorphElapsed { get; set; }
        protected float MorphDuration { get; set; }
        protected EaseType MorphEase { get; set; } = EaseType.Linear;

        protected List<Vector2>? FinalMorphVerts { get; set; }
        
        protected List<Vector2>? _finalBoundaryAfterMorph;
        protected bool _finalFilledAfterMorph;

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
            if (GpuMorphInstance != null)
            {
                MorphElapsed += dt;
                float t = Math.Min(1f, MorphElapsed / MorphDuration);
                float easedT = Easing.Ease(MorphEase, t);

                GpuMorphInstance.Update(easedT);
            }
            
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
                (float.IsNaN(s.X) || float.IsNaN(s.Y)) ? new Vector2(float.NaN, float.NaN) : Vector2.Lerp(s, targ, t)
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
        
        private List<Vector2> NormalizeVerts(List<Vector2> verts)
        {
            if (verts == null || verts.Count == 0) return new List<Vector2>();

            var bounds = GetBounds(verts);
            var result = new List<Vector2>(verts.Count);

            foreach (var v in verts)
            {
                if (float.IsNaN(v.X) || float.IsNaN(v.Y))
                    result.Add(new Vector2(float.NaN, float.NaN));
                else
                    result.Add(v - bounds.Center);
            }
            return result;
        }

        public override void Render(int program, int vbo, int vao)
        {
            if (!Visible) return;

            // === GPU MORPHING: самый быстрый путь ===
            if (GpuMorphInstance != null)
            {
                GL.UseProgram(program);

                // Цвет
                int colorLoc = GL.GetUniformLocation(program, "u_color");
                if (colorLoc >= 0) GL.Uniform3(colorLoc, Color);

                // Трансформация
                int transLoc = GL.GetUniformLocation(program, "u_translate");
                int cosLoc = GL.GetUniformLocation(program, "u_cos");
                int sinLoc = GL.GetUniformLocation(program, "u_sin");
                int scaleLoc = GL.GetUniformLocation(program, "u_scale");

                if (transLoc >= 0) GL.Uniform2(transLoc, X, Y);
                if (cosLoc >= 0) GL.Uniform1(cosLoc, MathF.Cos(Rotation));
                if (sinLoc >= 0) GL.Uniform1(sinLoc, MathF.Sin(Rotation));
                if (scaleLoc >= 0) GL.Uniform1(scaleLoc, Scale);

                // Рендерим с GPU-буфера
                GL.BindVertexArray(vao);
                GpuMorphInstance.BindOutputAsArrayBuffer();
                GL.EnableVertexAttribArray(0);

                if (!Filled) GL.LineWidth(LineWidth);

                GL.DrawArrays(PrimitiveType.LineStrip, 0, GpuMorphInstance.VertCount);

                GL.BindVertexArray(0);
                GL.UseProgram(0);
                return;
            }

            // === CPU-путь (ShapeAnim или обычные вершины) ===
            var source = ShapeAnim != null ? BoundaryVerts : CustomBoundary ?? GetBoundaryVerts();
            if (source == null || source.Count == 0) return;

            // Разбиваем по NaN-разделителям
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
                }
                else current.Add(p);
            }

            if (current.Count > 0) segments.Add(current);

            GL.UseProgram(program);

            // === Трансформация (GPU или CPU) ===
            int locTranslate = GL.GetUniformLocation(program, "u_translate");
            int locCos = GL.GetUniformLocation(program, "u_cos");
            int locSin = GL.GetUniformLocation(program, "u_sin");
            int locScale = GL.GetUniformLocation(program, "u_scale");
            bool useGpuTransform = locTranslate >= 0 && locCos >= 0 && locSin >= 0 && locScale >= 0;

            if (useGpuTransform)
            {
                GL.Uniform2(locTranslate, X, Y);
                float c = MathF.Cos(Rotation);
                float s = MathF.Sin(Rotation);
                GL.Uniform1(locCos, c);
                GL.Uniform1(locSin, s);
                GL.Uniform1(locScale, Scale);
            }

            // === Цвет (один раз — здесь, без конфликта) ===
            int colorUniform = GL.GetUniformLocation(program, "color");
            if (colorUniform >= 0) GL.Uniform3(colorUniform, Color);

            // === Рендерим каждый сегмент ===
            foreach (var seg in segments)
            {
                if (seg.Count < 2) continue;

                List<Vector3> drawVerts = useGpuTransform
                    ? seg.Select(v => new Vector3(v.X, v.Y, 0f)).ToList()
                    : TransformVerts(seg);

                var finalVerts = PrepareDrawVerts(drawVerts, Filled);
                RenderVerts(finalVerts, Filled, program, vbo, Color, LineWidth);
            }

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

        public Primitive SetFilled(bool filled, float _duration = 0f) { Filled = filled; return this; }

        public Primitive Draw(float duration = 1f, EaseType ease = EaseType.EaseInOut)
        {
            Scale = 0f;
            Animate(v => Scale = v, 0f, 1f, duration, ease);
            return this;
        }

        public Primitive MorphTo(Primitive target, float duration = 2f, EaseType ease = EaseType.EaseInOut,
            bool hideTargetDuringMorph = false)
        {
            // Подготовка вершин (как у тебя сейчас)
            var startVerts = NormalizeVerts(GetBoundaryVerts());
            var targetVertsUnpadded = NormalizeVerts(target.GetBoundaryVerts());
            var maxLen = Math.Max(startVerts.Count, targetVertsUnpadded.Count);
            var paddedStart = Helpers.PadWithDuplicates(startVerts, maxLen);
            var paddedTarget = Helpers.PadWithDuplicates(targetVertsUnpadded, maxLen);

            ScheduleOrExecute(() =>
            {
                // Запускаем GPU-морфинг
                GpuMorphInstance = new GpuMorph(paddedStart, paddedTarget, ease);
                MorphDuration = duration;
                MorphElapsed = 0f;
                MorphEase = ease;

                // Анимируем свойства
                AnimateColor(target.Color, duration, ease);
                MoveTo(target.X, target.Y, duration, ease);
                Resize(target.Scale <= 0f ? 1f : target.Scale, duration, ease);
                RotateTo(MathHelper.RadiansToDegrees(target.Rotation), duration, ease);
                SetLineWidth(target.LineWidth, duration, ease);

                if (hideTargetDuringMorph)
                    target.Visible = false;

                // КЛЮЧЕВОЙ МОМЕНТ: после морфинга — ПОЛНОСТЬЮ ЗАМЕНЯЕМ СЕБЯ НА ЦЕЛЬ
                Scene.CurrentScene?.Wait(duration).Schedule(() =>
                {
                    // 1. Удаляем старый объект (себя)
                    Scene.CurrentScene?.Objects.Remove(this);

                    // 2. Копируем все свойства из target
                    target.X = this.X;
                    target.Y = this.Y;
                    target.Scale = this.Scale;
                    target.Rotation = this.Rotation;
                    target.Color = this.Color;
                    target.LineWidth = this.LineWidth;
                    target.Filled = this.Filled;
                    target.Visible = true;

                    // 3. Добавляем target вместо себя
                    Scene.CurrentScene?.Add(target);

                    // 4. Убиваем GPU-морфинг
                    GpuMorphInstance?.Dispose();
                    GpuMorphInstance = null;
                });
            });

            return this;
        }

        private (Vector2 Min, Vector2 Max, Vector2 Center) GetBounds(List<Vector2> verts)
        {
            if (verts == null || verts.Count == 0)
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

        private List<Vector2> DenormalizeVerts(List<Vector2> verts, Vector2 center)
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
                    result.Add(v + center);
                }
            }
            return result;
        }

        public virtual List<Vector2> GetBoundaryVerts()
        {
            if (_finalBoundaryAfterMorph != null)
                return _finalBoundaryAfterMorph;

            if (CustomBoundary != null)
                return CustomBoundary;

            return GetBoundaryVertsOverride();
        }

        protected abstract List<Vector2> GetBoundaryVertsOverride();

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
        
        public Circle(float x = 0f, float y = 0f, float radius = 0.1f, bool filled = false, Vector3 color = default, int segments = 80)
            : base(x, y, filled, color) { Radius = radius; Segments = segments; }

        protected override List<Vector2> GetBoundaryVertsOverride()
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

    public class Rectangle : Primitive
    {
        public float Width { get; set; }
        public float Height { get; set; }
        public Rectangle(float x = 0f, float y = 0f, float width = 0.2f, float height = 0.2f, bool filled = false, Vector3 color = default)
            : base(x, y, filled, color) { Width = width; Height = height; }

        protected override List<Vector2> GetBoundaryVertsOverride()
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
        public object? Font { get; set; } = FontFamily.TimesNewRoman;
        public string? FontName { get; set; } = null;
        public enum HorizontalAlignment { Left, Center, Right }
        public enum VerticalAlignment { Top, Middle, Bottom }

        private string _textContent;
        public string TextContent
        {
            get => _textContent;
            set
            {
                _textContent = value ?? string.Empty;
                RecalculateWidthHeight();
                RebuildMeshIfReady();
            }
        }

        public float FontSize { get; set; }

        public float LetterPadding { get; set; }
        public float VerticalPadding { get; set; }

        public HorizontalAlignment Horizontal { get; set; }
        public VerticalAlignment Vertical { get; set; }

        public float Width { get; private set; }
        public float Height { get; private set; }

        private readonly SKTypeface _typeface;

        // caches
        private readonly Dictionary<(char ch, float size, string fontKey), List<List<Vector2>>> _contourCache = new();
        private readonly Dictionary<int, CachedChar> _charCache = new();

        // NEW: mesh
        private TextMesh? _mesh;
        public bool HasMeshForDebug => _mesh != null;
        
        // ===== DEBUG ACCESSORS =====

// Возвращает true, если mesh создан
        public bool DebugHasMesh => _mesh != null;

// VBO mesh-а, если есть
        public int DebugVBO => _mesh?.Vbo ?? -1;

// Сколько наборов линий в mesh
        public int DebugRangeCount => _mesh?.Ranges.Count ?? 0;

// Общее число вершин в mesh
        public int DebugVertexCount => _mesh?.VertexCount ?? 0;

// Примерная рамка (bounding box) текста на локальных координатах
        public (float MinX, float MinY, float MaxX, float MaxY) DebugBounds =>
            (-Width/2f, -Height/2f, Width/2f, Height/2f);

        public Text(string text, float x = 0f, float y = 0f, float fontSize = 0.1f, float letterPadding = 0.05f,
            float verticalPadding = 0.1f, Vector3 color = default,
            HorizontalAlignment horizontal = HorizontalAlignment.Center,
            VerticalAlignment vertical = VerticalAlignment.Middle, bool filled = false,
            object? font = null)
            : base(x, y, filled, color)
        {
            // set backing text without triggering mesh until _typeface is ready
            _textContent = text ?? string.Empty;

            FontSize = fontSize;
            LetterPadding = letterPadding;
            VerticalPadding = verticalPadding;
            Horizontal = horizontal;
            Vertical = vertical;
            Font = font ?? FontFamily.Arial;

            string fontKey;
            if (Font is FontFamily ff)
                fontKey = FontManager.GetNameFromFamily(ff);
            else if (Font is string s)
                fontKey = s;
            else
                fontKey = "Arial";

            _typeface = FontManager.GetTypeface(null, fontKey);

            // compute sizes and build mesh now that _typeface is available
            RecalculateWidthHeight();
            _mesh = TextMesh.CreateFromText(_textContent, _typeface, FontSize, LetterPadding, VerticalPadding, Filled, Horizontal, Vertical);
        }

        private void RecalculateWidthHeight()
        {
            var lines = _textContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
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
            public List<List<Vector3>> CachedContours = new List<List<Vector3>>();
        }

        private void RebuildMeshIfReady()
        {
            if (_typeface == null) return;

            _mesh?.Dispose();
            _mesh = TextMesh.CreateFromText(_textContent, _typeface, FontSize, LetterPadding, VerticalPadding, Filled, Horizontal, Vertical);
        }

        // keep these helpers for compatibility (may be used elsewhere)
        private void RenderContoursWithCharCache(char c, int index, List<List<Vector2>> contours, float offsetX,
            float offsetY, int program, int vbo)
        {
            if (contours == null || contours.Count == 0) return;

            int key = index;
            if (!_charCache.TryGetValue(key, out var cache))
            {
                cache = new CachedChar { C = c, CachedContours = new List<List<Vector3>>() };
                _charCache[key] = cache;
            }

            bool dirty = cache.C != c ||
                         cache.LastParentX != X ||
                         cache.LastParentY != Y ||
                         cache.LastParentScale != Scale ||
                         cache.LastParentRotation != Rotation ||
                         cache.LastOffsetX != offsetX ||
                         cache.LastOffsetY != offsetY;

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

        private void RenderContours(List<List<Vector2>> contours, int program, int vbo)
        {
            int totalVerts = contours.Sum(c => c.Count);
            Console.WriteLine($"[Text Render] Rendering {totalVerts} vertices for text '{_textContent}'");
            
            foreach (var contour in contours)
            {
                if (contour.Count < 2) continue;
                var transformed = TransformVerts(contour);
                var drawVerts = PrepareDrawVerts(transformed, Filled);
                RenderVerts(drawVerts, Filled, program, vbo, Color, LineWidth);
            }
        }

        public List<List<Vector2>> GetCharContours(char c, float offsetX, float offsetY = 0f)
        {
            var fontKey = Font switch {
                FontFamily ff => FontManager.GetNameFromFamily(ff),
                string s => s,
                _ => "Arial" };
            var key = (c, FontSize, fontKey);
            if (!_contourCache.TryGetValue(key, out var contours))
            {
                contours = CharMap.GetCharContours(c, 0f, 0f, FontSize, _typeface);
                _contourCache[key] = contours;
            }

            return contours
                .Select(contour => contour.Select(v => new Vector2(v.X + offsetX, v.Y + offsetY)).ToList())
                .ToList();
        }

        // CharPrimitive left mostly the same (if you use per-char primitives)
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

            protected override List<Vector2> GetBoundaryVertsOverride()
            {
                var contours = Parent.GetCharContours(Char, X);
                return contours.SelectMany(c => c).ToList();
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

        public override void Render(int program, int vbo, int vao)
        {
            if (!Visible) return;

            // Если идёт GPU-морфинг — используем его (самый быстрый путь)
            if (GpuMorphInstance != null)
            {
                base.Render(program, vbo, vao); // делегируем в Primitive.Render → он обработает GPU-морфинг
                return;
            }

            // Если есть готовый TextMesh — используем его
            if (_mesh != null)
            {
                _mesh.Render(program, X, Y, Scale, Rotation, Color, LineWidth);
                return;
            }

            // Иначе — старый fallback (по контурам)
            base.Render(program, vbo, vao);
        }

        protected override List<Vector2> GetBoundaryVertsOverride()
        {
            var all2 = new List<Vector2>();
            var lines = _textContent.Replace("\r", "").Split('\n');

            float step = FontSize + (VerticalPadding * FontSize);
            float line0YRelativeToCenter = (Height / 2f) - (FontSize / 2f);

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