using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PhysicsSimulation.Base;

namespace PhysicsSimulation.Rendering.SceneRendering
{
    public static class SceneManager
    {
        private static readonly Dictionary<string, Type> _scenes = new();
        private static readonly Dictionary<string, Assembly?> _userSceneAssemblies = new();
        private static Scene? _current;

        public static Scene? Current => _current;
        public static IReadOnlyDictionary<string, Type> RegisteredScenes => _scenes;

        // Core scenes (фиксированный порядок)
        private static readonly string[] CoreSceneOrder =
        {
            "MainMenuScene",
            "EditorScene"
        };

        // --- namespace и директории ---
        private const string BuiltInNamespace = "PhysicsSimulation.Scenes.Built_In_Scenes";
        private const string UserScenesNamespace = "PhysicsSimulation.Scenes.User_Scenes";

        static SceneManager()
        {
            RegisterAllScenes();
        }

        public static void RegisterAllScenes()
        {
            _scenes.Clear();

            // Получаем все типы сцен один раз
            var allTypes = Assembly.GetExecutingAssembly().GetTypes()
                .Where(t => t.IsSubclassOf(typeof(Scene)) && !t.IsAbstract)
                .ToList();

            // --- 1. Core сцены ---
            DebugManager.Info($"Looking for Core scenes in namespace: {BuiltInNamespace}");
            foreach (var name in CoreSceneOrder)
            {
                var type = allTypes.FirstOrDefault(t => t.Name == name && t.Namespace == BuiltInNamespace);
                if (type != null)
                {
                    _scenes[name] = type;
                    DebugManager.Info($"Core scene '{name}' loaded from assembly '{type.Assembly.GetName().Name}', namespace '{type.Namespace}'");
                }
                else
                {
                    DebugManager.Warn($"Core scene '{name}' not found in namespace '{BuiltInNamespace}'");
                }
            }

            // --- 2. Built-in сцены (все кроме core) ---
            DebugManager.Info($"Looking for Built-in scenes in namespace: {BuiltInNamespace}");
            var builtIn = allTypes
                .Where(t => !_scenes.ContainsKey(t.Name) && t.Namespace == BuiltInNamespace);

            foreach (var t in builtIn)
            {
                _scenes[t.Name] = t;
                DebugManager.Info($"Built-in scene '{t.Name}' loaded from assembly '{t.Assembly.GetName().Name}', namespace '{t.Namespace}'");
            }

            // --- 3. User сцены (через namespace) ---
            DebugManager.Info($"Looking for user scenes in namespace: {UserScenesNamespace}");

            var userTypes = allTypes
                .Where(t => t.IsSubclassOf(typeof(Scene)) && !t.IsAbstract && t.Namespace == UserScenesNamespace);

            foreach (var t in userTypes)
            {
                if (_scenes.TryAdd(t.Name, t))
                {
                    DebugManager.Info($"User scene '{t.Name}' loaded from assembly '{t.Assembly.GetName().Name}'");
                }
            }

            DebugManager.Stats($"Total loaded scenes: {_scenes.Count} → {string.Join(", ", _scenes.Keys)}");
        }

        public static Scene Load(string name)
        {
            if (!_scenes.TryGetValue(name, out var type))
                throw new Exception($"Scene '{name}' not found. Total: {string.Join(", ", _scenes.Keys)}");

            DebugManager.Scene($"Scene Loaded: {name}");
            _current?.Dispose();
            _current = (Scene)Activator.CreateInstance(type)!;
            return _current;
        }

        public static void Next()
        {
            if (_scenes.Count == 0)
            {
                DebugManager.Warn("No scenes to switch");
                return;
            }

            var list = _scenes.Keys.ToList();
            var currentIndex = _current == null ? -1 : list.IndexOf(_current.GetType().Name);
            var nextIndex = (currentIndex + 1) % list.Count;

            Load(list[nextIndex]);
        }

        public static void Reload()
        {
            if (_current == null)
            {
                DebugManager.Warn("Nothing to reload - current scene is null");
                return;
            }

            string sceneName = _current.GetType().Name;
            DebugManager.Scene($"Reloading current scene: {sceneName}");
            Load(sceneName);
        }

        private static Assembly CompileUserScene(string path)
        {
            var code = File.ReadAllText(path);
            var syntaxTree = CSharpSyntaxTree.ParseText(code);

            var assemblyName = Path.GetRandomFileName();
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>();

            var compilation = CSharpCompilation.Create(
                assemblyName,
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
            );

            using var ms = new MemoryStream();
            var result = compilation.Emit(ms);

            if (!result.Success)
            {
                var errors = string.Join("\n", result.Diagnostics
                    .Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => d.ToString()));
                throw new Exception($"Compilation of scene {path} failed:\n{errors}");
            }

            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }
    }
}
