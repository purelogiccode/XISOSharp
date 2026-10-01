#if NET10_0_OR_GREATER
using XISOSharp.Gui.Services;

namespace XISOSharp.Tests;

/// <summary>
/// Unit tests for <see cref="CliCommands"/>: every argument builder emits flags
/// before positionals and passes the non-interactive overwrite flag.
/// </summary>
public class CliCommandsTests
{
    /// <summary>Verifies the overwrite flag mapping.</summary>
    [Theory]
    [InlineData(true, "-y")]
    [InlineData(false, "-n")]
    public void OverwriteFlag_Maps(bool overwrite, string expected)
    {
        Assert.Equal(expected, CliCommands.OverwriteFlag(overwrite));
    }

    /// <summary>Verifies the version argv.</summary>
    [Fact]
    public void Version_IsVersionFlag()
    {
        Assert.Equal(["-v"], CliCommands.Version());
    }

    /// <summary>Verifies the extract argv includes destination, overwrite flag, and images in order.</summary>
    [Fact]
    public void Extract_WithDestination()
    {
        Assert.Equal(["-d", "out", "-x", "-y", "a.iso", "b.iso"],
            CliCommands.Extract(["a.iso", "b.iso"], "out", overwrite: true));
    }

    /// <summary>Verifies the extract argv omits a blank destination.</summary>
    [Fact]
    public void Extract_WithoutDestination()
    {
        Assert.Equal(["-x", "-n", "a.iso"], CliCommands.Extract(["a.iso"], null, overwrite: false));
        Assert.Equal(["-x", "-n", "a.iso"], CliCommands.Extract(["a.iso"], "   ", overwrite: false));
    }

    /// <summary>Verifies list and tree prepend their mode flag.</summary>
    [Fact]
    public void ListAndTree_PrependModeFlag()
    {
        Assert.Equal(["-l", "a.iso"], CliCommands.List(["a.iso"]));
        Assert.Equal(["-t", "a.iso", "b.iso"], CliCommands.Tree(["a.iso", "b.iso"]));
    }

    /// <summary>Verifies info includes an optional in-image path only when present.</summary>
    [Fact]
    public void Info_OptionalPath()
    {
        Assert.Equal(["-i", "a.iso"], CliCommands.Info("a.iso", null));
        Assert.Equal(["-i", "a.iso", "sub"], CliCommands.Info("a.iso", "sub"));
        Assert.Equal(["-i", "a.iso"], CliCommands.Info("a.iso", "  "));
    }

    /// <summary>Verifies unpack includes an optional destination only when present.</summary>
    [Fact]
    public void Unpack_OptionalDestination()
    {
        Assert.Equal(["--unpack", "a.iso"], CliCommands.Unpack("a.iso", null));
        Assert.Equal(["--unpack", "a.iso", "out"], CliCommands.Unpack("a.iso", "out"));
    }

    /// <summary>Verifies copy-out passes image, in-image path, and destination.</summary>
    [Fact]
    public void CopyOut_Exact()
    {
        Assert.Equal(["--copy-out", "a.iso", "dir/file.bin", "dest.bin"],
            CliCommands.CopyOut("a.iso", "dir/file.bin", "dest.bin"));
    }

    /// <summary>Verifies create emits name, excludes, and toggles before the overwrite flag.</summary>
    [Fact]
    public void Create_FullFlags()
    {
        Assert.Equal(["-c", "src", "game.iso", "-X", "*.tmp", "-X", "build", "-s", "-m", "-y"],
            CliCommands.Create("src", "game.iso", ["*.tmp", "build"], skipSystemUpdate: true,
                disableXbePatch: true, overwrite: true));
    }

    /// <summary>Verifies create omits blank name and exclude entries.</summary>
    [Fact]
    public void Create_OmitsBlankOptionals()
    {
        Assert.Equal(["-c", "src", "-n"],
            CliCommands.Create("src", "  ", ["", "   "], skipSystemUpdate: false,
                disableXbePatch: false, overwrite: false));
    }

    /// <summary>Verifies rewrite emits flags before the images.</summary>
    [Fact]
    public void Rewrite_FlagsBeforeImages()
    {
        Assert.Equal(["-r", "-d", "work", "-o", "out.iso", "-D", "-m", "--validate-strict",
                "--validate-report", "report.json", "-y", "a.iso"],
            CliCommands.Rewrite(["a.iso"], "out.iso", "work", deleteOld: true, disableXbePatch: true,
                validate: false, validateChecksums: false, validateStrict: true,
                validateReport: "report.json", overwrite: true));
    }

    /// <summary>Verifies checksum validation wins over plain validation.</summary>
    [Fact]
    public void Rewrite_ChecksumValidationWins()
    {
        string[] args = CliCommands.Rewrite(["a.iso"], null, null, false, false,
            validate: true, validateChecksums: true, validateStrict: false, validateReport: null, overwrite: false);
        Assert.Equal(["-r", "--validate-checksums", "-n", "a.iso"], args);
    }

    /// <summary>Verifies plain validation is emitted when checksums are off.</summary>
    [Fact]
    public void Rewrite_PlainValidation()
    {
        string[] args = CliCommands.Rewrite(["a.iso"], null, null, false, false,
            validate: true, validateChecksums: false, validateStrict: false, validateReport: null, overwrite: false);
        Assert.Equal(["-r", "--validate", "-n", "a.iso"], args);
    }

