using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace PhysicsSimulation
{
    public abstract class Primitive : SceneObject
    {
        public static readonly Dictionary<string, Func<float, float>> EaseFunctions = new()
        {
            ["linear"] = t => t,
            ["ease_in_out"] = t => t * t * (3 - 2 * t)
        };

        public float X { get; set; }
        public float Y { get; set; }
        public bool Filled { get; set; }
        public Vector3 Color { get; set; }
        public float Vx { get; set; }
        public float Vy { get; set; }
        public float Scale { get; set; } = 1.0f;
        public float Rotation { get; set; }
        public float LineWidth { get; set; } = 1.0f;

        protected List<Dictionary<string, object>> Animations { get; } = [];
        protected Dictionary<string, object>? ShapeAnim { get; set; }
        protected List<Vector2>? CustomBoundary { get; set; }
        protected List<Vector2>? BoundaryVerts { get; set; }

        protected Primitive(float x = 0.0f, float y = 0.0f, bool filled = false, Vector3 color = default, float vx = 0.0f, float vy = 0.0f)
        {
            X = x;
            Y = y;
            Filled = filled;
            Color = color == default ? Vector3.One : color;
            Vx = vx;
            Vy = vy;
        }

        protected void ScheduleOrExecute(Action action) =>
            (Scene.CurrentScene?.Recording == true ? () => Scene.CurrentScene.Schedule(action) : action)();

        public override void Update(float dt)
        {
            // Kinematics
            X += Vx * dt;
            Y += Vy * dt;
            (Vx, Vy) = (Math.Abs(X) > 1.0f ? -Vx : Vx, Math.Abs(Y) > 1.0f ? -Vy : Vy);

            // Property animations
            foreach (var anim in Animations.ToArray())
            {
                float elapsed = (float)anim["elapsed"] + dt;
                anim["elapsed"] = elapsed;
                float tRaw = Math.Min(1.0f, elapsed / Math.Max(1e-9f, (float)anim["duration"]));
                float t = EaseFunctions.GetValueOrDefault((string)anim["ease"], t => t)(tRaw);
                string property = (string)anim["property"];

                switch (property.ToUpperInvariant())
                {
                    case "COLOR":
                        Color = Vector3.Lerp((Vector3)anim["start"], (Vector3)anim["target"], t);
                        break;
                    default:
                        var propInfo = GetType().GetProperty(property);
                        if (propInfo?.PropertyType == typeof(float))
                            propInfo.SetValue(this, (float)anim["start"] + ((float)anim["target"] - (float)anim["start"]) * t);
                        break;
                }

                if (tRaw >= 1.0f)
                {
                    Animations.Remove(anim);
                    Console.WriteLine($"Completed animation for {GetType().Name}: property={property}");
                }
            }

            // Shape morph animation
            if (ShapeAnim == null) return;
            float shapeElapsed = (float)ShapeAnim["elapsed"] + dt;
            ShapeAnim["elapsed"] = shapeElapsed;
            float shapeTRaw = Math.Min(1.0f, shapeElapsed / Math.Max(1e-9f, (float)ShapeAnim["duration"]));
            float shapeT = EaseFunctions.GetValueOrDefault((string)ShapeAnim["ease"], t => t)(shapeTRaw);
            var startList = (List<Vector2>)ShapeAnim["start"];
            var targetList = (List<Vector2>)ShapeAnim["target"];
            BoundaryVerts = startList.Zip(targetList, (s, t) => Vector2.Lerp(s, t, shapeT)).ToList();

            if (shapeTRaw >= 1.0f)
            {
                CustomBoundary = BoundaryVerts;
                Filled = (bool)ShapeAnim["target_filled"];
                ShapeAnim = null;
                Console.WriteLine($"Completed morph for {GetType().Name}: final_verts={BoundaryVerts?.Count ?? 0}");
            }
        }

        public override void Render(int program, int vbo)
        {
            var boundary = ShapeAnim != null ? BoundaryVerts : CustomBoundary ?? GetBoundaryVerts();
            if (boundary == null || boundary.Count == 0) return;

            var verts = boundary.Select(v => new Vector3(
                v.X * Scale * MathF.Cos(Rotation) - v.Y * Scale * MathF.Sin(Rotation) + X,
                v.X * Scale * MathF.Sin(Rotation) + v.Y * Scale * MathF.Cos(Rotation) + Y,
                0.0f)).ToList();

            if (Filled)
                verts.Insert(0, new Vector3(X, Y, 0.0f));
            else if (verts.Count > 0)
                verts.Add(verts[0]);

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * Vector3.SizeInBytes, verts.ToArray(), BufferUsageHint.DynamicDraw);
            GL.UseProgram(program);
            GL.Uniform3(GL.GetUniformLocation(program, "color"), Color);

            if (!Filled) GL.LineWidth(LineWidth);

            int vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
            GL.DrawArrays(Filled ? PrimitiveType.TriangleFan : PrimitiveType.LineStrip, 0, verts.Count);
            GL.DeleteVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        public Primitive Animate(string property, float duration = 1.0f, string ease = "linear", float? target = null)
        {
            ScheduleOrExecute(() =>
            {
                var propInfo = GetType().GetProperty(property);
                if (propInfo?.PropertyType == typeof(float))
                {
                    float startVal = (float)(propInfo.GetValue(this) ?? 0.0f);
                    Animations.Add(new Dictionary<string, object>
                    {
                        ["property"] = property,
                        ["start"] = startVal,
                        ["target"] = target ?? startVal,
                        ["duration"] = duration,
                        ["elapsed"] = 0.0f,
                        ["ease"] = ease
                    });
                }
            });
            return this;
        }

        public Primitive AnimateColor(Vector3 target, float duration = 1.0f, string ease = "linear")
        {
            ScheduleOrExecute(() =>
            {
                Animations.Add(new Dictionary<string, object>
                {
                    ["property"] = "Color",
                    ["start"] = Color,
                    ["target"] = target,
                    ["duration"] = duration,
                    ["elapsed"] = 0.0f,
                    ["ease"] = ease
                });
            });
            return this;
        }

        public Primitive MoveTo(float x, float y, float duration = 1.0f, string ease = "linear")
        {
            ScheduleOrExecute(() =>
            {
                Animations.AddRange(new[]
                {
                    new Dictionary<string, object> { ["property"] = "X", ["start"] = X, ["target"] = x, ["duration"] = duration, ["elapsed"] = 0.0f, ["ease"] = ease },
                    new Dictionary<string, object> { ["property"] = "Y", ["start"] = Y, ["target"] = y, ["duration"] = duration, ["elapsed"] = 0.0f, ["ease"] = ease }
                });
            });
            return this;
        }

        public Primitive Resize(float targetScale, float duration = 1.0f, string ease = "linear") =>
            Animate("Scale", duration, ease, targetScale);

        public Primitive RotateTo(float targetRotation, float duration = 1.0f, string ease = "linear") =>
            Animate("Rotation", duration, ease, targetRotation);

        public Primitive SetLineWidth(float target, float duration = 1.0f, string ease = "linear") =>
            Animate("LineWidth", duration, ease, target);

        public Primitive SetFilled(bool filled, float duration = 0.0f)
        {
            ScheduleOrExecute(() => Filled = filled);
            return this;
        }

        public Primitive Draw(float duration = 1.0f, string ease = "ease_in_out")
        {
            ScheduleOrExecute(() =>
            {
                Scale = 0.0f;
                Animations.Add(new Dictionary<string, object>
                {
                    ["property"] = "Scale",
                    ["start"] = 0.0f,
                    ["target"] = 1.0f,
                    ["duration"] = duration,
                    ["elapsed"] = 0.0f,
                    ["ease"] = ease
                });
            });
            return this;
        }

        public Primitive MorphTo(Primitive target, float duration = 2.0f, string ease = "ease_in_out")
        {
            ScheduleOrExecute(() =>
            {
                List<Vector2> startVerts = CustomBoundary ?? BoundaryVerts ?? GetBoundaryVerts();
                List<Vector2> targetVerts = target.GetBoundaryVerts();
                if (startVerts.Count != targetVerts.Count)
                {
                    startVerts = startVerts.Count < targetVerts.Count ? Helpers.PadWithDuplicates(startVerts, targetVerts.Count) : startVerts;
                    targetVerts = startVerts.Count > targetVerts.Count ? Helpers.PadWithDuplicates(targetVerts, startVerts.Count) : targetVerts;
                }
                ShapeAnim = new Dictionary<string, object>
                {
                    ["start"] = startVerts,
                    ["target"] = targetVerts,
                    ["duration"] = duration,
                    ["elapsed"] = 0.0f,
                    ["ease"] = ease,
                    ["target_filled"] = target.Filled
                };
                BoundaryVerts = startVerts;
                AnimateColor(target.Color, duration, ease);
                MoveTo(target.X, target.Y, duration, ease);
                Resize(target.Scale == 0.0f ? 1.0f : target.Scale, duration, ease);
                RotateTo(target.Rotation, duration, ease);
                SetLineWidth(target.LineWidth, duration, ease);
            });
            return this;
        }

        public abstract List<Vector2> GetBoundaryVerts();
    }

    public class Circle : Primitive
    {
        public float Radius { get; set; }

        public Circle(float x = 0.0f, float y = 0.0f, float radius = 0.1f, bool filled = false, Vector3 color = default, float vx = 0.0f, float vy = 0.0f)
            : base(x, y, filled, color, vx, vy)
        {
            Radius = radius;
            Scale = 1.0f;
        }

        public override List<Vector2> GetBoundaryVerts() =>
            Enumerable.Range(0, 100)
                .Select(i => new Vector2(Radius * MathF.Cos(2 * MathF.PI * i / 100), Radius * MathF.Sin(2 * MathF.PI * i / 100)))
                .ToList();
    }

    public class Rectangle : Primitive
    {
        public float Width { get; set; }
        public float Height { get; set; }

        public Rectangle(float x = 0.0f, float y = 0.0f, float width = 0.2f, float height = 0.2f, bool filled = false, Vector3 color = default, float vx = 0.0f, float vy = 0.0f)
            : base(x, y, filled, color, vx, vy)
        {
            Width = width;
            Height = height;
            Scale = 1.0f;
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            float halfW = 0.5f * Width, halfH = 0.5f * Height;
            return [new(-halfW, -halfH), new(halfW, -halfH), new(halfW, halfH), new(-halfW, halfH)];
        }
    }

    // Замените ваш текущий класс Text этим кодом.
// Требует: using System; using System.Collections.Generic; using System.Linq; using OpenTK.Graphics.OpenGL4; using OpenTK.Mathematics;

    public class Text : Primitive
    {
        public string TextContent { get; private set; }
        public float FontSize { get; set; }

        // Внутреннее представление символов
        private List<CharPrimitive> CharObjects { get; } = new();

        public Text(string text, float x = 0.0f, float y = 0.0f, float fontSize = 0.1f, Vector3 color = default,
            bool filled = false)
            : base(x, y, filled, color)
        {
            TextContent = text ?? string.Empty;
            FontSize = fontSize;
            Scale = 1.0f;
            RebuildChars();
        }

        // Перестроить CharObjects (вызывать при изменении текста, размера шрифта и т.д.)
        private void RebuildChars()
        {
            CharObjects.Clear();
            float offsetX = -TextContent.Length * FontSize * 0.3f; // центрирование (как раньше)
            foreach (char c in TextContent)
            {
                // CharPrimitive хранит локальные вершины символа без учёта offsetX
                var cp = new CharPrimitive(c, offsetX, FontSize, Color, Filled);
                CharObjects.Add(cp);
                offsetX += FontSize * 0.6f;
            }
        }

        // Получить срез текста (как объект для морфинга)
        public TextSlice GetSlice(int start, int length)
        {
            start = Math.Max(0, start);
            length = Math.Max(0, Math.Min(length, CharObjects.Count - start));
            var sliceChars = CharObjects.GetRange(start, length);
            return new TextSlice(this, sliceChars);
        }

        // Установить новый текст (перестроит символы)
        public void SetText(string newText)
        {
            TextContent = newText ?? string.Empty;
            RebuildChars();
        }

        // Массовые операции над текстом (распространяются на все символы, но не ломают индивидуальные анимации символов)
        public Text SetFilledForAll(bool filled, float animDuration = 0.0f, string ease = "linear")
        {
            ScheduleOrExecute(() =>
            {
                foreach (var c in CharObjects)
                    c.SetFilled(filled, animDuration > 0 ? animDuration : 0);
                // также обновим текущий флаг
                Filled = filled;
            });
            return this;
        }

        public Text ResizeAll(float targetScale, float duration = 1.0f, string ease = "linear")
        {
            ScheduleOrExecute(() =>
            {
                foreach (var c in CharObjects)
                    c.Animate("Scale", duration, ease, targetScale);
                // и текст в целом (логическое значение)
                Scale = targetScale;
            });
            return this;
        }

        public Text RotateAll(float targetRotation, float duration = 1.0f, string ease = "linear")
        {
            ScheduleOrExecute(() =>
            {
                foreach (var c in CharObjects)
                    c.Animate("Rotation", duration, ease, targetRotation);
                Rotation = targetRotation;
            });
            return this;
        }

        public Text AnimateColorAll(Vector3 target, float duration = 1.0f, string ease = "linear")
        {
            ScheduleOrExecute(() =>
            {
                foreach (var c in CharObjects)
                    c.AnimateColor(target, duration, ease);
                Color = target;
            });
            return this;
        }

        // --- Рендер: теперь каждый символ рендерится как отдельный набор вершин ---
        // При рендеринге мы передаём трансформации родителя (Text) в CharPrimitive, чтобы символы визуально двигались/масштабировались вместе с текстом
        public override void Render(int program, int vbo)
        {
            // Если текст морфится как одно целое (CustomBoundary/ShapeAnim задан на тексте) — отрисуем как раньше (целым примитивом)
            if (CustomBoundary != null || ShapeAnim != null)
            {
                base.Render(program, vbo);
                return;
            }

            // Рендерим каждый символ отдельно; у символов есть свои локальные вершины и offsetX
            foreach (var c in CharObjects)
            {
                var verts = c.GetTransformedVertsRelativeToParent(X, Y, Scale, Rotation);
                if (verts == null || verts.Count == 0) continue;

                // Для заполненных символов будем использовать TRIANGLE_FAN, иначе — LINE_STRIP
                var drawVerts = new List<Vector3>(verts);
                if (!c.Filled && drawVerts.Count > 1)
                    drawVerts.Add(drawVerts[0]);
                if (c.Filled)
                    drawVerts.Insert(0,
                        new Vector3(X + 0f, Y + 0f, 0f)); // центр — текстовый центр (можно модифицировать)

                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, drawVerts.Count * Vector3.SizeInBytes, drawVerts.ToArray(),
                    BufferUsageHint.DynamicDraw);
                GL.UseProgram(program);

                // each char has its own color — если цвет не задан отдельно, CharPrimitive устанавливается цветом текста при создании
                GL.Uniform3(GL.GetUniformLocation(program, "color"), c.Color);

                if (!c.Filled) GL.LineWidth(c.LineWidth);

                int vao = GL.GenVertexArray();
                GL.BindVertexArray(vao);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
                GL.DrawArrays(c.Filled ? PrimitiveType.TriangleFan : PrimitiveType.LineStrip, 0, drawVerts.Count);
                GL.DeleteVertexArray(vao);
            }
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            // Собираем все границы символов в одну плоскую последовательность (в локальных координатах текста)
            if (CharObjects.Count == 0)
                return new Rectangle(0, 0, TextContent.Length * FontSize * 0.6f, FontSize).GetBoundaryVerts();

            return CharObjects.SelectMany(c => c.GetBoundaryVerts()).ToList();
        }

        // ----------------------- Внутренние вспомогательные классы -----------------------

        // Вложенный примитив для символа: хранит локальные вершины, offset и умеет возвращать трансформированные вершины
        public class CharPrimitive : Primitive
        {
            public char Character { get; }
            public List<Vector2> LocalVerts { get; } // вершины символа в своей локальной системе (центрованной)
            public float OffsetX { get; } // смещение относительно центра текста (в локальных координатах текста)

            public CharPrimitive(char c, float offsetX, float fontSize, Vector3 color, bool filled)
                : base(0f, 0f, filled, color)
            {
                Character = c;
                OffsetX = offsetX;
                // CharMap возвращает List<Vector2> вершин символа в локальных координатах (без offset)
                LocalVerts = CharMap.GetCharVerts(c, 0f, fontSize).Select(v => new Vector2(v.X, v.Y)).ToList();
                Scale = 1.0f;
            }

            // Возвращает вершины в мировых координатах, учитывая трансформацию родителя (текст)
            public List<Vector3> GetTransformedVertsRelativeToParent(float parentX, float parentY, float parentScale,
                float parentRotation)
            {
                // Сначала применяем локальный offset (в системе текста), затем объединяем с transform родителя
                var result = new List<Vector3>(LocalVerts.Count);
                float cos = MathF.Cos(parentRotation);
                float sin = MathF.Sin(parentRotation);

                float globalOffsetX = OffsetX * parentScale; // offset учитывает масштаб родителя

                foreach (var lv in LocalVerts)
                {
                    // сначала масштабируем локальные координаты символа (char-local) с учётом родительского масштаба и char.Scale
                    float sx = (lv.X * (parentScale * Scale));
                    float sy = (lv.Y * (parentScale * Scale));

                    // вращение относительно центра текста
                    float rx = sx * cos - sy * sin;
                    float ry = sx * sin + sy * cos;

                    // переводим в мировые координаты: родительская позиция + глобальный offsetX
                    result.Add(new Vector3(parentX + globalOffsetX + rx, parentY + ry, 0.0f));
                }

                return result;
            }

            // Возвращает локальные вершины символа в координатах текста (используется при подборе начальных вершин для среза)
            public override List<Vector2> GetBoundaryVerts()
            {
                // Локальные координаты символа + смещение offsetX (в системе текста)
                return LocalVerts.Select(v => new Vector2(v.X + OffsetX, v.Y)).ToList();
            }

            // Прямой рендер символа не используется — рендер осуществляется через Text.Render (чтобы корректно комбинировать parent transform).
            // Но для тестов можно реализовать Render, вызываемый автономно:
            public override void Render(int program, int vbo)
            {
                // Если нужно отрисовать символ автономно — используем его локальную позицию
                var verts = LocalVerts.Select(v => new Vector3(v.X + OffsetX + X, v.Y + Y, 0f)).ToList();
                if (!Filled && verts.Count > 0) verts.Add(verts[0]);
                if (Filled) verts.Insert(0, new Vector3(X + OffsetX, Y, 0f));

                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * Vector3.SizeInBytes, verts.ToArray(),
                    BufferUsageHint.DynamicDraw);
                GL.UseProgram(program);
                GL.Uniform3(GL.GetUniformLocation(program, "color"), Color);
                if (!Filled) GL.LineWidth(LineWidth);

                int vao = GL.GenVertexArray();
                GL.BindVertexArray(vao);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
                GL.DrawArrays(Filled ? PrimitiveType.TriangleFan : PrimitiveType.LineStrip, 0, verts.Count);
                GL.DeleteVertexArray(vao);
            }
        }

        // Временный групповой примитив: создаётся на основе произвольного набора вершин и умеет морфиться
        // Используется TextSlice.Morph чтобы морфить только часть текста.
        private class GroupPrimitive : Primitive
        {
            public GroupPrimitive(IEnumerable<Vector2> verts, float x, float y, Vector3 color, bool filled)
                : base(x, y, filled, color)
            {
                CustomBoundary = verts.ToList();
                Scale = 1.0f;
            }

            public override List<Vector2> GetBoundaryVerts()
            {
                return CustomBoundary ?? new List<Vector2>();
            }

            // Когда группа отрисовывается, базовый Primitive.Render всё сделает
        }

        // --- Срез текста: представляет выбранные CharPrimitive, умеет морфиться в произвольный Primitive ---
        public class TextSlice
        {
            private readonly Text _parent;
            public List<Text.CharPrimitive> Chars { get; }

            public TextSlice(Text parent, List<Text.CharPrimitive> chars)
            {
                _parent = parent;
                Chars = chars ?? new List<Text.CharPrimitive>();
            }

            // Морфим выбранные символы в целевой Primitive
            public void Morph(Primitive target, float duration = 2f, string ease = "ease_in_out")
            {
                if (Chars.Count == 0 || target == null) return;

                // Сбор вершин
                var startVerts = Chars.SelectMany(c => c.GetBoundaryVerts()).ToList();

                var gp = new Text.GroupPrimitive(startVerts, _parent.X, _parent.Y, _parent.Color, _parent.Filled);
                Scene.CurrentScene?.Add(gp);

                // Скрываем символы
                foreach (var c in Chars)
                    c.Animate("Scale", 0.0f, ease, 0.0f);

                gp.MorphTo(target, duration, ease);
            }

            // --- Новые функции для анимаций напрямую на срезе ---
            public TextSlice Move(float dx, float dy, float duration = 1f, string ease = "linear")
            {
                foreach (var c in Chars)
                    c.MoveTo(c.X + dx, c.Y + dy, duration, ease);
                return this;
            }

            public TextSlice Resize(float targetScale, float duration = 1f, string ease = "linear")
            {
                foreach (var c in Chars)
                    c.Animate("Scale", duration, ease, targetScale);
                return this;
            }

            public TextSlice Rotate(float targetRotation, float duration = 1f, string ease = "linear")
            {
                foreach (var c in Chars)
                    c.Animate("Rotation", duration, ease, targetRotation);
                return this;
            }

            public TextSlice AnimateColor(Vector3 target, float duration = 1f, string ease = "linear")
            {
                foreach (var c in Chars)
                    c.AnimateColor(target, duration, ease);
                return this;
            }

            public TextSlice SetFilled(bool filled, float duration = 0f)
            {
                foreach (var c in Chars)
                    c.SetFilled(filled, duration);
                return this;
            }
        }
    }
}