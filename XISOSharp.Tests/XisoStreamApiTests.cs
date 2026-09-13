using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Tests for the public stream-based read API (TODO #12, third bullet):
/// <see cref="XisoReader.OpenImageStream(string)"/>, the <c>Stream</c> overloads of
/// <c>Extract</c>/<c>UnpackImage</c>/<c>List</c>/<c>Tree</c>/<c>DecodeXiso</c>,
/// and <see cref="XisoReader.IsOptimizedImage(Stream, int?)"/>.
/// </summary>
[Collection("Sequential")]
public class XisoStreamApiTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly StringWriter _logCapture = new();
    private readonly TextWriter _savedOut;
    private readonly string _savedCwd;

    public XisoStreamApiTests()
    {
        _savedOut = Logger.Out;
        Logger.Out = _logCapture;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        _savedCwd = Directory.GetCurrentDirectory();
    }

    public void Dispose()
    {
        try
        {
            Directory.SetCurrentDirectory(_savedCwd);
        }
        catch
        {
            // ignored
        }

        Logger.Out = _savedOut;
        Logger.Quiet = false;
        Logger.RealQuiet = false;
        _logCapture.Dispose();

        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private string CreateIso()
    {
        string src = CreateTempDir("xiso_stream_src");
        Directory.CreateDirectory(Path.Combine(src, "sub"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), new string('B', 5000));

        string isoDir = CreateTempDir("xiso_stream_iso");
        int result = XisoWriter.CreateXiso(src, isoDir, null, null, out string? created, "game.iso", null);
        Assert.Equal(0, result);
        return created!;
    }

    private static Dictionary<string, string> HashTree(string root)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using SHA256 sha = SHA256.Create();
            using FileStream fs = File.OpenRead(file);
            result[rel] = Convert.ToHexString(sha.ComputeHash(fs));
        }

        return result;
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        private readonly Stream _inner = inner;
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void OpenImageStream_Iso_ReturnsReadableFileView()
    {
        string isoPath = CreateIso();

        using Stream s = XisoReader.OpenImageStream(isoPath);

        Assert.True(s.CanRead);
        Assert.True(s.CanSeek);
        Assert.Equal(new FileInfo(isoPath).Length, s.Length);
    }

    [Fact]
    public void UnpackImage_FromMemoryStream_MatchesFileExtract()
    {
        string isoPath = CreateIso();
        byte[] bytes = File.ReadAllBytes(isoPath);
        using MemoryStream ms = new(bytes, writable: false);
        string dest = CreateTempDir("xiso_stream_dest");

        int rc = XisoReader.UnpackImage(ms, "game.iso", dest);

        Assert.Equal(0, rc);
        Assert.True(ms.CanRead);
        string control = CreateTempDir("xiso_stream_control");
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(HashTree(control), HashTree(dest));
    }

    [Fact]
    public void Extract_FromFileStream_LeavesStreamOpen()
    {
        string isoPath = CreateIso();
        using FileStream fs = File.OpenRead(isoPath);
        string dest = CreateTempDir("xiso_stream_dest2");

        int rc = XisoReader.Extract(fs, "game.iso", dest, !XisoReader.IsOptimizedImage(isoPath));

        Assert.Equal(0, rc);
        Assert.True(fs.CanRead);
        string control = CreateTempDir("xiso_stream_control2");
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(HashTree(control), HashTree(dest));
    }

    [Fact]
    public void List_Tree_FromStream_ReturnZero()
    {
        string isoPath = CreateIso();
        using MemoryStream ms = new(File.ReadAllBytes(isoPath), writable: false);

        Assert.Equal(0, XisoReader.List(ms, "game.iso", !XisoReader.IsOptimizedImage(isoPath)));
        Assert.Equal(0, XisoReader.Tree(ms, "game.iso", !XisoReader.IsOptimizedImage(isoPath)));
    }

    [Fact]
    public async Task DecodeXisoAsync_FromStream_Extracts()
    {
        string isoPath = CreateIso();
        await using MemoryStream ms = new(await File.ReadAllBytesAsync(isoPath), writable: false);
        string dest = CreateTempDir("xiso_stream_dest3");

        (int result, string? outIso) = await XisoReader.DecodeXisoAsync(
            ms, "game.iso", dest, ExtractMode.Extract,
            llCompat: !XisoReader.IsOptimizedImage(isoPath));

        Assert.Equal(0, result);
        Assert.Null(outIso);
        string control = CreateTempDir("xiso_stream_control3");
        Assert.Equal(0, XisoReader.UnpackImage(isoPath, control));
        Assert.Equal(HashTree(control), HashTree(dest));
    }

    [Fact]
    public void IsOptimizedImage_Stream_MatchesPathProbe()
    {
        string isoPath = CreateIso();
        using MemoryStream ms = new(File.ReadAllBytes(isoPath), writable: false);
        long pos = ms.Position;

        Assert.Equal(XisoReader.IsOptimizedImage(isoPath), XisoReader.IsOptimizedImage(ms));
        Assert.Equal(pos, ms.Position);
    }

    [Fact]
    public void DecodeXiso_NullStream_ThrowsArgumentNullException() =>
        Assert.Throws<ArgumentNullException>(() =>
            XisoReader.DecodeXiso(null!, "game.iso", null, ExtractMode.List, out _, false));

    [Fact]
    public void DecodeXiso_NonSeekableStream_ThrowsArgumentException()
    {
        string isoPath = CreateIso();
        using FileStream inner = File.OpenRead(isoPath);
        using NonSeekableStream ns = new(inner);

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            XisoReader.DecodeXiso(ns, "game.iso", null, ExtractMode.List, out _, false));
        Assert.Contains("seekable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeXiso_RewriteModeFromStream_Refused()
    {
        string isoPath = CreateIso();
        using MemoryStream ms = new(File.ReadAllBytes(isoPath), writable: false);

        Assert.Throws<ArgumentException>(() =>
            XisoReader.DecodeXiso(ms, "game.iso", null, ExtractMode.Rewrite, out _, false));
    }
}
