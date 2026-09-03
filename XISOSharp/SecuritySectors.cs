namespace XISOSharp;

/// <summary>
/// Parses <c>sectors.txt</c> files that list 4096-sector security ranges,
/// ported from <c>References/XboxKit-0.7/XboxKit/RebuildISO.cs:ParseSecuritySectors</c>.
/// </summary>
public static class SecuritySectors
{
    /// <summary>
    /// Parses a sectors.txt file. Each non-empty line must be "<c>start-end</c>" where
    /// <c>end-start == 4095</c> and <c>0 &lt;= start &lt;= redumpLength/SectorSize-4096</c>.
    /// </summary>
    /// <param name="path">Path to sectors.txt.</param>
    /// <param name="redumpLength">Total Redump ISO length in bytes (for bounds check).</param>
    /// <param name="xgdType">0 = XGD1 (expects 16 ranges), else 1/2/3 expects 1 or 2 ranges.</param>
    /// <param name="quiet">Suppress info logging when false.</param>
    /// <returns>Array of start sectors, or <c>null</c> on error (already logged).</returns>
    public static int[]? ParseFile(string path, long redumpLength, int xgdType, bool quiet = false)
    {
        if (!File.Exists(path))
        {
            Logger.LogErr(
                "[ERROR] To rebuild from an initial seed, a list of security sector ranges is needed in sectors.txt\n");
            return null;
        }

        if (!quiet) Logger.Log($"[INFO] Reading security sector ranges {path}\n");

        var securitySectors = new List<int>();
        using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        var lineCount = 0;
        var maxStart = (redumpLength / Constants.SectorSize) - 4096;
        while (sr.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var range = line.Split('-');
            if (range.Length == 2 && int.TryParse(range[0].Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    out var startSector) &&
                int.TryParse(range[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var endSector))
            {
                if (startSector < 0 || startSector > maxStart || endSector - startSector != 4095)
                {
                    Logger.LogErr("[ERROR] Invalid security sectors in sectors.txt\n");
                    return null;
                }

                lineCount++;
                if (xgdType == 0 || lineCount == 1)
                    securitySectors.Add(startSector);
                // For XGD2/3 only keep first range as per original (lineCount==1)
                // But also allow second range for XGD2/3 to be kept? Original keeps only first when xgdType!=0 && lineCount==1,
                // but still counts lineCount for validation. Keep same: only first.
            }
            else
            {
                Logger.LogErr("[ERROR] Invalid format of sectors.txt\n");
                return null;
            }
        }

        if (xgdType == 0 && lineCount != 16)
        {
            Logger.LogErr($"[ERROR] Expected 16 security sector ranges in sectors.txt, got {lineCount}\n");
            return null;
        }

        if (xgdType != 0 && lineCount != 1 && lineCount != 2)
        {
            Logger.LogErr($"[ERROR] Expected 1 or 2 security sector ranges in sectors.txt, got {lineCount}\n");
            return null;
        }

        return securitySectors.ToArray();
    }

    /// <summary>
    /// Parses without requiring a physical file — useful for the <c>--security-sectors</c> CLI repeatability.
    /// </summary>
    public static int[]? ParseLines(IEnumerable<string> lines, long redumpLength, int xgdType, bool quiet = false)
    {
        var securitySectors = new List<int>();
        var maxStart = (redumpLength / Constants.SectorSize) - 4096;
        var lineCount = 0;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var range = line.Split('-');
            if (range.Length == 2 &&
                int.TryParse(range[0].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var s) &&
                int.TryParse(range[1].Trim(), System.Globalization.CultureInfo.InvariantCulture, out var e))
            {
                if (s < 0 || s > maxStart || e - s != 4095)
                {
                    Logger.LogErr("[ERROR] Invalid security sectors in sectors.txt\n");
                    return null;
                }

                lineCount++;
                if (xgdType == 0 || lineCount == 1) securitySectors.Add(s);
            }
            else
            {
                Logger.LogErr("[ERROR] Invalid format of sectors.txt\n");
                return null;
            }
        }

        if (xgdType == 0 && lineCount != 16)
        {
            Logger.LogErr($"[ERROR] Expected 16 security sector ranges in sectors.txt, got {lineCount}\n");
            return null;
        }

        if (xgdType != 0 && lineCount != 1 && lineCount != 2)
        {
            Logger.LogErr($"[ERROR] Expected 1 or 2 security sector ranges in sectors.txt, got {lineCount}\n");
            return null;
        }

        return securitySectors.ToArray();
    }
}