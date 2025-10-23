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
        public static GameWindow InitOpenTkWindow(string title = "Physics Simulation", bool fullscreen = false)
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

            var window = new GameWindow(GameWindowSettings.Default, settings) { VSync = VSyncMode.On };

            // GL настройки
            GL.Enable(EnableCap.Multisample);
            GL.Enable(EnableCap.LineSmooth);
            GL.Enable(EnableCap.PolygonSmooth);
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
            GL.Hint(HintTarget.PolygonSmoothHint, HintMode.Nicest);

            // Переключение полноэкранного режима по F11
            var prevSize = window.ClientSize;
            var prevState = window.WindowState;

            window.UpdateFrame += _ =>
            {
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
                void main() { gl_Position = vec4(in_vert.x * aspectRatio, in_vert.y, in_vert.z, 1.0); }
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
        public static void RenderVertices(int program, int vbo, List<Vector3> verts, Vector3 color, PrimitiveType mode, float lineWidth = 1f)
        {
            if (verts == null || verts.Count == 0) return;

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * Vector3.SizeInBytes, verts.ToArray(), BufferUsageHint.DynamicDraw);

            GL.UseProgram(program);
            int colorLoc = GL.GetUniformLocation(program, "color");
            if (colorLoc >= 0) GL.Uniform3(colorLoc, color);

            if (mode is PrimitiveType.Lines or PrimitiveType.LineStrip)
                GL.LineWidth(lineWidth);

            int vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);
            GL.DrawArrays(mode, 0, verts.Count);

            GL.DeleteVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }
    }
}
