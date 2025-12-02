using System;
using System.Diagnostics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using PhysicsSimulation.Base;
using PhysicsSimulation.Base.Utilities;
using PhysicsSimulation.Rendering.PrimitiveRendering.GPU;

namespace PhysicsSimulation
{
    internal abstract class Program
    {
        private static void Main(string[] args)
        {
            DebugManager.Log(LogLevel.Custom, $"Current Directory: {Environment.CurrentDirectory}", "SYSTEM", "A0FF33");
            DebugManager.Log(LogLevel.Custom, $"Current Version: {Environment.Version}", "SYSTEM", "A0FF33");

            var window = Helpers.InitOpenTkWindow();
            var (program, vbo) = Helpers.CreateGlContextAndProgram();

            int vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, Vector3.SizeInBytes, 0);

            int aspectLoc = GL.GetUniformLocation(program, "u_aspectRatio");
            if (aspectLoc >= 0)
            {
                GL.UseProgram(program);
                GL.Uniform1(aspectLoc, (float)window.Size.Y / window.Size.X);
                GL.UseProgram(0);
            }

            UpdateViewport(window);
            window.Resize += _ => UpdateViewport(window);

            // Создаем сцену и настраиваем
            var arena = new GeometryArena();
            var scene = new CustomSceneGpuExample(arena);
            scene.Setup();

            var stopwatch = Stopwatch.StartNew();
            double lastTime = stopwatch.Elapsed.TotalSeconds;

            window.RenderFrame += _ =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;

                scene.Update(dt);

                GL.Viewport(0, 0, window.Size.X, window.Size.Y);
                scene.Render();
                window.SwapBuffers();
            };

            // Переключение сцен по пробелу (здесь можно добавить SceneManager)
            bool spacePressed = false;
            window.UpdateFrame += _ =>
            {
                if (window.KeyboardState.IsKeyDown(Keys.Space))
                {
                    if (spacePressed) return;
                    // SceneManager.Next();
                    // scene = SceneManager.Current!;
                    spacePressed = true;
                }
                else spacePressed = false;
            };

            window.Run();
        }

        private static void UpdateViewport(GameWindow window)
        {
            int width = window.Size.X, height = window.Size.Y;
            if (width <= 0 || height <= 0) return;

            const float desiredAspect = 16f / 9f;
            float windowAspect = (float)width / height;

            int vpX = 0, vpY = 0, vpWidth = width, vpHeight = height;

            if (windowAspect > desiredAspect)
            {
                vpWidth = (int)(height * desiredAspect);
                vpX = (width - vpWidth) / 2;
            }
            else
            {
                vpHeight = (int)(width / desiredAspect);
                vpY = (height - vpHeight) / 2;
            }

            GL.Viewport(vpX, vpY, vpWidth, vpHeight);
        }
    }
}
