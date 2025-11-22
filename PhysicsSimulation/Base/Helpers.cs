using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace PhysicsSimulation
{
    public static class Helpers
    {
        // --- Инициализация окна OpenTK ---
        public static GameWindow InitOpenTkWindow(string title = "Physics Simulation", bool fullscreen = false, bool debug_mode = true)
        {
            var settings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1920, 1080),
                Title = title,
                Profile = ContextProfile.Core,
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3),
                NumberOfSamples = 4,
                WindowState = fullscreen ? WindowState.Fullscreen : WindowState.Normal
            };

            var window = new GameWindow(GameWindowSettings.Default, settings) { VSync = VSyncMode.Off };

            // GL настройки
            GL.Enable(EnableCap.Multisample);
            GL.Enable(EnableCap.LineSmooth);
            GL.Enable(EnableCap.PolygonSmooth);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
            GL.Hint(HintTarget.PolygonSmoothHint, HintMode.Nicest);

            // Переключение полноэкранного режима по F11
            var prevSize = window.ClientSize;
            var prevState = window.WindowState;
            
            int instantFrames = 0, avgFrames = 0;
            double instantTimer = 0, avgTimer = 0;
            string baseTitle = title;

            window.UpdateFrame += e =>
            {
                UpdateFPS(window, e.Time, ref instantFrames, ref instantTimer, ref avgFrames, ref avgTimer, baseTitle, debug_mode);
                if (!window.KeyboardState.IsKeyPressed(Keys.F11)) return;

                if (window.WindowState == WindowState.Fullscreen)
                {
                    window.WindowState = prevState;
                    window.ClientSize = prevSize;
                    window.WindowBorder = WindowBorder.Resizable;
                }
                else
                {
                    prevSize = window.ClientSize;
                    prevState = window.WindowState;
                    window.WindowBorder = WindowBorder.Hidden;
                    window.WindowState = WindowState.Fullscreen;
                }
            };

            return window;
        }

        // --- Компиляция шейдеров и создание GL-программы ---
        public static (int Program, int Vbo) CreateGlContextAndProgram()
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            int vertexShader = CompileShader(ShaderType.VertexShader, @"
#version 330 core

layout(location = 0) in vec3 in_vert;

uniform float aspectRatio;

// separate transform components
uniform vec2 u_translate;
uniform float u_cos;
uniform float u_sin;
uniform float u_scale;

void main()
{
    float s = u_scale;
    float c = u_cos;
    float sn = u_sin;

    float x = (in_vert.x * (c * s) - in_vert.y * (sn * s)) + u_translate.x;
    float y = (in_vert.x * (sn * s) + in_vert.y * (c * s)) + u_translate.y;
    gl_Position = vec4(x * aspectRatio, y, in_vert.z, 1.0);
}
");

            int fragmentShader = CompileShader(ShaderType.FragmentShader, @"
                #version 330 core
                uniform vec3 color;
                out vec4 f_color;
                void main() { f_color = vec4(color, 1.0); }
            ");

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            CheckLinkStatus(program);

            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            return (program, GL.GenBuffer());
        }

        private static int CompileShader(ShaderType type, string src)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, src);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int ok);
            if (ok == 0) throw new Exception($"{type} compilation failed: {GL.GetShaderInfoLog(shader)}");
            return shader;
        }

        private static void CheckLinkStatus(int program)
        {
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int ok);
            if (ok == 0) throw new Exception($"Program link failed: {GL.GetProgramInfoLog(program)}");
        }
        
        // --- Дублирование вершин до нужной длины ---
        public static List<Vector2> PadWithDuplicates(List<Vector2> verts, int targetLen)
        {
            if (verts == null) throw new ArgumentNullException(nameof(verts));
            if (targetLen <= 0) return new();

            int count = verts.Count;
            if (count == 0)
                return Enumerable.Repeat(Vector2.Zero, targetLen).ToList();

            if (count >= targetLen)
            {
                float step = (float)count / targetLen;
                return Enumerable.Range(0, targetLen).Select(i => verts[(int)(i * step)]).ToList();
            }

            var result = new List<Vector2>(targetLen);
            int q = targetLen / count, r = targetLen % count;
            for (int i = 0; i < count; i++)
                result.AddRange(Enumerable.Repeat(verts[i], q + (i < r ? 1 : 0)));

            return result;
        }

        // --- Отрисовка вершин ---
        public static void RenderVertices(int program, int vbo, int vao, List<Vector3> verts, Vector3 color, PrimitiveType mode, float lineWidth = 1f)
        {
            if (verts == null || verts.Count == 0) return;

            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * Vector3.SizeInBytes, verts.ToArray(), BufferUsageHint.DynamicDraw);

            GL.UseProgram(program);
            int colorLoc = GL.GetUniformLocation(program, "color");
            if (colorLoc >= 0) GL.Uniform3(colorLoc, color);

            if (mode == PrimitiveType.Lines || mode == PrimitiveType.LineStrip || mode == PrimitiveType.LineLoop)
                GL.LineWidth(lineWidth);

            GL.DrawArrays(mode, 0, verts.Count);
        }
        
        public static void UpdateFPS(GameWindow window, double dt, ref int instantFrames, ref double instantTimer,
            ref int avgFrames, ref double avgTimer, string baseTitle, bool debug_mode,
            double instantUpdateInterval = 0.5, double avgInterval = 5.0)
        {
            if (!debug_mode) return;

            instantFrames++;
            avgFrames++;
            instantTimer += dt;
            avgTimer += dt;

            double instantFps = 0;
            double avgFps = 0;
            bool updated = false;

            if (instantTimer >= instantUpdateInterval)
            {
                instantFps = instantFrames / instantTimer;
                instantFrames = 0;
                instantTimer = 0;
                updated = true;
            }

            if (avgTimer >= avgInterval)
            {
                avgFps = avgFrames / avgTimer;
                avgFrames = 0;
                avgTimer = 0;
                updated = true;
            }

            if (updated)
            {
                string fpsStr = ((int)instantFps).ToString().PadLeft(5, ' ');
                string avgStr = ((int)avgFps).ToString().PadLeft(5, ' ');
                window.Title = $"{baseTitle} | FPS: {fpsStr} | AVG: {avgStr}";
            }
        }
    }
}
