using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassCoder.Tools.Retrieval;

/// <summary>
/// What identifies one exchange with an upstream, and therefore what a replay has to match.
/// </summary>
/// <param name="Server">Which upstream answered.</param>
/// <param name="ServerTool">The server's own tool name, not ours - renaming a tool locally must
/// not invalidate a corpus recorded before the rename.</param>
/// <param name="Arguments">The call's arguments, normalised.</param>
public sealed record RetrievalCacheKey(RetrievalServer Server, string ServerTool, string Arguments)
{
    /// <summary>
    /// Builds a key from raw arguments, normalised so that trivially different spellings of one
    /// question are one question: properties ordered, whitespace collapsed, case folded.
    /// <para>
    /// Normalisation is what makes a corpus usable at all. Without it a model that asks for
    /// "IAsyncEnumerable" and "iasyncenumerable " in two runs misses twice on one recording,
    /// and a Replay arm fails for a reason that has nothing to do with the experiment.
    /// </para>
    /// </summary>
    public static RetrievalCacheKey From(
        RetrievalServer server, string serverTool, IReadOnlyDictionary<string, object?>? arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverTool);

        if (arguments is null || arguments.Count == 0)
        {
            return new RetrievalCacheKey(server, serverTool, string.Empty);
        }

        // Unit separator: a control character no argument value can contain, so two arguments
        // cannot collide by one ending where the next begins.
        const char separator = '\u001f';

        StringBuilder builder = new();
        foreach ((string name, object? value) in arguments.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            builder.Append(name.ToLowerInvariant()).Append('=').Append(Normalize(value)).Append(separator);
        }

        return new RetrievalCacheKey(server, serverTool, builder.ToString());
    }

    /// <summary>A stable file-safe name for this key.</summary>
    public string Digest()
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Server}{ServerTool}{Arguments}"));

        return Convert.ToHexStringLower(hash)[..32];
    }

    private static string Normalize(object? value)
    {
        string text = value switch
        {
            null => string.Empty,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            JsonElement element => element.GetRawText(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
    }
}

/// <summary>One recorded exchange, as it sits on disk.</summary>
/// <param name="Server">Which upstream answered.</param>
/// <param name="ServerTool">The server's tool name.</param>
/// <param name="Arguments">The normalised arguments this answer belongs to.</param>
/// <param name="Payload">What the server returned, already truncated to the run's cap.</param>
/// <param name="RecordedAt">When it was recorded, so a stale corpus is visible rather than assumed.</param>
public sealed record RetrievalCacheEntry(
    RetrievalServer Server,
    string ServerTool,
    string Arguments,
    string Payload,
    DateTimeOffset RecordedAt);

/// <summary>
/// The record/replay corpus (workplan task 56).
/// <para>
/// Built before the client on purpose: the client's only route upstream goes through this, so
/// there is never a version of the code in which a network call can happen outside it.
/// </para>
/// </summary>
public interface IRetrievalCache
{
    /// <summary>The recorded answer for this call, or null when there is none.</summary>
    RetrievalCacheEntry? Get(RetrievalCacheKey key);

    /// <summary>Records an answer, replacing any earlier one for the same key.</summary>
    void Put(RetrievalCacheKey key, string payload);
}

/// <summary>
/// Disk-backed corpus, one JSON file per key under a per-server folder.
/// <para>
/// Files rather than a database because the corpus is an artifact people read, diff and commit:
/// "why did this arm answer that" should be answerable with a text editor, and a recording that
/// cannot be inspected is a recording nobody trusts.
/// </para>
/// </summary>
public sealed class RetrievalCache : IRetrievalCache
{
    private static readonly JsonSerializerOptions Serializer = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;

    /// <summary>Creates a corpus rooted at <paramref name="directory"/>.</summary>
    public RetrievalCache(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _root = directory;
    }

    /// <inheritdoc />
    public RetrievalCacheEntry? Get(RetrievalCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RetrievalCacheEntry>(File.ReadAllText(path), Serializer);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable entry is a miss, not a crash. In Replay that surfaces as
            // retrieval_cache_miss, which is the loud failure the mode promises.
            return null;
        }
    }

    /// <inheritdoc />
    public void Put(RetrievalCacheKey key, string payload)
    {
        ArgumentNullException.ThrowIfNull(key);

        string path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        RetrievalCacheEntry entry = new(
            key.Server, key.ServerTool, key.Arguments, payload ?? string.Empty, DateTimeOffset.UtcNow);

        // Written beside and moved, so a corpus is never half a file: an interrupted Record run
        // must not leave an entry that Replay reads as a truncated answer.
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entry, Serializer));
        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(RetrievalCacheKey key) =>
        Path.Combine(_root, key.Server.ToString().ToLowerInvariant(), $"{key.Digest()}.json");
}
