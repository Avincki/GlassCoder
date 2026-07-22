using GlassCoder.Tools.Guardrails;
using Microsoft.Extensions.Options;

namespace GlassCoder.TestSupport;

/// <summary>
/// A throwaway directory tree that stands in for a repository, with a matching
/// <see cref="PathGuard"/>. Removed on dispose.
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    /// <summary>Creates the directory tree.</summary>
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "glasscoder-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute path of the workspace root.</summary>
    public string Root { get; }

    /// <summary>Writes a file, creating directories as needed, and returns its full path.</summary>
    public string WriteFile(string relativePath, string content)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Creates a directory and returns its full path.</summary>
    public string CreateDirectory(string relativePath)
    {
        string full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Builds workspace options rooted here.</summary>
    public WorkspaceOptions Options(params string[] writablePaths)
    {
        WorkspaceOptions options = new() { RepoRoot = Root };
        foreach (string writable in writablePaths)
        {
            options.WritablePaths.Add(writable);
        }

        return options;
    }

    /// <summary>Builds a guard rooted here.</summary>
    public PathGuard Guard(params string[] writablePaths) => new(Microsoft.Extensions.Options.Options.Create(Options(writablePaths)));

    /// <summary>Wraps any options object for constructor injection.</summary>
    public static IOptions<T> Wrap<T>(T value) where T : class => Microsoft.Extensions.Options.Options.Create(value);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test-only temp directory that will not delete is not worth failing a test over.
        }
    }
}
