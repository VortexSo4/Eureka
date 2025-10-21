using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;
namespace PhysicsSimulation
{
    public static class Helpers
    {
        // --- PadWithDuplicates для вершин ---
        public static List<Vector2> PadWithDuplicates(List<Vector2> verts, int targetLen)
        {
            if (verts == null) throw new ArgumentNullException(nameof(verts));
            if (targetLen <= 0) return new List<Vector2>();

            if (verts.Count == 0)
                return Enumerable.Repeat(Vector2.Zero, targetLen).ToList();

            if (verts.Count >= targetLen)
            {
                float step = (float)verts.Count / targetLen;
                return Enumerable.Range(0, targetLen).Select(i => verts[(int)(i * step)]).ToList();
            }

            var newVerts = new List<Vector2>(targetLen);
            int q = targetLen / verts.Count;
            int r = targetLen % verts.Count;
            for (int i = 0; i < verts.Count; i++)
            {
                int repeats = q + (i < r ? 1 : 0);
                newVerts.AddRange(Enumerable.Repeat(verts[i], repeats));
            }
            return newVerts;
        }

        // --- OpenTK Helpers ---
        public static GameWindow InitOpenTKWindow(string title = "Physics Simulation Framework")
        {
            var nativeSettings = new NativeWindowSettings
            {
                Size = new Vector2i(1920, 1080),
                Title = title,
                Profile = ContextProfile.Core,
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3),
                NumberOfSamples = 4
            };

            var window = new GameWindow(GameWindowSettings.Default, nativeSettings);
            window.VSync = VSyncMode.On;

            // Включаем мультисэмплинг в OpenGL
            GL.Enable(EnableCap.Multisample);

            // Остальной антиалиазинг для линий и точек
            GL.Enable(EnableCap.LineSmooth);   // сглаживание линий
            GL.Enable(EnableCap.PolygonSmooth); // сглаживание полигонов (можно оставить, но иногда вызывает артефакты)
            GL.Hint(HintTarget.LineSmoothHint, HintMode.Nicest);
            GL.Hint(HintTarget.PolygonSmoothHint, HintMode.Nicest);

            return window;
        }

        public static (int Program, int Vbo) CreateGLContextAndProgram()
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            int vertexShader = CompileShader(ShaderType.VertexShader, @"
                #version 330 core
                layout(location = 0) in vec3 in_vert;
                void main() { gl_Position = vec4(in_vert, 1.0); }
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

            int vbo = GL.GenBuffer();
            return (program, vbo);
        }

        private static int CompileShader(ShaderType type, string source)
        {
            int shader = GL.CreateShader(type);
            GL.ShaderSource(shader, source);
            GL.CompileShader(shader);
            GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
            if (success == 0)
                throw new Exception($"{type} compilation failed: " + GL.GetShaderInfoLog(shader));
            return shader;
        }

        private static void CheckLinkStatus(int program)
        {
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int success);
            if (success == 0)
                throw new Exception("Program linking failed: " + GL.GetProgramInfoLog(program));
        }

        public static void RenderVertices(int program, int vbo, List<Vector3> verts, Vector3 color, PrimitiveType mode, float lineWidth = 1.0f)
        {
            if (verts == null || verts.Count == 0) return;

            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, verts.Count * Vector3.SizeInBytes, verts.ToArray(), BufferUsageHint.DynamicDraw);

            GL.UseProgram(program);
            var loc = GL.GetUniformLocation(program, "color");
            if (loc >= 0) GL.Uniform3(loc, color);

            if (mode == PrimitiveType.LineStrip || mode == PrimitiveType.Lines)
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
