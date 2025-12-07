using PhysicsSimulation.Base.Utilities;
using System.Collections.Concurrent;

namespace PhysicsSimulation.Base
{
    public static class DebugManager
    {
        private static readonly string? LogFilePath;
        private static readonly object FileLock = new();

        private record LogChannel(string Tag, string Color, bool DefaultEnabled = true);

        private static readonly ConcurrentDictionary<string, LogChannel> Channels = new(
            new Dictionary<string, LogChannel>(StringComparer.OrdinalIgnoreCase)
            {
                ["Info"]     = new("INFO",     "#A0A0A0", true),
                ["Warn"]     = new("WARN",     "#FFD800", true),
                ["Error"]    = new("ERROR",    "#FF0000", true),
                ["Stats"]    = new("STATS",    "#00CC44", true),
                ["Morph"]    = new("MORPH",    "#FF55AA", true),
                ["Render"]   = new("RENDER",   "#004BFF", true),
                ["Memory"]   = new("MEMORY",   "#FF8800", true),
                ["Alloc"]    = new("ALLOC",    "#44AA44", false),
                ["Geometry"] = new("GEOM",     "#00AADD", false),
                ["Anim"]     = new("ANIM",     "#FFAA00", true),
                ["Dispatch"] = new("DISP",     "#AA88FF", true),
                ["Buffer"]   = new("BUFFER",   "#88AAAA", true),
                ["Shader"]   = new("SHADER",   "#FF6666", true),
                ["Draw"]     = new("DRAW",     "#00FFAA", true),
                ["Scene"]    = new("SCENE",    "#00AA88", true),
                ["Font"]     = new("FONT",     "#AA33FF", true),
                ["Custom"]   = new("CUSTOM",   "#FFFFFF", true)
            });

        private static readonly ConcurrentDictionary<string, bool> Enabled = new();

        private static int _maxTagLength = 5;
        private static readonly DateTime StartTime = DateTime.Now;

        static DebugManager()
        {
            foreach (var kv in Channels)
            {
                Enabled[kv.Key] = kv.Value.DefaultEnabled;
                if (kv.Value.Tag.Length > _maxTagLength)
                    _maxTagLength = kv.Value.Tag.Length;
            }

            try
            {
                var now = DateTime.Now;
                string logDir = Helpers.GetApplicationPath("Logs");
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                LogFilePath = Path.Combine(logDir, $"{now:dd.MM.yyyy_HH.mm.ss.fff}.txt");

                string header = $"[DEBUG] | Session Started: {now:dd.MM.yyyy HH:mm:ss.fff} |{Environment.NewLine}";
                File.WriteAllText(LogFilePath, header);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FATAL] Failed to create log file: {ex.Message}");
            }
        }

        private static void InternalLog(string channelKey, string message, string? customTag = null, string? customColor = null)
        {
            if (!Enabled.GetValueOrDefault(channelKey, false))
                return;

            if (!Channels.TryGetValue(channelKey, out var cfg))
                return;

            var elapsed = DateTime.Now - StartTime;
            string timestamp = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}.{elapsed.Milliseconds:D3}";

            string tag = customTag?.ToUpper() ?? cfg.Tag;
            string padded = tag.PadRight(_maxTagLength);

            string color = customColor ?? cfg.Color;
            string ansi = AnsiColorHelper.HexToAnsiEscape(color);

            string line = $"{ansi}[{padded}] | {timestamp} | {message}\u001b[0m";
            Console.WriteLine(line);

            if (LogFilePath != null)
            {
                string plain = $"[{padded}] | {timestamp} | {message}";
                lock (FileLock)
                {
                    try { File.AppendAllText(LogFilePath, plain + Environment.NewLine); }
                    catch { /* ignored */ }
                }
            }
        }

        public static void Info(string msg)     => InternalLog("Info", msg);
        public static void Warn(string msg)     => InternalLog("Warn", msg);
        public static void Error(string msg)    => InternalLog("Error", msg);
        public static void Stats(string msg)    => InternalLog("Stats", msg);
        public static void Morph(string msg)    => InternalLog("Morph", msg);
        public static void Render(string msg)   => InternalLog("Render", msg);
        public static void Memory(string msg)   => InternalLog("Memory", msg);

        public static void Alloc(string msg)    => InternalLog("Alloc", msg);
        public static void Geometry(string msg) => InternalLog("Geometry", msg);
        public static void Anim(string msg)     => InternalLog("Anim", msg);
        public static void Dispatch(string msg) => InternalLog("Dispatch", msg);
        public static void Buffer(string msg)   => InternalLog("Buffer", msg);
        public static void Shader(string msg)   => InternalLog("Shader", msg);
        public static void Draw(string msg)     => InternalLog("Draw", msg);

        public static void Scene(string msg)    => InternalLog("Scene", msg);
        public static void Font(string msg)     => InternalLog("Font", msg);

        public static void Custom(string msg, string? tag = null, string? color = null)
            => InternalLog("Custom", msg, tag, color);

        public static void Enable(string name)
        {
            if (Channels.ContainsKey(name))
                Enabled[name] = true;
            else
                Info($"Unknown log channel: {name}");
        }

        public static void Disable(string name)
        {
            if (Channels.ContainsKey(name))
                Enabled[name] = false;
            else
                Info($"Unknown log channel: {name}");
        }
    }
}