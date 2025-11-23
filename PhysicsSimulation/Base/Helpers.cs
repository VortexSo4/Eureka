using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace PhysicsSimulation
{
    public static class Helpers
    {
        private static string vertexShaderSource = @"
#version 430 core
layout (location = 0) in vec3 aPosition;

uniform vec2 u_translate;
uniform float u_cos;
uniform float u_sin;
uniform float u_scale;
uniform vec3 u_color;
uniform float u_aspectRatio;

out vec3 fragColor;

void main()
{
    vec3 pos = aPosition;
    float x = pos.x * u_cos - pos.y * u_sin;
    float y = pos.x * u_sin + pos.y * u_cos;
    x *= u_scale;
    y *= u_scale;
    x += u_translate.x;
    y += u_translate.y;
    x *= u_aspectRatio;
    gl_Position = vec4(x, y, pos.z, 1.0);
    fragColor = u_color;
}
";

        
        private static string fragmentShaderSource = @"
#version 430 core
in vec3 fragColor;
out vec4 FragColor;

void main()
{
    FragColor = vec4(fragColor, 1.0);
}
";

        
        // --- Инициализация окна OpenTK ---
        public static GameWindow InitOpenTkWindow(string title = "Physics Simulation", bool fullscreen = false, bool debug_mode = true)
        {
            var settings = new NativeWindowSettings
            {
                ClientSize = new Vector2i(1920, 1080),
                Title = title,
                Profile = ContextProfile.Core,
                API = ContextAPI.OpenGL,
                APIVersion = new Version(4, 3),
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

            int vertexShader = CompileShader(ShaderType.VertexShader, vertexShaderSource);

            int fragmentShader = CompileShader(ShaderType.FragmentShader, fragmentShaderSource);

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
        public static List<Vector2> ResizeVertexList(List<Vector2> source, int targetCount)
        {
            if (source.Count == targetCount) return source;
            if (source.Count == 0) return new List<Vector2>(new Vector2[targetCount]);
            if (targetCount <= 0) return new List<Vector2>();

            var result = new List<Vector2>(targetCount);

            if (source.Count < targetCount)
            {
                // Увеличиваем: интерполируем между точками
                float step = (float)(source.Count - 1) / (targetCount - 1);
                for (int i = 0; i < targetCount; i++)
                {
                    float t = i * step;
                    int idx = (int)t;
                    float frac = t - idx;

                    if (idx >= source.Count - 1)
                        result.Add(source[^1]);
                    else
                        result.Add(Vector2.Lerp(source[idx], source[idx + 1], frac));
                }
            }
            else
            {
                // Уменьшаем: берём точки равномерно
                float step = (float)source.Count / targetCount;
                for (int i = 0; i < targetCount; i++)
                {
                    int idx = (int)(i * step);
                    result.Add(source[idx]);
                }
            }

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
            int colorLoc = GL.GetUniformLocation(program, "u_color");
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
        
        public static string GetApplicationPath(string subfolder)
        {
            string root = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null)
                {
                    var csprojFiles = dir.GetFiles("*.csproj", SearchOption.TopDirectoryOnly);
                    if (csprojFiles.Length > 0)
                    {
                        root = dir.FullName;
                        break;
                    }
                    dir = dir.Parent;
                }

                if (string.IsNullOrEmpty(root))
                    root = Directory.GetCurrentDirectory();
            }

            string path = Path.Combine(root, subfolder);
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            return path;
        }
    }
}