    /// <summary>
    /// Verifies wipe and trim put every flag before the image: the CLI main
    /// parser stops at the first positional and rejects flags after a filename,
    /// so -o must precede the image.
    /// </summary>
    [Fact]
    public void WipeAndTrim_FlagsBeforeImage()
    {
        Assert.Equal(["--wipe", "-y", "-o", "out.iso", "a.iso"], CliCommands.Wipe("a.iso", "out.iso", true));
        Assert.Equal(["--trim", "-n", "-o", "out.iso", "a.iso"], CliCommands.Trim("a.iso", "out.iso", false));
        Assert.Equal(["--trim", "-n", "a.iso"], CliCommands.Trim("a.iso", null, false));
    }

    /// <summary>Verifies rebuild passes parts, output, and optional sectors before the flag.</summary>
    [Fact]
    public void Rebuild_Exact()
    {
        Assert.Equal(["rebuild", "video.bin", "game.iso", "-o", "out.iso", "--security-sectors", "sec.bin", "-y"],
            CliCommands.Rebuild(["video.bin", "game.iso"], "out.iso", "sec.bin", overwrite: true));
        Assert.Equal(["rebuild", "game.iso", "-o", "out.iso", "-n"],
            CliCommands.Rebuild(["game.iso"], "out.iso", null, overwrite: false));
    }

    /// <summary>Verifies compress emits invariant level/version, split, source, output, and flag.</summary>
    [Fact]
    public void Compress_Exact()
    {
        Assert.Equal(["compress", "--ciso-level", "9", "--ciso-version", "2", "--ciso-split", "4G",
                "game.iso", "game.cso", "-y"],
            CliCommands.Compress("game.iso", "game.cso", 9, 2, "4G", overwrite: true));
    }

    /// <summary>Verifies compress omits blank split and output.</summary>
    [Fact]
    public void Compress_OmitsBlankOptionals()
    {
        Assert.Equal(["compress", "--ciso-level", "6", "--ciso-version", "1", "game.iso", "-n"],
            CliCommands.Compress("game.iso", null, 6, 1, null, overwrite: false));
    }

    /// <summary>Verifies ZAR emits policy and output before the source.</summary>
    [Fact]
    public void Zar_Exact()
    {
        Assert.Equal(["--zar", "--policy", "skip", "-o", "out.zar", "game.iso"],
            CliCommands.Zar("game.iso", "out.zar", "skip"));
        Assert.Equal(["--zar", "game.iso"], CliCommands.Zar("game.iso", null, null));
    }

    /// <summary>Verifies decompress emits source, output, and flag.</summary>
    [Fact]
    public void Decompress_Exact()
    {
        Assert.Equal(["decompress", "game.cso", "game.iso", "-y"],
            CliCommands.Decompress("game.cso", "game.iso", overwrite: true));
        Assert.Equal(["decompress", "game.cso", "-n"],
            CliCommands.Decompress("game.cso", null, overwrite: false));
    }

    /// <summary>Verifies validate emits optional flags then the two positionals.</summary>
    [Fact]
    public void Validate_Exact()
    {
        Assert.Equal(["validate", "--validate-checksums", "--validate-report", "r.json", "a.iso", "b.iso"],
            CliCommands.Validate("a.iso", "b.iso", checksums: true, report: "r.json"));
        Assert.Equal(["validate", "a.iso", "b.iso"],
            CliCommands.Validate("a.iso", "b.iso", checksums: false, report: null));
    }

    /// <summary>Verifies checksum appends silent after the images.</summary>
    [Fact]
    public void Checksum_Exact()
    {
        Assert.Equal(["checksum", "a.iso", "b.iso", "--silent"],
            CliCommands.Checksum(["a.iso", "b.iso"], silent: true));
        Assert.Equal(["checksum", "a.iso"], CliCommands.Checksum(["a.iso"], silent: false));
    }

    /// <summary>Verifies batch emits directory, recursion, destination, mode, and flag.</summary>
    [Fact]
    public void Batch_Exact()
    {
        Assert.Equal(["--batch", "dir", "--batch-recursive", "-d", "out", "-x", "-y"],
            CliCommands.Batch("dir", recursive: true, "-x", "out", overwrite: true));
        Assert.Equal(["--batch", "dir", "-l", "-n"],
            CliCommands.Batch("dir", recursive: false, "-l", null, overwrite: false));
    }

    /// <summary>Verifies batch rejects an empty mode flag.</summary>
    [Fact]
    public void Batch_EmptyModeFlag_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => CliCommands.Batch("dir", false, string.Empty, null, false));
    }

    /// <summary>Verifies extract and rewrite keep every flag ahead of the image positionals.</summary>
    [Fact]
    public void ExtractAndRewrite_FlagsPrecedeImages()
    {
        string[] extract = CliCommands.Extract(["game.iso"], "out", true);
        Assert.True(Array.IndexOf(extract, "-x") < Array.IndexOf(extract, "game.iso"));
        Assert.True(Array.IndexOf(extract, "-y") < Array.IndexOf(extract, "game.iso"));

        string[] rewrite = CliCommands.Rewrite(["game.iso"], null, null, false, false, false, false, false, null, true);
        Assert.True(Array.IndexOf(rewrite, "-y") < Array.IndexOf(rewrite, "game.iso"));
    }
}
#endif
