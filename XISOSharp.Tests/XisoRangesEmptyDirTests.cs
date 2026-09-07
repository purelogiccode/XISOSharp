using System.Buffers.Binary;
using System.Text;

namespace XISOSharp.Tests;

/// <summary>
/// Regression tests for <see cref="XisoRanges.GetFileEntries(FileStream, long)"/>:
/// an empty subdirectory is stored as an all-0xFF directory table whose first
/// entry parses as leftChild == 0xFFFF. The collector must treat that as an
/// empty-table sentinel (like <see cref="XisoRanges.GetValidSectors"/> does);
/// otherwise the 0xFF bytes decode into a garbage entry with an entry sector
/// near 4 billion, poisoning results and later throwing EndOfStreamException
/// in consumers (e.g. <c>XisoSkeleton.Petrify</c>).
/// </summary>
[Collection("Sequential")]
public sealed class XisoRangesEmptyDirTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (string f in _tempFiles)
        {
            if (File.Exists(f))
                File.Delete(f);
        }
    }

    /// <summary>
    /// Builds a minimal image: header at 0x10000 (root sector 1, size 32), a root
    /// table at sector 1 containing one directory entry "D" (data at sector 2),
    /// and an all-0xFF empty table at sector 2. File is padded so the old bug's
    /// 255-byte garbage name read would not EOF by itself.
    /// </summary>
    private string CreateImageWithEmptySubdirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"xiso_empty_dir_{Guid.NewGuid():N}.iso");
        const int tableBytes = 32;
        Span<byte> buf = stackalloc byte[tableBytes];

        using (FileStream fs = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            // Header: magic + root sector (1) + root size (32). The file must span
            // the optional second header sector (0x10800): GetXisoRanges probes it
            // unconditionally (XBOX_DVD_LAYOUT_TOOL_SIG is optional, the read is not).
            fs.SetLength(0x10800 + Constants.SectorSize);
            fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));
            Span<byte> intBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(intBuf, 1);
            fs.Write(intBuf);
            BinaryPrimitives.WriteUInt32LittleEndian(intBuf, tableBytes);
            fs.Write(intBuf);

            // Root table at sector 1: single entry, directory "D" -> sector 2, size 32.
            fs.Seek(Constants.SectorSize, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt16LittleEndian(buf, 0); // left
            BinaryPrimitives.WriteUInt16LittleEndian(buf.Slice(2), 0); // right
            BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(4), 2); // entry sector
            BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(8), tableBytes); // entry size
            buf[12] = Constants.AttributeDir; // attributes
            buf[13] = 1; // name length
            buf[14] = (byte)'D';
            fs.Write(buf);

            // "Directory table" of D at sector 2: all 0xFF (empty-directory sentinel).
            fs.Seek(2 * Constants.SectorSize, SeekOrigin.Begin);
            buf.Fill(0xFF);
            fs.Write(buf);
        }

        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void GetFileEntries_EmptySubdirectory_NoGarbageEntries()
    {
        string path = CreateImageWithEmptySubdirectory();
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(fs, 0);

        // "D" is a directory, so there are no regular files; the all-0xFF table
        // must not contribute an entry. The old bug produced one entry whose
        // offset was entrySector 0xFFFFFFFF * SectorSize (far beyond the file).
        Assert.Empty(entries);
    }

    [Fact]
    public void GetFileEntries_EmptySubdirectory_AllOffsetsInBounds()
    {
        string path = CreateImageWithEmptySubdirectory();
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        List<(string Path, long Offset, uint Size)> entries = XisoRanges.GetFileEntries(fs, 0);
        Assert.All(entries, e => Assert.InRange(e.Offset, 0, fs.Length));
    }

    [Fact]
    public void Petrify_ImageWithEmptySubdirectory_Succeeds()
    {
        string path = CreateImageWithEmptySubdirectory();
        string skel = Path.ChangeExtension(path, ".skeleton.xiso");
        _tempFiles.Add(skel);
        string hash = Path.ChangeExtension(path, ".hash");
        _tempFiles.Add(hash);

        Assert.True(XisoSkeleton.Petrify(path, skel, hash, 0, true));
        Assert.True(new FileInfo(skel).Length > 0);
    }
}
