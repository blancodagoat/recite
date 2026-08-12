namespace Recite;

internal static class AppInfo
{
    public const string Name = "Recite";
    public const string GitHubUrl = "https://github.com/blancodagoat/recite";

    /// <summary>
    /// Full path of the running executable. ProcessPath is the only reliable source under
    /// single-file publish, where Assembly.Location is empty.
    /// </summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Name + ".exe");

    /// <summary>%APPDATA%\Recite — config.json and the log live here.</summary>
    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
}
