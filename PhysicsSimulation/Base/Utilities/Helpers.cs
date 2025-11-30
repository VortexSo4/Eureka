using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace PhysicsSimulation.Base.Utilities
{
    public static class Helpers
    {
        private static readonly string _morphComputeShaderSource = """
                                                                   #version 460 core

                                                                   layout(local_size_x = 256) in;

                                                                   layout(std430, binding = 0) readonly buffer SourceBuffer { vec2 sourceVertices[]; };
                                                                   layout(std430, binding = 1) readonly buffer TargetBuffer { vec2 targetVertices[]; };
                                                                   layout(std430, binding = 2) buffer OutputBuffer { vec2 outputVertices[]; };

                                                                   uniform float t;

                                                                   const vec2 NAN_VEC2 = vec2(uintBitsToFloat(0x7F800001u), uintBitsToFloat(0x7F800001u));

                                                                   void main()
                                                                   {
                                                                       uint idx = gl_GlobalInvocationID.x;
                                                                       if (idx >= sourceVertices.length() || idx >= targetVertices.length()) return;

                                                                       vec2 start = sourceVertices[idx];
                                                                       vec2 end   = targetVertices[idx];

                                                                       if (isnan(start.x) || isnan(start.y) || isnan(end.x) || isnan(end.y))
                                                                       {
                                                                           outputVertices[idx] = NAN_VEC2;
                                                                           return;
                                                                       }

                                                                       outputVertices[idx] = mix(start, end, t);
                                                                   }
                                                                   """;

        private static int _morphComputeProgram = -1;
        private static string _vertexShaderSource = """
                                                    #version 430 core
                                                    layout (location = 0) in vec3 aPosition;

                                                    uniform mat4 u_model;
                                                    uniform mat4 u_viewProjection;
                                                    uniform vec3 u_color;
                                                    uniform float u_aspectRatio;

                                                    out vec3 fragColor;

                                                    void main()
                                                    {
                                                        vec4 pos = vec4(aPosition, 1.0);
                                                        pos = u_model * pos;
                                                        pos = u_viewProjection * pos;
                                                        //pos.x *= u_aspectRatio;
                                                        gl_Position = pos;
                                                        fragColor = u_color;
                                                    }
                                                    """;

        private static string _fragmentShaderSource = """
                                                      #version 430 core
                                                      in vec3 fragColor;
                                                      out vec4 FragColor;

                                                      void main()
                                                      {
                                                          FragColor = vec4(fragColor, 1.0);
                                                      }
                                                      """;

        public static int GetMorphComputeProgram()
        {
            if (_morphComputeProgram != -1)
                return _morphComputeProgram;

            _morphComputeProgram = LoadComputeShader(_morphComputeShaderSource);
            return _morphComputeProgram;
        }
        
        
        // --- Инициализация окна OpenTK ---
        public static GameWindow InitOpenTkWindow(string title = "Physics Simulation", bool fullscreen = false, bool debugMode = true)
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
                UpdateFps(window, e.Time, ref instantFrames, ref instantTimer, ref avgFrames, ref avgTimer, baseTitle, debugMode);
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

            int vertexShader = CompileShader(ShaderType.VertexShader, _vertexShaderSource);

            int fragmentShader = CompileShader(ShaderType.FragmentShader, _fragmentShaderSource);

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
        public static List<Vector2> ResizeVertexList(List<Vector2> source, int targetTotalPoints)
        {
            if (source.Count == 0)
                return [..new Vector2[targetTotalPoints]];
            if (source.Count == targetTotalPoints)
                return source;
            if (targetTotalPoints <= 0)
                return [];

            // Разбиваем на контуры по NaN
            var contours = new List<List<Vector2>>();
            var current = new List<Vector2>();

            foreach (var v in source)
            {
                if (float.IsNaN(v.X))
                {
                    if (current.Count > 0)
                    {
                        contours.Add(current);
                        current = [];
                    }
                }
                else current.Add(v);
            }

            if (current.Count > 0) contours.Add(current);

            if (contours.Count == 0)
                return [..new Vector2[targetTotalPoints]];

            var result = new List<Vector2>();

            // Ключевое изменение: минимум 12 точек на контур — для идеальных прямых и углов
            const int minPointsPerContour = 12;

            int pointsPerContour = targetTotalPoints / contours.Count;
            int extraPoints = targetTotalPoints % contours.Count;

            // Гарантируем минимум 12 точек на контур
            if (pointsPerContour < minPointsPerContour)
            {
                pointsPerContour = minPointsPerContour;
                extraPoints = 0; // перераспределять не будем — и так хватит
            }

            for (int i = 0; i < contours.Count; i++)
            {
                var contour = contours[i];
                int targetPoints = pointsPerContour + (i < extraPoints ? 1 : 0);

                if (contour.Count < 2)
                {
                    // Пустой контур — дублируем
                    for (int j = 0; j < targetPoints; j++)
                        result.Add(contour.Count > 0 ? contour[0] : Vector2.Zero);
                }
                else
                {
                    result.Add(contour[0]); // первая — всегда точно

                    if (targetPoints > 2)
                    {
                        float step = (float)(contour.Count - 1) / (targetPoints - 1);
                        for (int j = 1; j < targetPoints - 1; j++)
                        {
                            float t = j * step;
                            int idx = (int)t;
                            float frac = t - idx;

                            if (idx >= contour.Count - 1)
                                result.Add(contour[^1]);
                            else
                                result.Add(Vector2.Lerp(contour[idx], contour[idx + 1], frac));
                        }
                    }

                    result.Add(contour[^1]); // последняя — всегда точно
                }

                if (i < contours.Count - 1)
                    result.Add(new Vector2(float.NaN, float.NaN));
            }

            return result;
        }
        
        public static void UpdateFps(GameWindow window, double dt, ref int instantFrames, ref double instantTimer,
            ref int avgFrames, ref double avgTimer, string baseTitle, bool debugMode,
            double instantUpdateInterval = 0.5, double avgInterval = 5.0)
        {
            if (!debugMode) return;

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
        public static bool AlmostEqual(float a, float b, float eps = 1e-5f) => Math.Abs(a - b) <= eps;
        
        public static void TestDebugManager()
        {
            DebugManager.Info("some text for testing debug output");
            DebugManager.Warn("some text for testing debug output");
            DebugManager.Error("some text for testing debug output");
            DebugManager.Stats("some text for testing debug output");
            DebugManager.Morph("some text for testing debug output");
            DebugManager.Render("some text for testing debug output");
            DebugManager.Memory("some text for testing debug output");
            DebugManager.Gpu("some text for testing debug output");
            DebugManager.Scene("some text for testing debug output");
            DebugManager.Font("some text for testing debug output");
        }
        
        public static int LoadComputeShader(string source)
        {
            int shader = GL.CreateShader(ShaderType.ComputeShader);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);

            GL.GetShader(shader, ShaderParameter.CompileStatus, out _);
            int program = GL.CreateProgram();
            GL.AttachShader(program, shader);
            GL.LinkProgram(program);

            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linkSuccess);
            if (linkSuccess == 0)
                throw new Exception("Compute program link failed: " + GL.GetProgramInfoLog(program));

            GL.DeleteShader(shader);
            return program;
        }
    }
}