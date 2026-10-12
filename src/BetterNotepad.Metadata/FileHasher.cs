using System.Security.Cryptography;

namespace BetterNotepad.Metadata;

public static class FileHasher
{
    private const int BufferSize = 1 << 20;

    private static HashAlgorithmName Name(HashAlgorithmKind k) => k switch
    {
        HashAlgorithmKind.Sha256 => HashAlgorithmName.SHA256,
        HashAlgorithmKind.Md5 => HashAlgorithmName.MD5,
        HashAlgorithmKind.Sha1 => HashAlgorithmName.SHA1,
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    /// <summary>Streaming hash of one file; lowercase hex. <paramref name="progress"/> receives 0..1.</summary>
    public static async Task<string> HashAsync(string path, HashAlgorithmKind algorithm, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var all = await HashAsync(path, new[] { algorithm }, progress, ct).ConfigureAwait(false);
        return all[algorithm];
    }

    /// <summary>One pass over the file for several algorithms.</summary>
    public static async Task<IReadOnlyDictionary<HashAlgorithmKind, string>> HashAsync(
        string path, IReadOnlyCollection<HashAlgorithmKind> algorithms, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (algorithms.Count == 0) throw new ArgumentException("No algorithm.", nameof(algorithms));
        var hashers = algorithms.Distinct().ToDictionary(a => a, a => IncrementalHash.CreateHash(Name(a)));
        try
        {
            FileStream fs;
            try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (FileNotFoundException ex) { throw new MetadataException(MetadataErrorCode.NotFound, ex.Message, ex); }
            catch (DirectoryNotFoundException ex) { throw new MetadataException(MetadataErrorCode.NotFound, ex.Message, ex); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new MetadataException(MetadataErrorCode.Io, ex.Message, ex); }

            await using (fs.ConfigureAwait(false))
            {
                long total = fs.Length, done = 0;
                var buf = new byte[BufferSize];
                int lastPct = -1;
                progress?.Report(0);
                int n;
                while ((n = await fs.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false)) > 0)
                {
                    foreach (var h in hashers.Values) h.AppendData(buf, 0, n);
                    done += n;
                    if (progress != null && total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct != lastPct) { lastPct = pct; progress.Report(done / (double)total); }
                    }
                }
                progress?.Report(1);
            }
            return hashers.ToDictionary(kv => kv.Key, kv => Convert.ToHexString(kv.Value.GetHashAndReset()).ToLowerInvariant());
        }
        finally { foreach (var h in hashers.Values) h.Dispose(); }
    }
}
