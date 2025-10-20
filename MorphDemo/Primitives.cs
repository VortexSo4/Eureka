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

    public class Text : Primitive
    {
        public string TextContent { get; set; }
        public float FontSize { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }

        public Text(string text, float x = 0.0f, float y = 0.0f, float fontSize = 0.1f, Vector3 color = default)
            : base(x, y, false, color)
        {
            TextContent = text;
            FontSize = fontSize;
            Width = text.Length * fontSize * 0.6f;
            Height = fontSize;
            Scale = 1.0f;
        }

        public override void Render(int program, int vbo)
        {
            if (CustomBoundary != null || ShapeAnim != null)
            {
                base.Render(program, vbo);
                return;
            }

            float offsetX = -Width / 2;
            foreach (char c in TextContent)
            {
                var verts = CharMap.GetCharVerts(c, offsetX, FontSize);
                if (verts.Count > 0)
                {
                    var scaledVerts = verts.Select(v => new Vector3(
                        v.X * Scale * MathF.Cos(Rotation) - v.Y * Scale * MathF.Sin(Rotation) + X,
                        v.X * Scale * MathF.Sin(Rotation) + v.Y * Scale * MathF.Cos(Rotation) + Y,
                        0.0f)).ToList();
                    if (scaledVerts.Count > 1)
                        scaledVerts.Add(scaledVerts[0]);

                    GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                    GL.BufferData(BufferTarget.ArrayBuffer, scaledVerts.Count * Vector3.SizeInBytes, scaledVerts.ToArray(), BufferUsageHint.DynamicDraw);
                    GL.UseProgram(program);
                    GL.Uniform3(GL.GetUniformLocation(program, "color"), Color);
                    GL.LineWidth(LineWidth);

                    int vao = GL.GenVertexArray();
                    GL.BindVertexArray(vao);
                    GL.EnableVertexAttribArray(0);
                    GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
                    GL.DrawArrays(PrimitiveType.LineStrip, 0, scaledVerts.Count);
                    GL.DeleteVertexArray(vao);
                }
                offsetX += FontSize * 0.6f;
            }
        }

        public override List<Vector2> GetBoundaryVerts()
        {
            var allVerts = new List<Vector2>();
            float offsetX = -Width / 2;
            foreach (char c in TextContent)
            {
                var charVerts = CharMap.GetCharVerts(c, offsetX, FontSize);
                if (charVerts.Count > 0)
                    allVerts.AddRange(charVerts);
                offsetX += FontSize * 0.6f;
            }
            return allVerts.Count == 0 ? new Rectangle(0, 0, Width, Height).GetBoundaryVerts() : allVerts;
        }
    }
}