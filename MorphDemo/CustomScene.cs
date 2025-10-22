using OpenTK.Mathematics;
using System;

namespace PhysicsSimulation
{
    public class CustomScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Starting Showcase Scene");

            // --- 1. Фон ---
            AnimateBackgroundColor(new Vector3(0.02f, 0.03f, 0.05f)); // мягкий тёмный фон

            // --- 2. Простой fade-in текста ---
            var title = new Text("PHYSICS SHOWCASE", y: 0.7f, fontSize: 0.12f, color: new Vector3(1f, 1f, 1f));
            Add(title.Draw(2f)); // плавно появляется
            Wait(2f);

            // --- 3. Геометрические примитивы ---
            var circle = new Circle(x: -0.6f, y: 0.2f, radius: 0.15f, color: new Vector3(0.2f, 0.7f, 1f));
            var square = new Rectangle(x: 0.6f, y: 0.2f, width: 0.3f, height: 0.3f, color: new Vector3(1f, 0.5f, 0.3f));
            Add(circle.Draw(), square.Draw());
            Wait(3f);

            // вращение + масштабирование
            square.RotateTo((float)Math.PI * 2, 4f);
            circle.Resize(0.4f, 4f, EaseType.EaseInOut);
            Wait(4f);

            // --- 5. Текст морфинг ---
            var textA = new Text("ENERGY", x: -0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.8f, 0.6f, 1f));
            var textB = new Text("MOTION", x: 0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(1f, 0.5f, 0.2f));
            Add(textA.Draw(), textB.Draw());
            Wait(1.5f);

            // морфим E→M, N→O и т.д.
            textA.MorphTo(new Text("MOTION", x: 0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(1f, 0.5f, 0.2f)), 2f);
            textB.MorphTo(new Text("ENERGY", x: -0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.8f, 0.6f, 1f)), 2f);
            Wait(2.5f);

            // --- 8. Финальный fade-out текста ---
            var endText = new Text("END OF SHOWCASE", y: -0.7f, fontSize: 0.1f, color: new Vector3(1f, 1f, 1f));
            Add(endText.Draw(2f));
            Wait(3f);
            textA.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            textB.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            circle.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            square.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            AnimateBackgroundColor(new Vector3(0f, 0f, 0f), 2f);
            Wait(2f);
            title.MoveTo(title.X, 3, 1f, EaseType.EaseIn);
            endText.MoveTo(endText.X, -3, 1f, EaseType.EaseIn);

            Console.WriteLine("Showcase Scene complete!");
        }
    }
}