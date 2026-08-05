using System.Text.Json.Nodes;

namespace GlassCoder.Core.Configuration;

/// <summary>
/// The settings that belong to a repository rather than to a machine (CLAUDE.md §13).
/// <para>
/// <c>Workspace:ReadablePaths</c>, <c>Workspace:WritablePaths</c>, <c>Context:RootContextFiles</c>
/// and <c>Git:PushableBranches</c> all name things <em>inside</em> a project, so one saved copy of
/// them is right for exactly one project. Switching the workspace root used to leave the previous
/// project's guardrails in force over the new one - a silent and dangerous default, because the
/// writable set is the one setting where being wrong means writing somewhere nobody agreed to.
/// </para>
/// <para>
/// So those sections can also live in the project, in a file the project carries. It sits above
/// the per-user settings and below environment variables and <c>--config</c>, the same band saved
/// settings have always occupied, so an ablation arm still means the same thing everywhere.
/// </para>
/// </summary>
public interface IProjectSettingsStore
{
    /// <summary>Name of the file inside a project. Visible, and meant to be committed.</summary>
    string FileName { get; }

    /// <summary>Where the file for <paramref name="projectRoot"/> would be.</summary>
    string FilePathFor(string projectRoot);

    /// <summary>Whether <paramref name="projectRoot"/> carries one.</summary>
    bool ExistsIn(string projectRoot);

    /// <summary>
    /// Writes the project-shaped sections of <paramref name="settings"/> into
    /// <paramref name="projectRoot"/>, and returns the path written.
    /// </summary>
    string Save(GlassCoderSettings settings, string projectRoot);

    /// <summary>Removes the file, falling back to whatever the per-user settings say.</summary>
    void Delete(string projectRoot);
}

/// <summary>The default <see cref="IProjectSettingsStore"/>: one JSON file in the project root.</summary>
public sealed class ProjectSettingsStore : IProjectSettingsStore
{
    /// <summary>
    /// Name of the project file. Not dot-prefixed-and-hidden by accident: it is a file the team
    /// is meant to see, review and commit, in the way <c>.editorconfig</c> is.
    /// </summary>
    public const string ProjectFileName = ".glasscoder.json";

    /// <inheritdoc />
    public string FileName => ProjectFileName;

    /// <inheritdoc />
    public string FilePathFor(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(Path.GetFullPath(projectRoot), ProjectFileName);
    }

    /// <inheritdoc />
    public bool ExistsIn(string projectRoot) => File.Exists(FilePathFor(projectRoot));

    /// <inheritdoc />
    public string Save(GlassCoderSettings settings, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string path = FilePathFor(projectRoot);

        JsonObject document = SettingsDocument.Serialize(settings);

        // No key ever reaches this file, and not because none was set: a project file is one
        // `git add` away from being public, so the removal is unconditional rather than a policy
        // some caller could get wrong.
        SettingsDocument.LiftApiKeys(settings, document, protector: null);
        SettingsDocument.KeepOnly(document, SettingsDocument.ProjectSectionNames);
        RemoveRepoRoot(document);

        SettingsDocument.WriteAtomically(path, document.ToJsonString(SettingsDocument.FileJson));
        return path;
    }

    /// <inheritdoc />
    public void Delete(string projectRoot) => File.Delete(FilePathFor(projectRoot));

    /// <summary>
    /// Drops <c>Workspace:RepoRoot</c> on the way out.
    /// <para>
    /// The file's own location <em>is</em> the root, so writing an absolute path into it would
    /// only be a way for the file to be wrong after the project is moved, renamed or cloned by
    /// somebody else. The configuration layer supplies the containing directory instead.
    /// </para>
    /// </summary>
    private static void RemoveRepoRoot(JsonObject document) =>
        (document[GlassCoderSettings.RootSectionName]?[nameof(GlassCoderSettings.Workspace)] as JsonObject)
            ?.Remove(nameof(GlassCoder.Tools.Guardrails.WorkspaceOptions.RepoRoot));
}
