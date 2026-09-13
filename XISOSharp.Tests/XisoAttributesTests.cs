namespace XISOSharp.Tests;

/// <summary>
/// <see cref="XisoAttributes.ToWindowsFileAttributes(byte)"/> (3.1): maps the
/// XDVDFS attribute byte exactly like the SimpleXisoDrive consumer's locked
/// <c>FileEntry.GetWindowsAttributes</c> — ReadOnly always set, standard flags
/// OR'd in, Normal only when nothing else applies, reserved bits ignored.
/// </summary>
public sealed class XisoAttributesTests
{
    [Theory]
    [InlineData(Constants.AttributeRo, FileAttributes.ReadOnly, true)]
    [InlineData(Constants.AttributeHid, FileAttributes.Hidden, false)]
    [InlineData(Constants.AttributeSys, FileAttributes.System, false)]
    [InlineData(Constants.AttributeArc, FileAttributes.Archive, false)]
    public void SingleFlag_MapsAndAlwaysSetsReadOnly(byte raw, FileAttributes expected, bool hasStandard)
    {
        FileAttributes result = XisoAttributes.ToWindowsFileAttributes(raw);

        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.HasFlag(expected));
        Assert.Equal(hasStandard, result.HasFlag(FileAttributes.Normal));
    }

    [Fact]
    public void Directory_MapsDirectoryAndReadOnly()
    {
        FileAttributes result = XisoAttributes.ToWindowsFileAttributes(Constants.AttributeDir);

        Assert.True(result.HasFlag(FileAttributes.Directory));
        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.False(result.HasFlag(FileAttributes.Normal));
    }

    [Fact]
    public void None_MapsReadOnlyAndNormal()
    {
        FileAttributes result = XisoAttributes.ToWindowsFileAttributes(0x00);

        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.HasFlag(FileAttributes.Normal));
        Assert.False(result.HasFlag(FileAttributes.Directory));
        Assert.False(result.HasFlag(FileAttributes.Hidden));
        Assert.False(result.HasFlag(FileAttributes.System));
        Assert.False(result.HasFlag(FileAttributes.Archive));
    }

    [Fact]
    public void NormalOnly_MapsReadOnlyAndNormal()
    {
        // NOR (0x80) carries no Windows-standard bit, so Normal is added.
        FileAttributes result = XisoAttributes.ToWindowsFileAttributes(Constants.AttributeNor);

        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.HasFlag(FileAttributes.Normal));
    }

    [Fact]
    public void CombinedAttributes_MapsAllFlags()
    {
        byte raw = Constants.AttributeRo | Constants.AttributeHid | Constants.AttributeSys |
                   Constants.AttributeDir;

        FileAttributes result = XisoAttributes.ToWindowsFileAttributes(raw);

        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.HasFlag(FileAttributes.Hidden));
        Assert.True(result.HasFlag(FileAttributes.System));
        Assert.True(result.HasFlag(FileAttributes.Directory));
        Assert.False(result.HasFlag(FileAttributes.Archive));
        Assert.False(result.HasFlag(FileAttributes.Normal));
    }

    [Fact]
    public void AllFlags_MapsEveryStandardAttribute()
    {
        byte raw = Constants.AttributeRo | Constants.AttributeHid | Constants.AttributeSys |
                   Constants.AttributeDir | Constants.AttributeArc | Constants.AttributeNor;

        FileAttributes result = XisoAttributes.ToWindowsFileAttributes(raw);

        Assert.True(result.HasFlag(FileAttributes.ReadOnly));
        Assert.True(result.HasFlag(FileAttributes.Hidden));
        Assert.True(result.HasFlag(FileAttributes.System));
        Assert.True(result.HasFlag(FileAttributes.Directory));
        Assert.True(result.HasFlag(FileAttributes.Archive));
        Assert.False(result.HasFlag(FileAttributes.Normal));
    }

    [Fact]
    public void ReservedBits_AreIgnored()
    {
        FileAttributes clean = XisoAttributes.ToWindowsFileAttributes(Constants.AttributeArc);
        FileAttributes reserved = XisoAttributes.ToWindowsFileAttributes(
            (byte)(Constants.AttributeArc | Constants.AttributeReservedMask));

        Assert.Equal(clean, reserved);
    }

    [Fact]
    public void MaskedDirentByte_MatchesConsumerMapping()
    {
        // The consumer's locked expectation matrix, ported 1:1.
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Directory,
            XisoAttributes.ToWindowsFileAttributes(0x10));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Hidden,
            XisoAttributes.ToWindowsFileAttributes(0x02));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.System,
            XisoAttributes.ToWindowsFileAttributes(0x04));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Archive,
            XisoAttributes.ToWindowsFileAttributes(0x20));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Normal,
            XisoAttributes.ToWindowsFileAttributes(0x00));
        Assert.Equal(FileAttributes.ReadOnly | FileAttributes.Directory | FileAttributes.Hidden |
                     FileAttributes.System,
            XisoAttributes.ToWindowsFileAttributes(0x10 | 0x02 | 0x04));
    }

    [Fact]
    public void ExplorerNodeAttributes_MatchDirectMapping()
    {
        string src = Path.Combine(Path.GetTempPath(), $"xiso_attr_{Guid.NewGuid():N}");
        string outDir = Path.Combine(Path.GetTempPath(), $"xiso_attr_out_{Guid.NewGuid():N}");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(outDir);
        try
        {
            File.WriteAllText(Path.Combine(src, "file.txt"), "attrs");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "nested.txt"), "x");
            Assert.Equal(0, XisoWriter.CreateXiso(src, outDir, null, null, out string? isoPath, null, null));
            Assert.NotNull(isoPath);

            XisoExplorer explorer = new(isoPath);
            IReadOnlyList<ExplorerNode> children = explorer.ListChildren("/");
            Assert.NotEmpty(children);
            foreach (ExplorerNode node in children)
            {
                FileAttributes mapped = XisoAttributes.ToWindowsFileAttributes(node.Attributes);
                Assert.True(mapped.HasFlag(FileAttributes.ReadOnly));
                if (node.IsDirectory)
                {
                    Assert.True(mapped.HasFlag(FileAttributes.Directory));
                }
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(src)) Directory.Delete(src, true);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
            }
            catch
            {
                // ignored
            }
        }
    }
}
