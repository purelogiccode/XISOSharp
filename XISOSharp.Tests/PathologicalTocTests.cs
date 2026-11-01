using System.Buffers.Binary;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp.Tests;

/// <summary>
/// Phase 4 pathological-TOC tests: a directory table whose AVL shape is a long
/// left-skewed chain must list, tree, extract, audit and rewrite successfully
/// (the old recursive walk treated every left descent as a directory-nesting
/// level and failed at <see cref="Constants.MaxTocDepth"/>; deeper chains could
/// overflow the process stack). Right-skewed and alternating chains are covered
/// too, and a left self-cycle must still fail with the named TOC error.
/// </summary>
[Collection("Sequential")]
public sealed class PathologicalTocTests : IDisposable
{
    private const int EntrySize = 24; // 14-byte header + 8-char name + 2 pad
    private const uint RootSector = 40;
    private const int ChainLength = 4096;

    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch
            {
                // ignored
            }
        }
    }

    public enum Skew
    {
        Left,
        Right,
        Alternating,
        Balanced,
    }

    [Fact]
    public void LeftSkewedChain_ListTreeExtractRewrite_Succeeds()
    {
        string work = CreateTempDir("xiso_path_toc");
        string iso = CreateTreeIso(work, "left.iso", ChainLength, BuildLayout(Skew.Left, ChainLength));

        int savedFiles = Logger.TotalFiles;
        try
        {
            Logger.TotalFiles = 0;
            Assert.Equal(0, XisoReader.List(iso, llCompat: false));
            Assert.Equal(ChainLength, Logger.TotalFiles);

            Logger.TotalFiles = 0;
            Assert.Equal(0, XisoReader.Tree(iso, llCompat: false));
            Assert.Equal(ChainLength, Logger.TotalFiles);
        }
        finally
        {
            Logger.TotalFiles = savedFiles;
        }

        string extracted = Path.Combine(work, "extracted");
        Assert.Equal(0, XisoReader.Extract(iso, extracted, llCompat: false));
        Assert.Equal(ChainLength, Directory.GetFiles(extracted).Length);

        string rewriteDir = Path.Combine(work, "rewrite");
        Directory.CreateDirectory(rewriteDir);
        Assert.Equal(0, XisoReader.Rewrite(iso, rewriteDir, out string? rewritten));
        Assert.NotNull(rewritten);
        Assert.True(File.Exists(rewritten));

        Logger.TotalFiles = 0;
        try
        {
            Assert.Equal(0, XisoReader.List(rewritten, llCompat: false));
            Assert.Equal(ChainLength, Logger.TotalFiles);
        }
        finally
        {
            Logger.TotalFiles = savedFiles;
        }
    }

    [Theory]
    [InlineData(Skew.Right)]
    [InlineData(Skew.Alternating)]
    [InlineData(Skew.Balanced)]
    public void OtherShapes_ListAndExtractSucceed(Skew skew)
    {
        string work = CreateTempDir("xiso_path_toc");
        string iso = CreateTreeIso(work, $"{skew}.iso", ChainLength, BuildLayout(skew, ChainLength));

        Logger.TotalFiles = 0;
        try
        {
            Assert.Equal(0, XisoReader.List(iso, llCompat: false));
            Assert.Equal(ChainLength, Logger.TotalFiles);
        }
        finally
        {
            Logger.TotalFiles = 0;
        }

        string extracted = Path.Combine(work, "extracted");
        Assert.Equal(0, XisoReader.Extract(iso, extracted, llCompat: false));
        Assert.Equal(ChainLength, Directory.GetFiles(extracted).Length);
    }

    [Fact]
    public void LeftSkewedAndBalanced_RewritesHaveSameChecksum()
    {
        string work = CreateTempDir("xiso_path_toc");
        string leftIso = CreateTreeIso(work, "left.iso", ChainLength, BuildLayout(Skew.Left, ChainLength));
        string balancedIso = CreateTreeIso(work, "balanced.iso", ChainLength, BuildLayout(Skew.Balanced, ChainLength));

        string leftRewriteDir = Path.Combine(work, "rw_left");
        string balancedRewriteDir = Path.Combine(work, "rw_balanced");
        Directory.CreateDirectory(leftRewriteDir);
        Directory.CreateDirectory(balancedRewriteDir);

        Assert.Equal(0, XisoReader.Rewrite(leftIso, leftRewriteDir, out string? leftOut));
        Assert.Equal(0, XisoReader.Rewrite(balancedIso, balancedRewriteDir, out string? balancedOut));
        Assert.NotNull(leftOut);
        Assert.NotNull(balancedOut);

        Assert.Equal(
            XisoChecksum.ComputeImageChecksum(balancedOut),
            XisoChecksum.ComputeImageChecksum(leftOut));
    }

    [Fact]
    public void LeftSelfCycle_StillThrowsInvalidToc()
    {
        string work = CreateTempDir("xiso_path_toc");
        string iso = CreateTreeIso(work, "cycle.iso", 4, BuildLayout(Skew.Left, 4));

        // Point entry 1's left child at itself (offset 0 is the no-left marker,
        // so the table root cannot self-cycle on the left; a deeper node can).
        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(((long)RootSector * Constants.SectorSize) + EntrySize, SeekOrigin.Begin);
            Span<byte> offset = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(offset, EntrySize / Constants.DwordSize);
            fs.Write(offset);
        }

        XisoFormatException exception = Assert.Throws<XisoFormatException>(
            () => XisoReader.List(iso, llCompat: false));
        Assert.Contains("invalid TOC entry", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cycle", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LeftSubtreeCorruption_ContinueOnError_SkipsSubtreeAndKeepsSiblings()
    {
        string work = CreateTempDir("xiso_path_toc");
        string iso = CreateTreeIso(work, "badleft.iso", 3, BuildLayout(Skew.Left, 3));

        // Entry 2 (file0002) is the leftmost leaf. Point its left child far past
        // the table: the walk must record the failure against entry 1 (the parent
        // that called it), extract entry 1 and entry 0, and skip only file0002.
        using (FileStream fs = new(iso, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            fs.Seek(((long)RootSector * Constants.SectorSize) + (2 * EntrySize), SeekOrigin.Begin);
            Span<byte> offset = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(offset, 0xFFFE);
            fs.Write(offset);
        }

        string dest = Path.Combine(work, "extracted");
        UnpackOptions options = new() { ContinueOnError = true };
        ExtractErrorException exception = Assert.Throws<ExtractErrorException>(
            () => XisoReader.Extract(iso, dest, llCompat: false, options: options));

        Assert.Equal(ExtractError.ErrExtractFailed, exception.ErrorCode);
        Assert.Contains("invalid TOC entry", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(dest, "file0000")));
        Assert.True(File.Exists(Path.Combine(dest, "file0001")));
        Assert.False(File.Exists(Path.Combine(dest, "file0002")));
    }

    private readonly record struct NodeLayout(string Name, int Left, int Right);

    private static NodeLayout[] BuildLayout(Skew skew, int count)
    {
        NodeLayout[] layout = new NodeLayout[count];

        if (skew == Skew.Balanced)
        {
            int next = 0;
            Build(0, count - 1);
            return layout;

            int Build(int lo, int hi)
            {
                if (lo > hi) return -1;
                int mid = (lo + hi) / 2;
                // Physical entries are assigned in pre-order so the table's first
                // entry (offset 0) is the tree root, as the on-disk format requires.
                int physical = next++;
                int left = Build(lo, mid - 1);
                int right = Build(mid + 1, hi);
                layout[physical] = new NodeLayout($"file{mid:D4}", left, right);
                return physical;
            }
        }

        for (int i = 0; i < count; i++)
        {
            int left = -1;
            int right = -1;
            if (i < count - 1)
            {
                if (skew == Skew.Left)
                {
                    left = i + 1;
                }
                else if (skew == Skew.Right)
                {
                    right = i + 1;
                }
                else if (i % 2 == 0)
                {
                    left = i + 1;
                }
                else
                {
                    right = i + 1;
                }
            }

            layout[i] = new NodeLayout($"file{i:D4}", left, right);
        }

        return layout;
    }

    private string CreateTreeIso(string work, string name, int count, NodeLayout[] layout)
    {
        string path = Path.Combine(work, name);
        long rootAbs = (long)RootSector * Constants.SectorSize;
        uint rootSize = (uint)(count * EntrySize);

        using (FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            // At least one whole sector so the root sector itself is inside the image.
            fs.SetLength(rootAbs + Math.Max(rootSize, (uint)Constants.SectorSize));

            fs.Seek(Constants.HeaderOffset, SeekOrigin.Begin);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));
            Span<byte> u32 = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(u32, RootSector);
            fs.Write(u32);
            BinaryPrimitives.WriteUInt32LittleEndian(u32, rootSize);
            fs.Write(u32);
            fs.Write(new byte[Constants.FileTimeSize + Constants.UnusedSize]);
            fs.Write(Encoding.ASCII.GetBytes(Constants.HeaderData));

            fs.Seek(rootAbs, SeekOrigin.Begin);
            byte[] entry = new byte[EntrySize];
            for (int i = 0; i < count; i++)
            {
                Array.Clear(entry);
                NodeLayout node = layout[i];
                BinaryPrimitives.WriteUInt16LittleEndian(entry,
                    node.Left < 0 ? (ushort)0 : (ushort)(node.Left * (EntrySize / Constants.DwordSize)));
                BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(2),
                    node.Right < 0 ? (ushort)0 : (ushort)(node.Right * (EntrySize / Constants.DwordSize)));
                entry[12] = Constants.AttributeArc;
                byte[] nameBytes = Encoding.ASCII.GetBytes(node.Name);
                entry[13] = (byte)nameBytes.Length;
                nameBytes.CopyTo(entry, 14);
                fs.Write(entry);
            }
        }

        return path;
    }

    private string CreateTempDir(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
