using XISOSharp.Models;

namespace XISOSharp.Sample;

// Usage:
//   dotnet run --project samples/XISOSharp.Sample -- <image.iso>
//   dotnet run --project samples/XISOSharp.Sample -- --pack <sourceDir> <out.iso>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 3 && string.Equals(args[0], "--pack", StringComparison.OrdinalIgnoreCase))
        {
            int result = XisoWriter.PackFromDirectory(args[1], args[2]);
            Console.WriteLine(result == 0 ? $"Packed {args[2]}" : $"Pack failed ({result})");
            return result;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: XISOSharp.Sample <image.iso> | --pack <sourceDir> <out.iso>");
            return 1;
        }

        using XisoImage image = XisoImage.Open(args[0]);
        Console.WriteLine($"{image.Source.Format}: {image.Length:N0} bytes, volume {image.Volume.DiscFormat}");

        TitleInfo? title = image.GetTitleInfo();
        if (title is not null)
        {
            Console.WriteLine($"Title: {title.TitleName} ({title.TitleIdHex}, {title.GameRegionName})");
        }

        foreach (ExplorerNode node in image.List())
        {
            Console.WriteLine($"  {(node.IsDirectory ? "dir " : "file")} {node.FullPath} ({node.Size:N0} bytes)");
        }

        XisoImageHashResult hash = image.ComputeImageHash();
        Console.WriteLine($"SHA-256: {hash.Sha256}");
        Console.WriteLine($"SHA3-256: {hash.Sha3256}");

        AuditResult audit = image.Validate();
        Console.WriteLine(audit.IsValid ? "Audit: PASS" : $"Audit: FAIL ({audit.Issues.Count} issue(s))");
        return audit.IsValid ? 0 : 1;
    }
}
