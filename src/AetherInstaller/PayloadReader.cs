using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AetherInstaller;

public sealed class PayloadInfo
{
    public long Offset { get; init; }
    public long Length { get; init; }
}

public static class PayloadReader
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("AETHERPK");
    private const int FooterSize = 24; // 8 (offset) + 8 (length) + 8 (magic)

    public static PayloadInfo? TryReadFooter(string exePath)
    {
        if (!File.Exists(exePath)) return null;
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < FooterSize) return null;

        fs.Seek(-FooterSize, SeekOrigin.End);
        Span<byte> footer = stackalloc byte[FooterSize];
        if (fs.Read(footer) != FooterSize) return null;

        if (!footer[16..24].SequenceEqual(Magic)) return null;

        var offset = BinaryPrimitives.ReadInt64LittleEndian(footer[0..8]);
        var length = BinaryPrimitives.ReadInt64LittleEndian(footer[8..16]);

        if (offset <= 0 || length <= 0 || offset + length + FooterSize != fs.Length)
            return null;

        return new PayloadInfo { Offset = offset, Length = length };
    }

    public static long EstimateUncompressedBytes(string exePath, PayloadInfo info)
    {
        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sub = new SubStream(fs, info.Offset, info.Length);
        using var zip = new ZipArchive(sub, ZipArchiveMode.Read, leaveOpen: false);
        long total = 0;
        foreach (var e in zip.Entries) total += e.Length;
        return total;
    }

    public static void ExtractPayload(
        string exePath,
        PayloadInfo info,
        string destinationDir,
        Action<int, string>? onProgress = null)
    {
        Directory.CreateDirectory(destinationDir);

        using var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sub = new SubStream(fs, info.Offset, info.Length);
        using var zip = new ZipArchive(sub, ZipArchiveMode.Read, leaveOpen: false);

        var fileEntries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var totalFiles = Math.Max(1, fileEntries.Count);

        for (int i = 0; i < fileEntries.Count; i++)
        {
            var entry = fileEntries[i];
            var relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (relPath.Contains("..")) continue;

            var destPath = Path.Combine(destinationDir, relPath);
            var parent = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            // Retry briefly in case an old subprocess just released its file handle
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    entry.ExtractToFile(destPath, overwrite: true);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(250);
                }
            }

            var pct = (int)((i + 1) * 86.0 / totalFiles);
            onProgress?.Invoke(pct, $"Extracting {entry.Name} ({i + 1}/{totalFiles})...");
        }
    }

    public static void WriteUninstallerStub(string exePath, PayloadInfo info, string uninstallerDestPath)
    {
        using var src = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var dst = new FileStream(uninstallerDestPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var remaining = info.Offset;
        var buf = new byte[65536];
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buf.Length, remaining);
            var n = src.Read(buf, 0, toRead);
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            remaining -= n;
        }
    }

    private sealed class SubStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _offset;
        private readonly long _length;
        private long _pos;

        public SubStream(Stream inner, long offset, long length)
        {
            _inner = inner;
            _offset = offset;
            _length = length;
            _inner.Position = _offset;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _pos;
            set
            {
                _pos = Math.Clamp(value, 0, _length);
                _inner.Position = _offset + _pos;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var avail = _length - _pos;
            if (avail <= 0) return 0;
            _inner.Position = _offset + _pos;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, avail));
            _pos += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _length + offset,
                _ => _pos
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
