using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using XISOSharp.Models;

namespace XISOSharp.Fuzz;

/// <summary>
/// Bounded, deterministic mutation fuzzer for the XISO/CISO/CCI/ZAR readers.
/// It starts from a synthetic valid image (no copyrighted game data), mutates
/// bytes/truncates it, then drives every read API and fails on any exception
/// outside the documented format/I/O contract. Run it locally or nightly:
/// <c>dotnet run --project XISOSharp.Fuzz -c Release -- --seconds 300</c>.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int seconds = ReadInt(args, "--seconds", 30);
        int iterationsLimit = ReadInt(args, "--iterations", 0);
        int seed = ReadInt(args, "--seed", Environment.TickCount);
        Random random = new(seed);
        Console.WriteLine($"FUZZ: seed={seed} seconds={seconds} iterations={iterationsLimit}");

        string root = Path.Combine(Path.GetTempPath(), $"xiso_fuzz_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            string baseIso = CreateBaseIso(root);
            List<FuzzTarget> targets =
            [
                new("iso", baseIso),
                new("ciso", CreateCiso(root, baseIso)),
                new("cci", CreateCci(root, baseIso)),
                new("zar", CreateZar(root, baseIso)),
                new("god", CreateGod(root, baseIso)),
            ];

            Stopwatch clock = Stopwatch.StartNew();
            int iterations = 0;
            int failures = 0;
            while ((iterationsLimit <= 0 ? clock.Elapsed < TimeSpan.FromSeconds(seconds) : iterations < iterationsLimit))
            {
                iterations++;
                FuzzTarget target = targets[random.Next(targets.Count)];
                string mutated = Path.Combine(root, $"mutated_{iterations}{Path.GetExtension(target.Path)}");
                string? mutatedData = null;
                try
                {
                    Mutate(target.Path, mutated, random);
                    if (string.Equals(target.Format, "god", StringComparison.Ordinal))
                    {
                        // GOD needs the header's .data sibling; keep the base
                        // fragments while mutating the header fields.
                        mutatedData = mutated + ".data";
                        Directory.CreateDirectory(mutatedData);
                        File.Copy(
                            Path.Combine(target.Path + ".data", "Data0000"),
                            Path.Combine(mutatedData, "Data0000"),
                            overwrite: true);
                    }

                    RunChecks(target.Format, mutated, root);
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    // Expected: corrupt input is rejected by the documented contract.
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.Error.WriteLine($"FUZZ FAILURE format={target.Format} iteration={iterations} seed={seed}");
                    Console.Error.WriteLine(ex);
                    if (failures >= 5)
                    {
                        break;
                    }
                }
                finally
                {
                    TryDelete(mutated);
                    if (mutatedData != null) TryDeleteDirectory(mutatedData);
                }
            }

            Console.WriteLine($"FUZZ: iterations={iterations} failures={failures} elapsed={clock.Elapsed.TotalSeconds:F1}s");
            return failures == 0 ? 0 : 1;
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static string CreateBaseIso(string root)
    {
        string source = Path.Combine(root, "src");
        string output = Path.Combine(root, "base");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "fuzz base\n");
        File.WriteAllBytes(Path.Combine(source, "sub", "payload.bin"), RandomNumberGenerator.GetBytes(8192));
        // Exercise the XBE/XEX header parsers with magic-bearing payloads.
        byte[] xbe = new byte[0x1000];
        "XBEH"u8.CopyTo(xbe);
        File.WriteAllBytes(Path.Combine(source, "default.xbe"), xbe);
        byte[] xex = new byte[0x1000];
        "XEX2"u8.CopyTo(xex);
        File.WriteAllBytes(Path.Combine(source, "default.xex"), xex);
        Directory.CreateDirectory(output);
        string iso = Path.Combine(output, "base.iso");
        int result = XisoWriter.PackFromDirectory(source, iso);
        if (result != 0)
        {
            throw new InvalidOperationException("Could not create the fuzz base ISO.");
        }

        return iso;
    }

    private static string CreateCiso(string root, string iso)
    {
        string path = Path.Combine(root, "base.cso");
        int result = CisoWriter.CompressToCso(iso, path, level: 9);
        if (result != 0)
        {
            throw new InvalidOperationException("Could not create the fuzz base CISO.");
        }

        return path;
    }

    private static string CreateCci(string root, string iso)
    {
        string path = Path.Combine(root, "base.cci");
        int result = XisoCci.CompressToCci(iso, path, level: 1);
        if (result != 0)
        {
            throw new InvalidOperationException("Could not create the fuzz base CCI.");
        }

        return path;
    }

    private static string CreateZar(string root, string iso)
    {
        string path = Path.Combine(root, "base.zar");
        if (!XisoZarchive.CreateZar(iso, path, quiet: true))
        {
            throw new InvalidOperationException("Could not create the fuzz base ZAR.");
        }

        return path;
    }

    private static string CreateGod(string root, string iso)
    {
        string header = Path.Combine(root, "base.god");
        string dataDirectory = header + ".data";
        Directory.CreateDirectory(dataDirectory);

        byte[] isoBytes = File.ReadAllBytes(iso);
        int sectors = isoBytes.Length / Constants.SectorSize;
        uint dataBlocks = (uint)((sectors + 1) / 2);

        byte[] headerBytes = new byte[GodHeader.MinimumHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0), 0x4C495645); // LIVE
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x344), 0x7000); // GOD
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(GodHeader.TitleIdOffset), 0x4D5307E6);
        headerBytes[GodHeader.DiscNumberOffset] = 1;
        headerBytes[GodHeader.DiscCountOffset] = 1;
        byte[] title = Encoding.BigEndianUnicode.GetBytes("Fuzz GOD");
        title.AsSpan(0, Math.Min(title.Length, GodHeader.TitleNameLength))
            .CopyTo(headerBytes.AsSpan(GodHeader.TitleNameOffset));
        WriteUInt24LittleEndian(headerBytes, 0x392, dataBlocks);
        WriteUInt24LittleEndian(headerBytes, 0x395, 0);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x39D), 1);
        BinaryPrimitives.WriteUInt32BigEndian(headerBytes.AsSpan(0x3A9), 1);
        File.WriteAllBytes(header, headerBytes);

        string dataPath = Path.Combine(dataDirectory, "Data0000");
        using FileStream data = new(
            dataPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            1 << 16, FileOptions.RandomAccess);
        for (int block = 0; block * 0x198 < sectors; block++)
        {
            int startSector = block * 0x198;
            int count = Math.Min(0x198, sectors - startSector);
            data.Seek(0x2000 + block * 0xCD000L, SeekOrigin.Begin);
            data.Write(isoBytes, startSector * Constants.SectorSize, count * Constants.SectorSize);
        }

        return header;
    }

    private static void WriteUInt24LittleEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }

    private static void RunChecks(string format, string path, string root)
    {
        if (string.Equals(format, "zar", StringComparison.Ordinal))
        {
            _ = XisoZarchive.ListZar(path);
            _ = XisoChecksum.ComputeZarChecksum(path);
            string extract = Path.Combine(root, "zar_extract");
            try
            {
                XisoZarchive.ExtractZar(path, extract, maxEntries: 4096, maxTotalBytes: 64L * 1024 * 1024);
            }
            finally
            {
                TryDeleteDirectory(extract);
            }

            return;
        }

        using XisoImage image = XisoImage.Open(path);
        _ = image.Volume;
        _ = image.List();
        IReadOnlyList<ExplorerNode> flat = image.ListFlat();
        ExplorerNode? file = flat.FirstOrDefault(static node => !node.IsDirectory && node.Size is > 0 and < 1_000_000);
        if (file is not null)
        {
            _ = image.ReadFile(file.FullPath);
        }

        // A mutated GOD header can claim a multi-terabyte logical length;
        // hashing it would stream the whole range and stall the fuzzer. The
        // other formats' base images are small and still exercise this path.
        if (!string.Equals(format, "god", StringComparison.Ordinal))
            _ = image.ComputeImageHash();

        _ = image.Validate();
        _ = image.GetTitleInfo();
        _ = image.GetXbeInfo("/default.xbe");
        _ = image.GetXexInfo("/default.xex");
        _ = XisoReader.AuditXiso(path, requireOptimizedTag: false);
    }

    private static void Mutate(string source, string destination, Random random)
    {
        byte[] data = File.ReadAllBytes(source);
        int mode = random.Next(4);
        if (mode == 0 && data.Length > 64)
        {
            // Truncate to a random length.
            int length = random.Next(1, data.Length);
            File.WriteAllBytes(destination, data.AsSpan(0, length).ToArray());
            return;
        }

        int mutations = random.Next(1, 16);
        for (int i = 0; i < mutations; i++)
        {
            int position;
            if (mode == 1 && data.Length > 512)
            {
                // Bias header/index mutations.
                position = random.Next(2) == 0 ? random.Next(0, Math.Min(256, data.Length)) : random.Next(Math.Max(0, data.Length - 256), data.Length);
            }
            else
            {
                position = random.Next(data.Length);
            }

            data[position] = (byte)random.Next(256);
        }

        File.WriteAllBytes(destination, data);
    }

    private static bool IsExpected(Exception ex) =>
        ex is XisoFormatException
            or XisoEmptyException
            or XisoFileTooLargeException
            or ExtractErrorException
            or ExtractFileException
            or TruncatedCopyException
            or InvalidDataException
            or EndOfStreamException
            or IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or OperationCanceledException;

    private static int ReadInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private sealed record FuzzTarget(string Format, string Path);
}
