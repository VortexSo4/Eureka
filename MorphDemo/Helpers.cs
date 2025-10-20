using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.Common;

namespace PhysicsSimulation
{
    public static class Helpers
    {
        public static GameWindow InitOpenTKWindow(string title = "Physics Simulation Framework")
        {
            var nativeSettings = new NativeWindowSettings
            {
                ClientSize = (1920, 1080),
                Title = title,
                Profile = ContextProfile.Core,
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3)
            };
            var window = new GameWindow(GameWindowSettings.Default, nativeSettings);
            window.VSync = VSyncMode.On;
            return window;
        }

        public static (int, int) CreateGLContextAndProgram()
        {
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            int vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, @"
                #version 330 core
                in vec3 in_vert;
                void main() {
                    gl_Position = vec4(in_vert, 1.0);
                }
            ");
            GL.CompileShader(vertexShader);
            GL.GetShader(vertexShader, ShaderParameter.CompileStatus, out int success);
            if (success == 0)
                throw new Exception("Vertex shader compilation failed: " + GL.GetShaderInfoLog(vertexShader));

            int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, @"
                #version 330 core
                uniform vec3 color;
                out vec4 f_color;
                void main() {
                    f_color = vec4(color, 1.0);
                }
            ");
            GL.CompileShader(fragmentShader);
            GL.GetShader(fragmentShader, ShaderParameter.CompileStatus, out success);
            if (success == 0)
                throw new Exception("Fragment shader compilation failed: " + GL.GetShaderInfoLog(fragmentShader));

            int program = GL.CreateProgram();
            GL.AttachShader(program, vertexShader);
            GL.AttachShader(program, fragmentShader);
            GL.LinkProgram(program);
            GL.GetProgram(program, GetProgramParameterName.LinkStatus, out success);
            if (success == 0)
                throw new Exception("Program linking failed: " + GL.GetProgramInfoLog(program));

            GL.DeleteShader(vertexShader);
            GL.DeleteShader(fragmentShader);

            int vbo = GL.GenBuffer();

            return (program, vbo);
        }
    }
}