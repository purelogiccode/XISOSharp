using System.Text;
using XISOSharp.Cli;
using XISOSharp.Models;
using XISOSharp.TestDataGenerator;
using ZArchiveSharp;

namespace XISOSharp.Tests;

/// <summary>
/// Safe-extract name policy (PlanToEnhance §15.5): hostile image/archive names
/// can be rejected (the historical default), sanitized with collision-safe
/// names, or skipped. Covers the ISO, STFS and ZAR extractors plus the CLI.
/// </summary>
[Collection("Sequential")]
public sealed class ExtractNamePolicyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                else if (File.Exists(dir)) File.Delete(dir);
            }
            catch
            {
                // Best effort cleanup.
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

    private string CreateIso(string work, Action<string> populate)
    {
        string src = CreateTempDir("xiso_names_src");
        populate(src);
        string iso = Path.Combine(work, "game.iso");
        Assert.Equal(0, XisoWriter.PackFromDirectory(src, iso));
        return iso;
    }

    private static (uint RootSize, long RootAbs) RootLayout(string isoPath)
    {
        VolumeInfo volume = XisoReader.GetVolumeInfo(isoPath);
        Assert.True(volume.IsValid, $"fixture ISO invalid: {isoPath}");
        return (volume.RootDirSize, ((long)volume.RootDirSector * Constants.SectorSize) + volume.DiscLseek);
    }

    private static long FindEntryHeader(byte[] image, long tableAbs, uint tableSize, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name);
        long tableEnd = tableAbs + tableSize;
        for (long i = tableAbs; i + 14 + nameBytes.Length <= Math.Min(tableEnd, image.Length); i++)
        {
            bool match = true;
            for (int k = 0; k < nameBytes.Length; k++)
            {
                if (image[i + 14 + k] != nameBytes[k])
                {
                    match = false;
                    break;
                }
            }

            if (match && image[i + 13] == nameBytes.Length)
            {
                return i;
            }
        }

        Assert.Fail($"entry '{name}' not found in directory table at {tableAbs}");
        return -1;
    }

    /// <summary>Replaces the first byte of <paramref name="name"/> with <c>'/'</c>.</summary>
    private static void MakeFileUnsafe(string isoPath, string name)
    {
        (uint rootSize, long rootAbs) = RootLayout(isoPath);
        byte[] image = File.ReadAllBytes(isoPath);
        long header = FindEntryHeader(image, rootAbs, rootSize, name);
        image[header + 14] = (byte)'/';
        File.WriteAllBytes(isoPath, image);
    }

    [Fact]
    public void UnpackImage_SanitizePolicy_RenamesUnsafeFile()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        });
        MakeFileUnsafe(iso, "a.txt");

        string destination = Path.Combine(work, "out");
        Assert.Equal(0, XisoReader.UnpackImage(iso, destination, options: new UnpackOptions
        {
            NamePolicy = ExtractNamePolicy.Sanitize,
        }));

        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "_.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    [Fact]
    public void UnpackImage_SkipPolicy_OmitsUnsafeFile()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        });
        MakeFileUnsafe(iso, "a.txt");

        string destination = Path.Combine(work, "out");
        Assert.Equal(0, XisoReader.UnpackImage(iso, destination, options: new UnpackOptions
        {
            NamePolicy = ExtractNamePolicy.Skip,
        }));

        Assert.False(File.Exists(Path.Combine(destination, "_.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    [Fact]
    public void UnpackImage_SanitizePolicy_RenamesUnsafeDirectoryAndChildren()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "top.txt"), "top");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "inner.txt"), "inner");
        });

        (uint rootSize, long rootAbs) = RootLayout(iso);
        byte[] image = File.ReadAllBytes(iso);
        long header = FindEntryHeader(image, rootAbs, rootSize, "sub");
        image[header + 16] = (byte)'\\';
        File.WriteAllBytes(iso, image);

        string destination = Path.Combine(work, "out");
        Assert.Equal(0, XisoReader.UnpackImage(iso, destination, options: new UnpackOptions
        {
            NamePolicy = ExtractNamePolicy.Sanitize,
        }));

        Assert.Equal("inner", File.ReadAllText(Path.Combine(destination, "su_", "inner.txt")));
        Assert.Equal("top", File.ReadAllText(Path.Combine(destination, "top.txt")));
    }

    [Fact]
    public void UnpackImage_SanitizePolicy_WorksWithParallelExtraction()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        });
        MakeFileUnsafe(iso, "a.txt");

        string destination = Path.Combine(work, "out");
        Assert.Equal(0, XisoReader.UnpackImage(iso, destination, options: new UnpackOptions
        {
            NamePolicy = ExtractNamePolicy.Sanitize,
            MaxDegreeOfParallelism = 4,
        }));

        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "_.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    [Fact]
    public void UnpackImage_SanitizePolicy_WorksWithFilesystemDestination()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        });
        MakeFileUnsafe(iso, "a.txt");

        MemoryFilesystem memory = new();
        Assert.Equal(0, XisoReader.UnpackImage(iso, memory, options: new UnpackOptions
        {
            NamePolicy = ExtractNamePolicy.Sanitize,
        }));

        Assert.Equal("hello", Encoding.UTF8.GetString(memory.ReadAllBytes("_.txt")));
        Assert.Equal("world", Encoding.UTF8.GetString(memory.ReadAllBytes("b.txt")));
    }

    [Fact]
    public void Cli_SanitizePolicy_ExtractsRenamedFile()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        });
        MakeFileUnsafe(iso, "a.txt");

        string destination = Path.Combine(work, "out");
        using (LogCapture capture = new())
        {
            int result = Program.Main(["-x", "-d", destination, "--name-policy", "sanitize", iso]);
            Assert.Equal(0, result);
            Assert.Contains("renamed unsafe entry", capture.Error, StringComparison.Ordinal);
        }

        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "_.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    [Fact]
    public void Cli_Unpack_SanitizePolicy_ExtractsRenamedFile()
    {
        string work = CreateTempDir("xiso_names");
        string iso = CreateIso(work, src =>
        {
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");
            File.WriteAllText(Path.Combine(src, "b.txt"), "world");
        });
        MakeFileUnsafe(iso, "a.txt");

        string destination = Path.Combine(work, "out");
        using (LogCapture capture = new())
        {
            int result = Program.Main(["--name-policy", "sanitize", "--unpack", iso, destination]);
            Assert.Equal(0, result);
            Assert.Contains("renamed unsafe entry", capture.Error, StringComparison.Ordinal);
        }

        Assert.Equal("hello", File.ReadAllText(Path.Combine(destination, "_.txt")));
        Assert.Equal("world", File.ReadAllText(Path.Combine(destination, "b.txt")));
    }

    [WindowsOnlyFact]
    public void StfsPackage_SanitizeAndSkipPolicies_HandleUnsafeNames()
    {
        string work = CreateTempDir("xiso_names_stfs");
        string packagePath = Path.Combine(work, "game");
        SyntheticStfsPackage.Create(packagePath, [
            new SyntheticStfsFile("safe.bin", [1, 2, 3]),
        ]);

        // Rename the file-table entry in place: a colon is a valid STFS name
        // (the parser only rejects structural separators) but drive-relative
        // on Windows, so extraction needs the policy.
        byte[] bytes = File.ReadAllBytes(packagePath);
        int entryNameOffset = 0xB000 + 0x1000; // file table is data block 0
        Encoding.ASCII.GetBytes("a:b.bin").CopyTo(bytes.AsSpan(entryNameOffset));
        bytes[entryNameOffset + 0x28] = 7;
        File.WriteAllBytes(packagePath, bytes);

        using StfsPackage package = StfsPackage.Open(packagePath);

        string sanitized = Path.Combine(work, "sanitized");
        Assert.Equal(1, package.ExtractTo(sanitized, ExtractNamePolicy.Sanitize));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(sanitized, "a_b.bin")));

        string skipped = Path.Combine(work, "skipped");
        Assert.Equal(0, package.ExtractTo(skipped, ExtractNamePolicy.Skip));
        Assert.False(File.Exists(Path.Combine(skipped, "a_b.bin")));

        Assert.Throws<InvalidDataException>(() =>
            package.ExtractTo(Path.Combine(work, "rejected")));
    }

    [WindowsOnlyFact]
    public void ExtractZar_SanitizeAndSkipPolicies_HandleUnsafeNames()
    {
        string work = CreateTempDir("xiso_names_zar");
        string zar = Path.Combine(work, "game.zar");
        using (FileStream output = File.Create(zar))
        using (ZArchiveWriter writer = new(output))
        {
            Assert.True(writer.StartNewFile("a:b.txt"));
            writer.AppendData("hello"u8);
            writer.Finalize();
        }

        string sanitized = Path.Combine(work, "sanitized");
        Assert.Equal(0, XisoZarchive.ExtractZar(zar, sanitized, ExtractNamePolicy.Sanitize));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(sanitized, "a_b.txt")));

        string skipped = Path.Combine(work, "skipped");
        Assert.Equal(0, XisoZarchive.ExtractZar(zar, skipped, ExtractNamePolicy.Skip));
        Assert.False(File.Exists(Path.Combine(skipped, "a_b.txt")));

        Assert.Throws<InvalidDataException>(() =>
            XisoZarchive.ExtractZar(zar, Path.Combine(work, "rejected")));
    }

    private sealed class LogCapture : IDisposable
    {
        private readonly TextWriter _savedOut = Logger.Out;
        private readonly TextWriter _savedError = Logger.Error;
        private readonly bool _savedQuiet = Logger.Quiet;
        private readonly bool _savedRealQuiet = Logger.RealQuiet;
        private readonly StringWriter _out = new();
        private readonly StringWriter _error = new();

        internal LogCapture()
        {
            Logger.Out = _out;
            Logger.Error = _error;
            Logger.Quiet = false;
            Logger.RealQuiet = false;
        }

        internal string Error => _error.ToString();

        public void Dispose()
        {
            Logger.Out = _savedOut;
            Logger.Error = _savedError;
            Logger.Quiet = _savedQuiet;
            Logger.RealQuiet = _savedRealQuiet;
            _out.Dispose();
            _error.Dispose();
        }
    }
}
