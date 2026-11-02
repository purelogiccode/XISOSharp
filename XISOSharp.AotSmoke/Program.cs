using System.Security.Cryptography;
using XISOSharp.Models;

namespace XISOSharp.AotSmoke;

/// <summary>
/// AOT/trim smoke test: exercises the public library surface end to end
/// (pack, open, list, read, hash, validate, extract) under NativeAOT. Keep it
/// free of reflection-heavy APIs so a publish failure names the offending
/// library call rather than the harness.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xiso_aot_{Guid.NewGuid():N}");
        try
        {
            string source = Path.Combine(root, "src");
            string output = Path.Combine(root, "out");
            string extract = Path.Combine(root, "extract");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "readme.txt"), "AOT smoke\n");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.WriteAllBytes(Path.Combine(source, "sub", "payload.bin"), RandomNumberGenerator.GetBytes(4096));

            string iso = Path.Combine(output, "game.iso");
            int pack = XisoWriter.PackFromDirectory(source, iso);
            if (pack != 0 || !File.Exists(iso))
            {
                Console.Error.WriteLine($"pack failed: {pack}");
                return 1;
            }

            using XisoImage image = XisoImage.Open(iso);
            if (!image.Volume.IsValid || image.List().Count == 0)
            {
                Console.Error.WriteLine("open/list failed");
                return 1;
            }

            byte[] readme = image.ReadFile("/readme.txt");
            if (readme.Length == 0)
            {
                Console.Error.WriteLine("read failed");
                return 1;
            }

            XisoImageHashResult hash = image.ComputeImageHash();
            if (hash.Sha256.Length != 64 || hash.Crc32.Length != 8)
            {
                Console.Error.WriteLine("hash failed");
                return 1;
            }

            AuditResult audit = image.Validate();
            if (!audit.IsValid)
            {
                Console.Error.WriteLine("audit failed: " + string.Join("; ", audit.Issues));
                return 1;
            }

            int extracted = image.Extract(extract,
                new UnpackOptions { MaxDegreeOfParallelism = 2 });
            if (extracted != 0 ||
                !File.Exists(Path.Combine(extract, "readme.txt")) ||
                !File.Exists(Path.Combine(extract, "sub", "payload.bin")))
            {
                Console.Error.WriteLine("extract failed");
                return 1;
            }

            Console.WriteLine("AOT-SMOKE: all passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"AOT-SMOKE failed: {ex}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
