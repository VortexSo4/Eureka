using PhysicsSimulation.Base.Utilities;

namespace PhysicsSimulation.Base
{
    public enum LogLevel
    {
        Info,
        Warn,
        Error,
        Stats,
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
        public static bool ShowMorph   { get; set; } = true;
        public static bool ShowRender  { get; set; } = true;
        public static bool ShowMemory  { get; set; } = true;
        public static bool ShowGpu     { get; set; } = true;
        public static bool ShowScene   { get; set; } = true;
        public static bool ShowFontManager { get; set; } = true;

        private static readonly DateTime StartTime = DateTime.Now;
        
        private static int _maxTagLength = 5;
        
        private static void RegisterTag(string tag)
        {
            if (tag.Length > _maxTagLength)
                _maxTagLength = tag.Length;
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
        
        private static readonly Dictionary<LogLevel, string> LevelColors = new()
        {
            { LogLevel.Info,    "#A0A0A0" },
            { LogLevel.Warn,    "#FFD800" },
            { LogLevel.Error,   "#FF0000" },
            { LogLevel.Stats,   "#00CC44" },
            { LogLevel.Morph,   "#FF55AA" },
            { LogLevel.Render,  "#004BFF" },
            { LogLevel.Memory,  "#FF8800" },
            { LogLevel.Gpu,     "#227722" },
            { LogLevel.Scene,   "#00AA88" },
            { LogLevel.Font,    "#AA33FF" },
            { LogLevel.Custom,  "#FFFFFF" }
        };
        
        public static void Log(
            LogLevel level,
            string message,
            string? customTag = null,
            string? customHexColor = null
        )
        {
            if (!ShouldShow(level)) return;

            var elapsed = DateTime.Now - StartTime;
            string timestamp = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}";

            string rawTag = level switch
            {
                LogLevel.Info    => "INFO",
                LogLevel.Warn    => "WARN",
                LogLevel.Error   => "ERROR",
                LogLevel.Stats   => "STATS",
                LogLevel.Morph   => "MORPH",
                LogLevel.Render  => "RENDER",
                LogLevel.Memory  => "MEMORY",
                LogLevel.Gpu     => "GPU",
                LogLevel.Scene   => "SCENE",
                LogLevel.Font    => "FONT",
                LogLevel.Custom  => customTag?.ToUpper() ?? "CUSTOM",
                _ => "DEBUG"
            };

            RegisterTag(rawTag);

            string tag = rawTag.PadRight(_maxTagLength);

            string colorHex = customHexColor ?? (LevelColors.GetValueOrDefault(level, "#FFFFFF"));
            string colorCode = AnsiColorHelper.HexToAnsiEscape(colorHex);

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
        public static void Morph(string msg)   => Log(LogLevel.Morph, msg);
        public static void Render(string msg)  => Log(LogLevel.Render, msg);
        public static void Memory(string msg)  => Log(LogLevel.Memory, msg);
        public static void Gpu(string msg)     => Log(LogLevel.Gpu, msg);
        public static void Scene(string msg)   => Log(LogLevel.Scene, msg);
        public static void Font(string msg) => Log(LogLevel.Font, msg);

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
                case "morph":   ShowMorph = state; break;
                case "render":  ShowRender = state; break;
                case "memory":  ShowMemory = state; break;
                case "gpu":     ShowGpu = state; break;
                case "scene":   ShowScene = state; break;
                case "font": ShowFontManager = state; break;
                default: Info($"Unknown log: {name}"); break;
            }
        }

        private static bool ShouldShow(LogLevel level) => level switch
        {
            LogLevel.Info    => ShowInfo,
            LogLevel.Warn    => ShowWarn,
            LogLevel.Error   => ShowError,
            LogLevel.Stats   => ShowStats,
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