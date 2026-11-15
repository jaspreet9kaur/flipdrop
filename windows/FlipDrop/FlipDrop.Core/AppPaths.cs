namespace FlipDrop.Core;

/// <summary>
/// Resolves all on-disk locations. The app data folder sits next to the
/// executable (a portable app: copy the folder, and it comes along).
/// </summary>
public static class AppPaths
{
    /// <summary>Folder that holds the running executable (publish folder, or bin during dev/tests).</summary>
    public static string ExeDir { get; } = AppContext.BaseDirectory;

    /// <summary>App data: cert PFX, paired devices, settings. Next to the exe.</summary>
    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    /// <summary>Default folder incoming photos are written to (configurable in settings).</summary>
    public static string DefaultReceiveDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FlipDrop");

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string CertFile => Path.Combine(DataDir, "flipdrop.pfx");
    public static string DevicesFile => Path.Combine(DataDir, "devices.dat");

    public static void EnsureDataDir() => Directory.CreateDirectory(DataDir);
}
