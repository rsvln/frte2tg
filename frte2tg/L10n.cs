using Newtonsoft.Json;

namespace frte2tg
{
    // Strings of one language, loaded from locales/<locale>.json next to the app; keys missing in it fall back
    // to en.json, and a key missing everywhere is returned as is.
    public class Strings
    {
        readonly Dictionary<string, string> strings;
        readonly Dictionary<string, string> fallback;

        public string Locale { get; }

        public Strings(string locale, Dictionary<string, string> strings, Dictionary<string, string> fallback)
        {
            Locale = locale;
            this.strings = strings;
            this.fallback = fallback;
        }

        public string T(string key, params object[] args)
        {
            string s = strings.TryGetValue(key, out var v) ? v : fallback.TryGetValue(key, out var f) ? f : key;
            return args.Length == 0 ? s : string.Format(s, args);
        }

        public bool Has(string key) => strings.ContainsKey(key) || fallback.ContainsKey(key);

        // Display name of a Frigate label ("person" -> "person" / "человек"); unknown labels are shown as is.
        public string Label(string label)
        {
            string key = "label." + label;
            return strings.TryGetValue(key, out var v) ? v : fallback.TryGetValue(key, out var f) ? f : label;
        }

        // All strings with the given prefixes, fallback included, for the web page.
        public Dictionary<string, string> Export(params string[] prefixes)
        {
            var result = new Dictionary<string, string>();
            foreach (var kv in fallback.Concat(strings))
                if (prefixes.Any(p => kv.Key.StartsWith(p)))
                    result[kv.Key] = kv.Value;
            return result;
        }
    }

    // The three languages of the app: web UI, Telegram, and AI prompts / replies (options.weblocale,
    // telegramlocale, ailocale; each defaults to options.locale).
    public static class L10n
    {
        public const string DefaultLocale = "en";

        static readonly Strings Empty = new Strings(DefaultLocale, new Dictionary<string, string>(), new Dictionary<string, string>());
        public static Strings Web { get; private set; } = Empty;
        public static Strings Tg { get; private set; } = Empty;
        public static Strings Ai { get; private set; } = Empty;

        // Object names from every locale file, so "/last человек" works whatever the locale is.
        static Dictionary<string, string> labelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string LocalesDir => Path.Combine(Program.appLocation, "locales");

        public static void Load(string web, string tg, string ai)
        {
            var fallback = ReadFile(DefaultLocale) ?? new Dictionary<string, string>();
            Web = Make(web, fallback);
            Tg = Make(tg, fallback);
            Ai = Make(ai, fallback);

            var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(LocalesDir))
                foreach (var file in Directory.GetFiles(LocalesDir, "*.json"))
                    foreach (var kv in ReadFile(Path.GetFileNameWithoutExtension(file)) ?? new Dictionary<string, string>())
                        if (kv.Key.StartsWith("label."))
                            aliases.TryAdd(kv.Value, kv.Key.Substring("label.".Length));
            labelAliases = aliases;
        }

        static Strings Make(string locale, Dictionary<string, string> fallback)
        {
            locale = string.IsNullOrWhiteSpace(locale) ? DefaultLocale : locale.Trim().ToLower();
            var selected = locale == DefaultLocale ? fallback : ReadFile(locale);
            if (selected == null)
            {
                Program.Log("app", "", "", "Locale '" + locale + "' not found in " + LocalesDir + ", using " + DefaultLocale);
                return new Strings(DefaultLocale, fallback, fallback);
            }
            return new Strings(locale, selected, fallback);
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

        // Frigate label for a display name typed by a user in any available language ("человек" -> "person").
        public static string LabelFromName(string name) => labelAliases.TryGetValue(name, out var label) ? label : null;
    }
}
