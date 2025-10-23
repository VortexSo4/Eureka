using OpenTK.Mathematics;

namespace PhysicsSimulation.BuiltInScenes
{
    public class MainMenuScene : Scene
    {
        protected override void StartSlides()
        {
            Console.WriteLine("Main Menu loaded");
            AnimateBackgroundColor(new Vector3(1.0f, 1.0f, 1.0f));

            var title = new Text(
                """
                PHYSICS
                SIMULATION
                """,
                y: 0.3f,
                fontSize: 0.22f,
                color: new Vector3(0.01f, 0.01f, 0.01f)
            );
            
            Add(title.Draw(1.5f));

            var hint = new Text("Press [SPACE] to continue", y: -0.5f, fontSize: 0.05f, color: new Vector3(0.01f, 0.01f, 0.01f));
            Add(hint.Draw(1.5f));
        }
    }
}

// using OpenTK.Mathematics;
//
// namespace PhysicsSimulation.BuiltInScenes
// {
//     public class MainMenuScene : Scene
//     {
//         protected override void StartSlides()
//         {
//             Console.WriteLine("Main Menu loaded");
//             AnimateBackgroundColor(new Vector3(0.05f, 0.05f, 0.08f));
//
//             var title = new Text(
//                 """
//                 PHYSICS
//                 SIMULATION
//                 """,
//                 y: 0.3f,
//                 fontSize: 0.22f,
//                 color: new Vector3(1f, 1f, 1f)
//             );
//             
//             Add(title.Draw(1.5f));
//
//             var hint = new Text("Press [SPACE] to continue", y: -0.5f, fontSize: 0.05f, color: new Vector3(0.8f, 0.8f, 0.8f));
//             Add(hint.Draw(1.5f));
//         }
//     }
// }