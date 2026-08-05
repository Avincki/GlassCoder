namespace GlassCoder.Core.Configuration;

/// <summary>
/// Anchors the harness's own output - logs, metrics - to a stable per-user directory.
/// <para>
/// Configuration names these directories relatively (<c>"logs"</c>, <c>"metrics"</c>).
/// Resolving that against the working directory scatters the files by however the process
/// happened to be launched: Visual Studio puts them under <c>bin</c> where a
/// <c>dotnet clean</c> erases the run history, a terminal at the repository root puts them
/// in a folder the repository deliberately ignores. Relative paths therefore anchor here,
/// under local (non-roaming) application data - transcripts are machine-local history, not
/// something to sync between machines. Absolute paths in configuration are honoured as-is.
/// </para>
/// </summary>
public static class AppPaths
{
    /// <summary>Overrides the data root. Set it to make a portable or test install.</summary>
    /// <remarks>
    /// The sibling of <see cref="UserSettingsStore.DirectoryEnvironmentVariable"/>: that one
    /// moves the settings, this one moves the generated data.
    /// </remarks>
    public const string DataDirectoryEnvironmentVariable = "GLASSCODER_DATA_DIR";

    /// <summary>
    /// Absolute directory for <paramref name="configured"/>: absolute paths pass through
    /// unchanged, relative ones resolve against the data root rather than the working
    /// directory.
    /// </summary>
    public static string ResolveDataDirectory(string configured)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configured);

        return Path.GetFullPath(configured, DataRoot());
    }

    private static string DataRoot()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Path.GetFullPath(fromEnvironment);
        }

        string local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        return Path.Combine(local, "GlassCoder");
    }
}
