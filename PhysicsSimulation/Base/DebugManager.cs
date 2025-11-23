using System;
using System.Collections.Generic;

namespace PhysicsSimulation
{
    public enum LogLevel
    {
        Info,
        Warn,
        Error,
        Stats,
        Physics,
        Morph,
        Render,
        Memory,
        Gpu,
        Scene,
        Font,
        Custom
    }

    public static class DebugManager
    {
        private static readonly string? LogFilePath;
        private static readonly object FileLock = new();
        public static bool ShowInfo    { get; set; } = true;
        public static bool ShowWarn    { get; set; } = true;
        public static bool ShowError   { get; set; } = true;
        public static bool ShowStats   { get; set; } = true;
        public static bool ShowPhysics { get; set; } = true;
        public static bool ShowMorph   { get; set; } = true;
        public static bool ShowRender  { get; set; } = true;
        public static bool ShowMemory  { get; set; } = true;
        public static bool ShowGpu     { get; set; } = true;
        public static bool ShowScene   { get; set; } = true;
        public static bool ShowFontManager { get; set; } = true;

        private static readonly DateTime _startTime = DateTime.Now;
        
        private static readonly HashSet<string> _knownTags = new();
        private static int _maxTagLength = 5;
        
        private static void RegisterTag(string tag)
        {
            if (tag.Length > _maxTagLength)
                _maxTagLength = tag.Length;

            _knownTags.Add(tag);
        }
        
        static DebugManager()
        {
            try
            {
                var now = DateTime.Now;
                string filename = $"{now:dd.MM.yyyy_HH.mm.ss.fff}.txt";
                string logDir = Helpers.GetApplicationPath("Logs");

                if (!Directory.Exists(logDir))
                    Directory.CreateDirectory(logDir);

                LogFilePath = Path.Combine(logDir, filename);

                string header = $"[DEBUG] | Session Started: {now:dd.MM.yyyy HH:mm:ss.fff} | {Environment.NewLine}";
                File.WriteAllText(LogFilePath, header);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] Failed to create log file: {ex.Message}");
            }
        }
        
        public static void Log(LogLevel level, string message, string? customTag = null)
        {
            if (!ShouldShow(level)) return;

            var elapsed = DateTime.Now - _startTime;
            string timestamp = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}";

            string rawTag = level switch
            {
                LogLevel.Info    => "INFO",
                LogLevel.Warn    => "WARN",
                LogLevel.Error   => "ERROR",
                LogLevel.Stats   => "STATS",
                LogLevel.Physics => "PHYS",
                LogLevel.Morph   => "MORPH",
                LogLevel.Render  => "REND",
                LogLevel.Memory  => "MEM",
                LogLevel.Gpu     => "GPU",
                LogLevel.Scene   => "SCENE",
                LogLevel.Font    => "FONT",
                LogLevel.Custom  => customTag?.ToUpper() ?? "CUSTOM",
                _ => "DEBUG"
            };

            RegisterTag(rawTag);

            string tag = rawTag.PadRight(_maxTagLength);

            string colorCode = level switch
            {
                LogLevel.Info    => "\u001b[37m",
                LogLevel.Warn    => "\u001b[33m",
                LogLevel.Error   => "\u001b[31m",
                LogLevel.Stats   => "\u001b[36m",
                LogLevel.Physics => "\u001b[35m",
                LogLevel.Morph   => "\u001b[95m",
                LogLevel.Render  => "\u001b[94m",
                LogLevel.Memory  => "\u001b[93m",
                LogLevel.Gpu     => "\u001b[92m",
                LogLevel.Scene   => "\u001b[96m",
                LogLevel.Font    => "\u001b[38;5;219m",
                LogLevel.Custom  => "\u001b[97m",
                _ => "\u001b[37m"
            };

            string reset = "\u001b[0m";
            string line = $"{colorCode}[{tag}] | {timestamp} | {message}{reset}";

            // В консоль
            Console.WriteLine(line);

            // В файл — безопасно и без блокировок
            string plainLine = $"[{tag}] | {timestamp} | {message}";
            if (LogFilePath == null) return;

            lock (FileLock)
            {
                try
                {
                    File.AppendAllText(LogFilePath, plainLine + Environment.NewLine);
                }
                catch
                {
                    // ignored
                }
            }
        }

        public static void Info(string msg)    => Log(LogLevel.Info, msg);
        public static void Warn(string msg)    => Log(LogLevel.Warn, msg);
        public static void Error(string msg)   => Log(LogLevel.Error, msg);
        public static void Stats(string msg)   => Log(LogLevel.Stats, msg);
        public static void Physics(string msg) => Log(LogLevel.Physics, msg);
        public static void Morph(string msg)   => Log(LogLevel.Morph, msg);
        public static void Render(string msg)  => Log(LogLevel.Render, msg);
        public static void Memory(string msg)  => Log(LogLevel.Memory, msg);
        public static void Gpu(string msg)     => Log(LogLevel.Gpu, msg);
        public static void Scene(string msg)   => Log(LogLevel.Scene, msg);
        public static void Font(string msg) => Log(LogLevel.Font, msg);

        private static float _fps;
        private static int _primitives;
        private static int _vertices;
        private static int _morphs;

        public static void UpdateStats(float fps, int primitives, int vertices, int morphs)
        {
            _fps = fps;
            _primitives = primitives;
            _vertices = vertices;
            _morphs = morphs;

            if (ShowStats)
            {
                Stats($"FPS: {_fps:F1} | Объектов: {_primitives} | Вершин: {_vertices:N0} | Морфится: {_morphs} | Память: {GC.GetTotalMemory(false)/1024/1024} МБ");
            }
        }

        // === ВКЛЮЧЕНИЕ/ВЫКЛЮЧЕНИЕ ===
        public static void Enable(string name)  => SetLevel(name, true);
        public static void Disable(string name) => SetLevel(name, false);

        private static void SetLevel(string name, bool state)
        {
            switch (name.ToLower())
            {
                case "info":    ShowInfo = state; break;
                case "warn":    ShowWarn = state; break;
                case "error":   ShowError = state; break;
                case "stats":   ShowStats = state; break;
                case "physics": ShowPhysics = state; break;
                case "morph":   ShowMorph = state; break;
                case "render":  ShowRender = state; break;
                case "memory":  ShowMemory = state; break;
                case "gpu":     ShowGpu = state; break;
                case "scene":   ShowScene = state; break;
                case "font": ShowFontManager = state; break;
                default: Info($"Неизвестный лог: {name}"); break;
            }
        }

        private static bool ShouldShow(LogLevel level) => level switch
        {
            LogLevel.Info    => ShowInfo,
            LogLevel.Warn    => ShowWarn,
            LogLevel.Error   => ShowError,
            LogLevel.Stats   => ShowStats,
            LogLevel.Physics => ShowPhysics,
            LogLevel.Morph   => ShowMorph,
            LogLevel.Render  => ShowRender,
            LogLevel.Memory  => ShowMemory,
            LogLevel.Gpu     => ShowGpu,
            LogLevel.Scene   => ShowScene,
            LogLevel.Font    => ShowFontManager,
            _ => true
        };
    }
}