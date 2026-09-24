using System.Reflection;

namespace frte2tg
{
    // App version from the assembly attributes set by Directory.Build.props (version.txt + build date).
    public static class VersionInfo
    {
        // "1.2.3+2026.09.24"
        public static string Informational { get; } =
            typeof(VersionInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // "1.2.3"
        public static string Version => Informational.Split('+')[0];

        // "2026-09-24", or empty when the build date is unknown
        public static string BuildDate => Informational.Contains('+') ? Informational.Split('+')[1].Replace('.', '-') : "";

        public const string ProjectUrl = "https://github.com/rsvln/frte2tg";
    }
}
