using System.Collections.Concurrent;
using GlassCoder.Tools.Changes;

namespace GlassCoder.Tools.Verification;

/// <summary>
/// Consecutive identical pre-write refusals, counted per file and per run.
/// <para>
/// The pre-write compile is approximate by construction, and when its picture is wrong in a way
/// its own inconclusive checks do not catch, it refuses the same correct content with the same
/// errors forever. Run 5c071f37 is the shape of that failure: ten refusals of one WPF
/// code-behind, each answered with a fresh variation, each refused identically, until the token
/// limit - because the gate could not see the XAML-generated partial and had no way to learn.
/// The write tools consult this tracker so that an argument the gate keeps losing the same way
/// is eventually conceded to the authoritative gate, the build.
/// </para>
/// <para>
/// "Identical" means the same file drawing the same set of introduced diagnostics, by
/// fingerprint. A different set - the model actually fixed something, or broke something new -
/// restarts the count, and a verification that lets a write through wipes the slate for that
/// file. Counts are keyed by run so one run's stuck argument is never charged to the next.
/// </para>
/// </summary>
public sealed class VerificationRefusalTracker
{
    private readonly ConcurrentDictionary<string, Entry> _refusals = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one refusal and returns how many times in a row this file has now drawn exactly
    /// this one.
    /// </summary>
    public int RecordRefusal(string fullPath, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        ArgumentNullException.ThrowIfNull(fingerprint);

        return _refusals.AddOrUpdate(
            Key(fullPath),
            _ => new Entry(fingerprint, 1),
            (_, existing) => string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? existing with { Count = existing.Count + 1 }
                : new Entry(fingerprint, 1)).Count;
    }

    /// <summary>Wipes a file's slate - verification passed, stood aside, or was overridden.</summary>
    public void Forget(string fullPath)
    {
        ArgumentNullException.ThrowIfNull(fullPath);
        _refusals.TryRemove(Key(fullPath), out _);
    }

    /// <summary>
    /// One order-independent string for a set of diagnostics, so "the same refusal" survives the
    /// compiler reporting the same errors in a different order.
    /// </summary>
    public static string FingerprintOf(IEnumerable<CodeDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return string.Join(
            "\n",
            diagnostics.Select(d => $"{d.Id}|{d.FilePath}|{d.Message}").Order(StringComparer.Ordinal));
    }

    private static string Key(string fullPath) => $"{RunContext.Current.RunId}|{fullPath}";

    private sealed record Entry(string Fingerprint, int Count);
}
