using OpenTK.Mathematics;
using PhysicsSimulation.Rendering.PrimitiveRendering;
using PhysicsSimulation.Rendering.SceneRendering;

namespace PhysicsSimulation.Scenes.Built_In_Scenes
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
            var circle1 = new Circle(x: -0.6f, y: 0.2f, radius: 0.15f, color: new Vector3(0.2f, 0.7f, 1f));
            var square1 = new Rectangle(x: 0.6f, y: 0.2f, width: 0.3f, height: 0.3f, color: new Vector3(1f, 0.5f, 0.3f));
            Add(circle.Draw(), square.Draw(), circle1.Draw(), square1.Draw());
            Wait(3f);
            
            // вращение + масштабирование
            square.RotateTo(360, 4f);
            square1.RotateTo(360, 4f);
            circle.Resize(0.4f, 4f); 
            circle1.Resize(0.4f, 4f);
            Wait(2f);
            
            square1.RotateTo(-360, 0f);
            Wait(2f);
            
            // --- 5. Текст морфинг ---
            var textA = new Text("ENERGY", x: -0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.8f, 0.6f, 1f));
            var textB = new Text("MOTION", x: 0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(1f, 0.5f, 0.2f));
            square1.MorphTo(textB);
            circle1.MorphTo(textA);
            Wait(2.5f);

            // морфим E→M, N→O и т.д.
            circle1.MorphTo(new Text("MOTION", x: 0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(1f, 0.5f, 0.2f)));
            square1.MorphTo(new Text("ENERGY", x: -0.4f, y: -0.2f, fontSize: 0.1f, color: new Vector3(0.8f, 0.6f, 1f)));
            Wait(1.5f);
            
            // --- 8. Финальный fade-out текста ---
            var endText = new Text("END OF SHOWCASE", fontSize: 0.1f, color: new Vector3(1f, 1f, 1f));
            title.MorphTo(endText);
            Wait(3f);
            square1.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            circle1.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            circle.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            square.AnimateColor(new Vector3(0f, 0f, 0f), 2f);
            AnimateBackgroundColor(new Vector3(0f, 0f, 0f), 2f);
            Wait(2f);
            title.Resize(600);
            
            Console.WriteLine("Showcase Scene complete!");
        }
    }
}