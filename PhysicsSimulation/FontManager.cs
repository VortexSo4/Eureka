using SkiaSharp;

namespace PhysicsSimulation
{
    public enum FontFamily
    {
        TimesNewRoman,
        Arial,
        ArialBlack,
        Calibri,
        Audiowide,
        Orbitron,
        Quantico,
        Oxanium,
        Exo2
    }

    public static class FontManager
    {
        public static string FontDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");

        private static readonly Dictionary<string, SKTypeface> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static string GetNameFromFamily(FontFamily? f)
        {
            if (f == null) return "Arial";
            return f.Value switch
            {
                FontFamily.TimesNewRoman => "Times New Roman",
                FontFamily.Arial => "Arial",
                FontFamily.ArialBlack => "Arial Black",
                FontFamily.Calibri => "Calibri",
                FontFamily.Audiowide => "Audiowide",
                FontFamily.Orbitron => "Orbitron",
                FontFamily.Quantico => "Quantico",
                FontFamily.Oxanium => "Oxanium",
                FontFamily.Exo2 => "Exo 2",
                _ => "Arial"
            };
        }

        public static SKTypeface GetTypeface(FontFamily? family = null, string? fontName = null)
        {
            string key = fontName ?? GetNameFromFamily(family);
            if (string.IsNullOrWhiteSpace(key)) key = "Arial";

            Console.WriteLine(
                $"[FontManager] Requested font key: '{key}' (fontName param {(fontName == null ? "null" : "set")})");

            // --- 1) Сформировать кандидатов локальных путей (учитываем случаи с расширением / путём) ---
            var localCandidates = new List<string>();

            // если fontName выглядит как абсолютный/относительный путь - добавим его прямо
            if (!string.IsNullOrEmpty(fontName) &&
                (fontName.Contains(Path.DirectorySeparatorChar) || fontName.Contains('/')))
            {
                // если передали прямой путь — сначала сам параметр, затем попробовать в FontDir с именем файла
                localCandidates.Add(fontName);
                localCandidates.Add(Path.Combine(FontDir, Path.GetFileName(fontName)));
            }
            else
            {
                // базовые варианты: ключ как есть и без пробелов
                var baseNames = new List<string> { key, key.Replace(" ", "") };

                // если ключ имеет расширение (.ttf/.otf), отдельно добавим вариант с этим именем
                if (Path.HasExtension(key))
                {
                    baseNames.Add(Path.GetFileNameWithoutExtension(key));
                }

                var exts = new[] { ".ttf", ".otf", "" };

                foreach (var bn in baseNames.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (var ext in exts)
                    {
                        localCandidates.Add(Path.Combine(FontDir, bn + ext));
                    }
                }
            }

            // краткая печать кандидатов
            Console.WriteLine($"[FontManager] Local candidates to check ({localCandidates.Count}):");
            foreach (var c in localCandidates) Console.WriteLine($"  -> {c}");

            // 1a) Попробовать загрузить локальный файл (проверяем все кандидаты)
            try
            {
                foreach (var path in localCandidates)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (File.Exists(path))
                    {
                        Console.WriteLine($"[FontManager] Found local font file: {path}");
                        try
                        {
                            var tf = SKTypeface.FromFile(path);
                            if (tf != null)
                            {
                                Console.WriteLine($"[FontManager] Loaded SKTypeface from file: {path}");
                                Cache[key] = tf;
                                return tf;
                            }
                            else
                            {
                                Console.WriteLine($"[FontManager] SKTypeface.FromFile returned null for: {path}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[FontManager] Exception loading font from '{path}': {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[FontManager] Local file not found: {path}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FontManager] Error scanning local font files: {ex.Message}");
            }

            // 1b) Если локального файла нет, но в кеше уже есть запись — вернуть её
            if (Cache.TryGetValue(key, out var cached))
            {
                Console.WriteLine($"[FontManager] Using cached SKTypeface for key: '{key}'");
                return cached;
            }

            // 2) Попробовать системный шрифт
            try
            {
                var sys = SKTypeface.FromFamilyName(key);
                if (sys != null)
                {
                    Console.WriteLine($"[FontManager] Using system font: '{key}'");
                    Cache[key] = sys;
                    return sys;
                }
                else
                {
                    Console.WriteLine($"[FontManager] SKTypeface.FromFamilyName returned null for '{key}'");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FontManager] Exception while loading system font '{key}': {ex.Message}");
            }

            // 3) Fallback
            var fallback = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
            Console.WriteLine($"[FontManager] Using fallback font: 'Arial'");
            Cache[key] = fallback;
            return fallback;
        }
    }
}
