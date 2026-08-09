using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GlassCoder.Tools.Changes;
using GlassCoder.Tools.Verification;

namespace GlassCoder.Tools.FileSystem;

/// <summary>
/// What each file looked like the last time this run read it (workplan task 70).
/// <para>
/// A re-read is not free and it is not always useless - but a re-read of a file that has not
/// changed since the last one tells the model exactly what it already had, and that is the shape
/// of a stall rather than a step. Runs <c>122e11c6</c> and <c>d5edbc59</c> each spent a step
/// re-reading a file they had read correctly, because the refusal they were answering said
/// "not found" and re-reading is the obvious response to not-found.
/// </para>
/// <para>
/// Saying so does not refuse anything. The read returns exactly what it would have returned; one
/// clause is added to the summary. The point is that the model can tell "I have new information"
/// from "I have the same information again", which is a distinction it cannot otherwise make.
/// </para>
/// <para>
/// Keyed by run, on the <see cref="VerificationRefusalTracker"/> pattern, so one run's reads are
/// never charged to the next.
/// </para>
/// </summary>
public sealed class FileReadMemo
{
    private readonly ConcurrentDictionary<string, string> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that the file was read, and says whether its content is the same as last time.
    /// </summary>
    /// <param name="fullPath">The file, fully resolved.</param>
    /// <param name="content">What was read - the whole file, not the returned window.</param>
    /// <returns>True when this run has read this file before and it has not changed since.</returns>
    public bool RecordRead(string fullPath, string content)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(content);

        string key = $"{RunContext.Current.RunId}|{fullPath}";
        string digest = Digest(content);

        bool unchanged = _seen.TryGetValue(key, out string? previous) &&
                         string.Equals(previous, digest, StringComparison.Ordinal);

        _seen[key] = digest;
        return unchanged;
    }

    // No Forget, deliberately: the comparison is on content, so a write changes the digest and the
    // next read counts as new without anything having to remember to say so. An invalidation call
    // the write tools had to make would be one more thing to leave out of a new write path.
    //
    // The content itself is not kept either: a run reading twenty files would hold twenty file
    // bodies for the length of the run to answer one yes-or-no question.
    private static string Digest(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
