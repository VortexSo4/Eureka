using System.Diagnostics;
using EurekaDSL;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using PhysicsSimulation.Base;
using PhysicsSimulation.Base.Utilities;
using PhysicsSimulation.Rendering.SceneRendering;

namespace PhysicsSimulation
{
    internal abstract class Program
    {
        public static void Main()
        {
            DebugManager.Log(LogLevel.Custom, $"Current Directory: {Environment.CurrentDirectory}", "SYSTEM", "A0FF33");
            DebugManager.Log( LogLevel.Custom, $"Current Version: {Environment.Version}", "SYSTEM", "A0FF33");

            //string sceneName = args.Length > 0 ? args[0] : "MainMenuScene";

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

            // === 2. DSL-скрипт ===
            const string src = @"
            scene(<Main Menu>)

            bgColor([0.05, 0.05, 0.08], 1)

            xsd = 5/3
            title = text {
                text: $<Eureka {xsd}>,
                x: 1.5,
                y: -1,
                fontSize: 0.2,
                font: <Quantico-Bold>,
                color: [1,1,1],
                horizontal: <Right>,
                vertical: <Bottom>
            }

            c = circle {
                x: 0.0,
                y: 0.0,
                radius: 0.8,
                color: [1, 0.3, 0.6]
            }

            r = rect {
                x: 0.5,
                y: 0.5,
                width: 0.2,
                height: 0.4,
                color: [1, 0.3, 0.6]
            }

            Add {
                c.draw(1.2),
                title.draw(1.5),
                r.draw(1.9)
            }

            wait(2)

            c.move(2, 0.8, 0)
            title.move(1, 0.5, 0)
            r.move(2, -0.5, -0.5)
            wait(2)
            c.scale(1.5, 1.8)
            r.scale(1.5, 1.8)
            wait(2)
            r.morph(c, 2)
            wait(3)
            c.color([0, 0, 0], 2)
            r.color([0, 0, 0], 2)
            title.color([0, 0, 0], 2)
            title.x = 10

            wait(3)
            ";

            // === 3. ВЫПОЛНЯЕМ СКРИПТ ТОЛЬКО ПОСЛЕ создания OpenGL ===
            bool scriptExecuted = false;

            window.UpdateFrame += _ =>
            {
                if (!scriptExecuted)
                {
                    scriptExecuted = true;
                    Bootstrap.RunScript(src);
                }
            };

            // === 4. Главный цикл рендера ===
            var stopwatch = Stopwatch.StartNew();
            double lastTime = stopwatch.Elapsed.TotalSeconds;

            window.RenderFrame += _ =>
            {
                double currentTime = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(currentTime - lastTime);
                lastTime = currentTime;

                var currentScene = Scene.CurrentScene;
                if (currentScene == null)
                {
                    // Ничего не рендерим, пока сцена ещё не готова
                    GL.ClearColor(0.05f, 0.05f, 0.08f, 1f);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    window.SwapBuffers();
                    return;
                }

                currentScene.Update(dt);

                GL.Viewport(0, 0, window.Size.X, window.Size.Y);
                GL.ClearColor(0f, 0f, 0f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                UpdateViewport(window);

                GL.ClearColor(currentScene.BackgroundColor.X, currentScene.BackgroundColor.Y, currentScene.BackgroundColor.Z, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);

                currentScene.Render(program, vbo, vao);
                window.SwapBuffers();
            };

            // --- переключение сцен по пробелу ---
            /*
            bool spacePressed = false;
            window.UpdateFrame += _ =>
            {
                if (window.KeyboardState.IsKeyDown(Keys.Space))
                {
                    if (spacePressed) return;
                    SceneManager.Next();
                    scene = SceneManager.Current!;
                    spacePressed = true;
                }
                else spacePressed = false;
            };
            */

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
