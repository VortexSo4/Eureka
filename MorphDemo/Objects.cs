using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace PhysicsSimulation
{
    public static class Objects
    {
        public static Scene? CurrentScene { get; set; }

        public static readonly Dictionary<string, Func<float, float>> EaseFunctions = new Dictionary<string, Func<float, float>>
        {
            { "linear", t => t },
            { "ease_in_out", t => t * t * (3 - 2 * t) }
        };

        public abstract class SceneObject
        {
            public virtual void Update(float dt) { }
            public virtual void Render(int program, int vbo) { }
        }

        public class Primitive : SceneObject
        {
            public float X { get; set; }
            public float Y { get; set; }
            public bool Filled { get; set; }
            public Vector3 Color { get; set; }
            public float Vx { get; set; }
            public float Vy { get; set; }
            public float Scale { get; set; } = 1.0f;
            public float Rotation { get; set; }
            public float LineWidth { get; set; } = 1.0f;

            protected List<Dictionary<string, object>> Animations { get; } = new List<Dictionary<string, object>>();
            protected Dictionary<string, object>? ShapeAnim { get; set; }
            protected List<Vector2>? CustomBoundary { get; set; }
            protected List<Vector2>? BoundaryVerts { get; set; }

            public Primitive(float x = 0.0f, float y = 0.0f, bool filled = false, Vector3 color = default, float vx = 0.0f, float vy = 0.0f)
            {
                X = x;
                Y = y;
                Filled = filled;
                Color = color == default ? new Vector3(1.0f, 1.0f, 1.0f) : color;
                Vx = vx;
                Vy = vy;
            }

            protected void ScheduleOrExecute(Action action)
            {
                if (CurrentScene?.Recording == true)
                {
                    CurrentScene.Schedule(action);
                }
                else
                {
                    action();
                }
            }

            public static List<Vector2> PadWithDuplicates(List<Vector2> verts, int targetLen)
            {
                if (verts.Count == 0)
                    return new List<Vector2>(new Vector2[targetLen]);

                if (verts.Count >= targetLen)
                {
                    var step = verts.Count / (float)targetLen;
                    var result = new List<Vector2>();
                    for (int i = 0; i < targetLen; i++)
                        result.Add(verts[(int)(i * step)]);
                    return result;
                }

                var newVerts = new List<Vector2>();
                int q = targetLen / verts.Count;
                int r = targetLen % verts.Count;
                for (int i = 0; i < verts.Count; i++)
                {
                    for (int j = 0; j < q + (i < r ? 1 : 0); j++)
                        newVerts.Add(verts[i]);
                }
                return newVerts;
            }

            public override void Update(float dt)
            {
                // Basic kinematics
                X += Vx * dt;
                Y += Vy * dt;
                if (Math.Abs(X) > 1.0f)
                    Vx = -Vx;
                if (Math.Abs(Y) > 1.0f)
                    Vy = -Vy;

                // Update property animations
                foreach (var anim in Animations.ToArray())
                {
                    float elapsed = (float)anim["elapsed"] + dt;
                    anim["elapsed"] = elapsed;
                    float tRaw = Math.Min(1.0f, elapsed / Math.Max(1e-9f, (float)anim["duration"]));
                    var easeFn = EaseFunctions.GetValueOrDefault((string)anim["ease"], t => t);
                    float t = easeFn(tRaw);

                    string property = anim["property"].ToString();
                    if (property.Equals("Color", StringComparison.OrdinalIgnoreCase))
                    {
                        Vector3 start = (Vector3)anim["start"];
                        Vector3 target = (Vector3)anim["target"];
                        Color = start + (target - start) * t;
                    }
                    else
                    {
                        var propInfo = GetType().GetProperty(property);
                        if (propInfo != null && propInfo.PropertyType == typeof(float))
                        {
                            float start = (float)anim["start"];
                            float target = (float)anim["target"];
                            propInfo.SetValue(this, start + (target - start) * t);
                        }
                    }

                    if (tRaw >= 1.0f)
                    {
                        Animations.Remove(anim);
                        Console.WriteLine($"Completed animation for {GetType().Name}: property={property}");
                    }
                }

                // Update shape morph animation
                if (ShapeAnim != null)
                {
                    float elapsed = (float)ShapeAnim["elapsed"] + dt;
                    ShapeAnim["elapsed"] = elapsed;
                    float tRaw = Math.Min(1.0f, elapsed / Math.Max(1e-9f, (float)ShapeAnim["duration"]));
                    var easeFn = EaseFunctions.GetValueOrDefault((string)ShapeAnim["ease"], t => t);
                    float t = easeFn(tRaw);
                    var startList = (List<Vector2>)ShapeAnim["start"];
                    var targetList = (List<Vector2>)ShapeAnim["target"];
                    int length = Math.Min(startList.Count, targetList.Count);
                    BoundaryVerts = new List<Vector2>();
                    for (int i = 0; i < length; i++)
                    {
                        BoundaryVerts.Add(startList[i] + (targetList[i] - startList[i]) * t);
                    }
                    if (tRaw >= 1.0f)
                    {
                        CustomBoundary = BoundaryVerts;
                        Filled = (bool)ShapeAnim["target_filled"];
                        ShapeAnim = null;
                        Console.WriteLine($"Completed morph for {GetType().Name}: final_verts={BoundaryVerts?.Count ?? 0}");
                    }
                }
            }

            public override void Render(int program, int vbo)
            {
                List<Vector2>? boundary = ShapeAnim != null ? BoundaryVerts : CustomBoundary ?? GetBoundaryVerts();
                if (boundary == null || boundary.Count == 0)
                    return;

                float cosR = MathF.Cos(Rotation);
                float sinR = MathF.Sin(Rotation);
                var verts = new List<Vector3>();
                foreach (var v in boundary)
                {
                    float vx = v.X * Scale;
                    float vy = v.Y * Scale;
                    float vxr = vx * cosR - vy * sinR;
                    float vyr = vx * sinR + vy * cosR;
                    verts.Add(new Vector3(vxr + X, vyr + Y, 0.0f));
                }

                if (Filled)
                {
                    verts.Insert(0, new Vector3(X, Y, 0.0f));
                }
                else if (verts.Count > 0)
                {
                    verts.Add(verts[0]);
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * Vector3.SizeInBytes, verts.ToArray(), BufferUsageHint.DynamicDraw);

                GL.UseProgram(program);
                GL.Uniform3(GL.GetUniformLocation(program, "color"), Color);

                if (!Filled)
                    GL.LineWidth(LineWidth);

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
                    if (propInfo != null && propInfo.PropertyType == typeof(float))
                    {
                        float startVal = (float)(propInfo.GetValue(this) ?? 0.0f);
                        float tgt = target ?? startVal;
                        Animations.Add(new Dictionary<string, object>
                        {
                            { "property", property }, { "start", startVal }, { "target", tgt }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
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
                        { "property", "Color" }, { "start", Color }, { "target", target }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
                    });
                });
                return this;
            }

            public Primitive MoveTo(float x, float y, float duration = 1.0f, string ease = "linear")
            {
                ScheduleOrExecute(() =>
                {
                    Animations.Add(new Dictionary<string, object>
                    {
                        { "property", "X" }, { "start", X }, { "target", x }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
                    });
                    Animations.Add(new Dictionary<string, object>
                    {
                        { "property", "Y" }, { "start", Y }, { "target", y }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
                    });
                });
                return this;
            }

            public Primitive Resize(float targetScale, float duration = 1.0f, string ease = "linear")
            {
                ScheduleOrExecute(() =>
                {
                    Animations.Add(new Dictionary<string, object>
                    {
                        { "property", "Scale" }, { "start", Scale }, { "target", targetScale }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
                    });
                });
                return this;
            }

            public Primitive RotateTo(float targetRotation, float duration = 1.0f, string ease = "linear")
            {
                ScheduleOrExecute(() =>
                {
                    Animations.Add(new Dictionary<string, object>
                    {
                        { "property", "Rotation" }, { "start", Rotation }, { "target", targetRotation }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
                    });
                });
                return this;
            }

            public Primitive SetLineWidth(float target, float duration = 1.0f, string ease = "linear")
            {
                ScheduleOrExecute(() =>
                {
                    Animations.Add(new Dictionary<string, object>
                    {
                        { "property", "LineWidth" }, { "start", LineWidth }, { "target", target }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
                    });
                });
                return this;
            }

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
                        { "property", "Scale" }, { "start", 0.0f }, { "target", 1.0f }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }
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
                        if (startVerts.Count < targetVerts.Count)
                            startVerts = PadWithDuplicates(startVerts, targetVerts.Count);
                        else
                            targetVerts = PadWithDuplicates(targetVerts, startVerts.Count);
                    }
                    ShapeAnim = new Dictionary<string, object>
                    {
                        { "start", startVerts }, { "target", targetVerts }, { "duration", duration }, { "elapsed", 0.0f }, { "ease", ease }, { "target_filled", target.Filled }
                    };
                    BoundaryVerts = startVerts;
                    AnimateColor(target.Color, duration, ease);
                    MoveTo(target.X, target.Y, duration, ease);
                    Resize(target.Scale != 0.0f ? target.Scale : 1.0f, duration, ease);
                    RotateTo(target.Rotation, duration, ease);
                    SetLineWidth(target.LineWidth, duration, ease);
                });
                return this;
            }

            public virtual List<Vector2> GetBoundaryVerts()
            {
                throw new NotImplementedException("Subclasses must implement GetBoundaryVerts");
            }
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

            public override List<Vector2> GetBoundaryVerts()
            {
                var verts = new List<Vector2>();
                int numPoints = 100;
                for (int i = 0; i < numPoints; i++)
                {
                    float angle = 2 * MathF.PI * i / numPoints;
                    verts.Add(new Vector2(Radius * MathF.Cos(angle), Radius * MathF.Sin(angle)));
                }
                return verts;
            }
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
                float halfW = 0.5f * Width;
                float halfH = 0.5f * Height;
                return new List<Vector2>
                {
                    new Vector2(-halfW, -halfH),
                    new Vector2(halfW, -halfH),
                    new Vector2(halfW, halfH),
                    new Vector2(-halfW, halfH)
                };
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
                    var verts = GetCharVerts(c, offsetX, FontSize);
                    if (verts.Count > 0)
                    {
                        float cos = MathF.Cos(Rotation);
                        float sin = MathF.Sin(Rotation);
                        var scaledVerts = new List<Vector3>();
                        foreach (var v in verts)
                        {
                            float vx = v.X * Scale;
                            float vy = v.Y * Scale;
                            float vxr = vx * cos - vy * sin;
                            float vyr = vx * sin + vy * cos;
                            scaledVerts.Add(new Vector3(vxr + X, vyr + Y, 0.0f));
                        }
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
                    var charVerts = GetCharVerts(c, offsetX, FontSize);
                    if (charVerts.Count > 0)
                        allVerts.AddRange(charVerts);
                    offsetX += FontSize * 0.6f;
                }

                if (allVerts.Count == 0)
                {
                    var rect = new Rectangle(0, 0, Width, Height);
                    return rect.GetBoundaryVerts();
                }
                return allVerts;
            }

            private List<Vector2> GetCharVerts(char c, float offsetX, float size)
            {
                var charMap = new Dictionary<char, List<Vector2>>
                {
                    {'A', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(0.0f + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, -0.5f * size),
                        new Vector2(0.125f * size + offsetX, 0.0f), new Vector2(-0.125f * size + offsetX, 0.0f)
                    }},
                    {'B', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.25f * size + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.0f),
                        new Vector2(-0.125f * size + offsetX, 0.0f), new Vector2(-0.125f * size + offsetX, -0.5f * size),
                        new Vector2(-0.25f * size + offsetX, -0.5f * size)
                    }},
                    {'C', new List<Vector2> {
                        new Vector2(0.25f * size + offsetX, -0.5f * size), new Vector2(0.0f + offsetX, -0.5f * size),
                        new Vector2(-0.25f * size + offsetX, -0.25f * size), new Vector2(-0.25f * size + offsetX, 0.25f * size),
                        new Vector2(0.0f + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.5f * size)
                    }},
                    {'D', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.0f + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.25f * size),
                        new Vector2(0.25f * size + offsetX, -0.25f * size), new Vector2(0.0f + offsetX, -0.5f * size),
                        new Vector2(-0.25f * size + offsetX, -0.5f * size)
                    }},
                    {'E', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.25f * size + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.0f),
                        new Vector2(-0.25f * size + offsetX, 0.0f), new Vector2(0.25f * size + offsetX, 0.0f),
                        new Vector2(0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, -0.5f * size)
                    }},
                    {'F', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.25f * size + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.0f),
                        new Vector2(-0.25f * size + offsetX, 0.0f), new Vector2(0.25f * size + offsetX, 0.0f),
                        new Vector2(0.25f * size + offsetX, -0.5f * size)
                    }},
                    {'G', new List<Vector2> {
                        new Vector2(0.25f * size + offsetX, -0.5f * size), new Vector2(0.0f + offsetX, -0.5f * size),
                        new Vector2(-0.25f * size + offsetX, -0.25f * size), new Vector2(-0.25f * size + offsetX, 0.25f * size),
                        new Vector2(0.0f + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.25f * size + offsetX, 0.0f), new Vector2(0.0f + offsetX, 0.0f)
                    }},
                    {'I', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(0.25f * size + offsetX, -0.5f * size),
                        new Vector2(0.0f + offsetX, -0.5f * size), new Vector2(0.0f + offsetX, 0.5f * size),
                        new Vector2(-0.25f * size + offsetX, 0.5f * size), new Vector2(0.25f * size + offsetX, 0.5f * size)
                    }},
                    {'J', new List<Vector2> {
                        new Vector2(0.25f * size + offsetX, -0.5f * size), new Vector2(0.0f + offsetX, -0.5f * size),
                        new Vector2(-0.25f * size + offsetX, -0.25f * size), new Vector2(-0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.25f * size + offsetX, 0.5f * size)
                    }},
                    {'K', new List<Vector2> {
                        new Vector2(-0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, 0.5f * size),
                        new Vector2(-0.25f * size + offsetX, 0.0f), new Vector2(0.25f * size + offsetX, 0.5f * size),
                        new Vector2(0.25f * size + offsetX, -0.5f * size), new Vector2(-0.25f * size + offsetX, 0.0f)
                    }}
                };
                return charMap.ContainsKey(char.ToUpper(c)) ? charMap[char.ToUpper(c)] : new List<Vector2>();
            }
        }

        public class Scene
        {
            public List<SceneObject> Objects { get; } = new List<SceneObject>();
            private List<(float, Action)> _actions = new List<(float, Action)>();
            public bool Recording { get; private set; } = true;
            private float _timelineOffset = 0.0f;
            public float CurrentTime { get; set; } = 0.0f;

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
                {
                    float t = _timelineOffset;
                    _actions.Add((t, () => AddNow(objs)));
                }
                else
                {
                    AddNow(objs);
                }
                return this;
            }

            public Scene Wait(float duration = 1.0f)
            {
                if (Recording)
                    _timelineOffset += duration;
                else
                    CurrentTime += duration;
                return this;
            }

            public Scene Schedule(Action action)
            {
                if (Recording)
                {
                    float t = _timelineOffset;
                    _actions.Add((t, action));
                }
                else
                {
                    action();
                }
                return this;
            }

            public virtual void Update(float dt)
            {
                CurrentTime += dt;
                foreach (var action in _actions.ToArray().Where(a => a.Item1 <= CurrentTime))
                {
                    try
                    {
                        action.Item2();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Scheduled action raised: {ex.Message}");
                    }
                    _actions.Remove(action);
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
}