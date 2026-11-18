using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Presenter.Core;

namespace Presenter.Slides;

/// <summary>
/// Rendered slides on disk, a folder per key under the cache folder
/// (research 05 §7.3). The key is a SHA-256 over what decides the pixels:
/// the deck's files, the renderer and its version, and the slide size. A
/// folder is a cache entry once its <c>complete</c> marker exists; the
/// marker's time is its last use, and the least recently used entries beyond
/// the limit are removed. Only folders named like keys are ever deleted.
/// </summary>
internal static partial class SlideCache
{
    /// <summary>Bumped when the layout or the processing of cached slides changes.</summary>
    private const string Format = "paint-presentator-slides-v1";
    public const string Marker = "complete";

    public static string Key(DeckKind kind, string renderer, PixelSize size, IEnumerable<string> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
        Add(Format);
        Add(kind.ToString());
        Add(renderer);
        Add(size.ToString());
        var buffer = new byte[1 << 16];
        foreach (var f in files)
        {
            Add(Path.GetFileName(f).ToLowerInvariant());
            using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                buffer.Length, FileOptions.SequentialScan);
            Add(fs.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            int n;
            while ((n = fs.Read(buffer, 0, buffer.Length)) > 0) sha.AppendData(buffer, 0, n);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset())[..32];
    }

    public static string SlideFile(string entryDir, int slide) => Path.Combine(entryDir, $"slide-{slide:000}.png");

    internal sealed record MarkerData(int Total, string Kind, string Size, string Renderer);

    /// <summary>The slide count of a complete entry whose slides are all
    /// there, or null. A hit counts as a use (for least-recently-used).</summary>
    public static int? TryHit(string entryDir)
    {
        var marker = Path.Combine(entryDir, Marker);
        if (!File.Exists(marker)) return null;
        try
        {
            var data = JsonSerializer.Deserialize<MarkerData>(File.ReadAllText(marker));
            if (data is null || data.Total < 1) return null;
            for (int i = 1; i <= data.Total; i++)
                if (!File.Exists(SlideFile(entryDir, i))) return null;
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
            return data.Total;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void MarkComplete(string entryDir, MarkerData data)
    {
        var marker = Path.Combine(entryDir, Marker);
        File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(data));
        File.Move(marker + ".tmp", marker, overwrite: true);
    }

    /// <summary>
    /// Keeps the <paramref name="keep"/> most recently used complete entries
    /// and removes the rest, and removes unfinished entries older than a day
    /// (a render that died). Entries in use (locked) are left alone.
    /// </summary>
    public static void Evict(string cacheDir, int keep, string? current = null)
    {
        if (!Directory.Exists(cacheDir)) return;
        var complete = new List<(DirectoryInfo Dir, DateTime Used)>();
        foreach (var dir in new DirectoryInfo(cacheDir).EnumerateDirectories())
        {
            if (!EntryName().IsMatch(dir.Name)) continue; // not ours: never touched
            if (current is not null && Path.GetFullPath(dir.FullName).Equals(Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
                continue;
            var marker = new FileInfo(Path.Combine(dir.FullName, Marker));
            if (marker.Exists) complete.Add((dir, marker.LastWriteTimeUtc));
            else if (dir.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) TryDelete(dir);
        }
        foreach (var (dir, _) in complete.OrderByDescending(e => e.Used).Skip(Math.Max(0, keep - (current is null ? 0 : 1))))
            TryDelete(dir);
    }

    private static void TryDelete(DirectoryInfo dir)
    {
        // an entry being rendered holds its lock file open: deleting fails, and it stays
        var lockFile = Path.Combine(dir.FullName, EntryLock.FileName);
        try
        {
            if (File.Exists(lockFile)) File.Delete(lockFile);
            dir.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[0-9a-f]{32}(~[0-9]+)?$")]
    private static partial Regex EntryName();
}

/// <summary>An exclusive lock on a cache entry while it is rendered.</summary>
internal sealed class EntryLock : IDisposable
{
    public const string FileName = ".lock";
    private readonly FileStream _stream;

    private EntryLock(FileStream stream) => _stream = stream;

    public static EntryLock? TryTake(string entryDir)
    {
        Directory.CreateDirectory(entryDir);
        try
        {
            return new EntryLock(new FileStream(Path.Combine(entryDir, FileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
