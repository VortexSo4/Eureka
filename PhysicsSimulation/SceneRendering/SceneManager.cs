using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PhysicsSimulation.Base;

namespace PhysicsSimulation.SceneRendering
{
    public static class SceneManager
    {
        private static readonly Dictionary<string, Type> _scenes = new();
        private static Scene? _current;

        public static Scene? Current => _current;
        public static IReadOnlyDictionary<string, Type> RegisteredScenes => _scenes;

        // Core scenes (фиксированный порядок)
        private static readonly string[] CoreSceneOrder =
        [
            "MainMenuScene",
            "EditorScene"
        ];

        static SceneManager()
        {
            RegisterAllScenes();
        }

        public static void RegisterAllScenes()
        {
            _scenes.Clear();

            // --- 1. Core scenes ---
            foreach (var name in CoreSceneOrder)
            {
                var type = Assembly.GetExecutingAssembly()
                    .GetTypes()
                    .FirstOrDefault(t => t.IsSubclassOf(typeof(Scene)) && !t.IsAbstract && t.Name == name);
                if (type != null)
                {
                    _scenes[name] = type;
                    DebugManager.Info($"Core-scene '{name}' is loaded");
                }
            }

            // --- 2. Built-in сцены ---
            var builtIn = Assembly.GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsSubclassOf(typeof(Scene)) && !t.IsAbstract &&
                            !_scenes.ContainsKey(t.Name) &&
                            t.Namespace == "PhysicsSimulation.BuiltInScenes");

            foreach (var t in builtIn)
            {
                _scenes[t.Name] = t;
                DebugManager.Info($"Built-in scene '{t.Name}' is loaded");
            }

            // --- 3. User сцены ---
            string userScenesPath = Path.Combine(Environment.CurrentDirectory, "UserScenes");
            if (Directory.Exists(userScenesPath))
            {
                DebugManager.Info($"User scenes search started: {userScenesPath}");

                foreach (var file in Directory.GetFiles(userScenesPath, "*.cs"))
                {
                    try
                    {
                        var assembly = CompileUserScene(file);
                        var userTypes = assembly.GetTypes()
                            .Where(t => t.IsSubclassOf(typeof(Scene)) && !t.IsAbstract);

                        foreach (var t in userTypes)
                        {
                            if (!_scenes.ContainsKey(t.Name))
                            {
                                _scenes[t.Name] = t;
                                DebugManager.Info($"User scene '{t.Name}' is loaded from {Path.GetFileName(file)}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugManager.Error($"Scene load '{file}' is failed : {ex.Message}");
                    }
                }
            }
            else
            {
                DebugManager.Info("folder UserScenes is not found - user scenes are disabled");
            }

            DebugManager.Stats($"Total loaded scenes: {_scenes.Count} → {string.Join(", ", _scenes.Keys)}");
        }

        public static Scene Load(string name)
        {
            if (!_scenes.TryGetValue(name, out var type))
                throw new Exception($"Scene '{name}' is not found. Total: {string.Join(", ", _scenes.Keys)}");

            DebugManager.Scene($"Scene Loaded: {name}");
            _current?.Dispose();
            _current = (Scene)Activator.CreateInstance(type)!;
            return _current;
        }

        public static void Next()
        {
            if (_scenes.Count == 0)
            {
                DebugManager.Warn("Нет сцен для переключения");
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
                DebugManager.Warn("Нечего перезагружать - текущая сцена null");
                return;
            }

            DebugManager.Scene($"Перезагрузка текущей сцены: {_current.GetType().Name}");
            Load(_current.GetType().Name);
        }

        // --- Компиляция пользовательских сцен ---
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
                throw new Exception($"Компиляция сцены {path} провалилась:\n{errors}");
            }

            ms.Seek(0, SeekOrigin.Begin);
            return Assembly.Load(ms.ToArray());
        }
    }
}
