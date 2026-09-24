using Newtonsoft.Json;

namespace frte2tg
{
    // UI strings for Telegram and the web UI. Loaded from locales/<locale>.json next to the app;
    // keys missing in the selected locale fall back to en.json, and a key missing everywhere is returned as is.
    public static class L10n
    {
        public const string DefaultLocale = "en";

        static Dictionary<string, string> strings = new Dictionary<string, string>();
        static Dictionary<string, string> fallback = new Dictionary<string, string>();
        // Object names from every locale file, so "/last человек" works whatever the locale is.
        static Dictionary<string, string> labelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string Locale { get; private set; } = DefaultLocale;

        public static string LocalesDir => Path.Combine(Program.appLocation, "locales");

        public static void Load(string locale)
        {
            locale = string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale.Trim().ToLower();
            fallback = ReadFile(DefaultLocale) ?? new Dictionary<string, string>();
            var selected = locale == DefaultLocale ? fallback : ReadFile(locale);
            if (selected == null)
            {
                Program.Log("app", "", "", "Locale '" + locale + "' not found in " + LocalesDir + ", using " + DefaultLocale);
                selected = fallback;
                locale = DefaultLocale;
            }
            strings = selected;
            Locale = locale;

            labelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(LocalesDir))
                foreach (var file in Directory.GetFiles(LocalesDir, "*.json"))
                    foreach (var kv in ReadFile(Path.GetFileNameWithoutExtension(file)) ?? new Dictionary<string, string>())
                        if (kv.Key.StartsWith("label."))
                            labelAliases.TryAdd(kv.Value, kv.Key.Substring("label.".Length));
        }

        static Dictionary<string, string> ReadFile(string locale)
        {
            string path = Path.Combine(LocalesDir, locale + ".json");
            if (!System.IO.File.Exists(path))
                return null;
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(System.IO.File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                Program.Log("app", "", "", "Failed to read locale file " + path + ": " + ex.Message);
                return null;
            }
        }

        public static string T(string key, params object[] args)
        {
            string s = strings.TryGetValue(key, out var v) ? v : fallback.TryGetValue(key, out var f) ? f : key;
            return args.Length == 0 ? s : string.Format(s, args);
        }

        // Display name of a Frigate label ("person" -> "person" / "человек"); unknown labels are shown as is.
        public static string Label(string label)
        {
            string key = "label." + label;
            return strings.TryGetValue(key, out var v) ? v : fallback.TryGetValue(key, out var f) ? f : label;
        }

        // Frigate label for a display name typed by a user in any available language ("человек" -> "person").
        public static string LabelFromName(string name) => labelAliases.TryGetValue(name, out var label) ? label : null;

        // All strings with the given prefixes, fallback included, for the web page.
        public static Dictionary<string, string> Export(params string[] prefixes)
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in fallback.Concat(strings))
                if (prefixes.Any(p => kv.Key.StartsWith(p)))
                    result[kv.Key] = kv.Value;
            return result;
        }
    }
}
