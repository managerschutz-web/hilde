using System.Runtime.InteropServices;
using Hilde.Core;

namespace Hilde.Setup;

public sealed record SystemReport(string OsDescription, bool IsWindows11OrLater, bool Is64Bit,
    long TotalMemoryBytes, long? FreeDiskBytes, PerformanceTier RecommendedTier, int LogicalCores);

/// <summary>Schritt 1 des Einrichtungsassistenten: Betriebssystem, RAM, Speicherplatz.</summary>
public static class SystemCheck
{
    public static SystemReport Run(HildePaths paths)
    {
        var os = Environment.OSVersion;
        bool win11 = OperatingSystem.IsWindows() && os.Version.Major >= 10 && os.Version.Build >= 22000;
        long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var tier = total >= 14L * 1024 * 1024 * 1024 ? PerformanceTier.Standard : PerformanceTier.Light;
        return new SystemReport(RuntimeInformation.OSDescription, win11,
            RuntimeInformation.OSArchitecture == Architecture.X64, total, FreeBytes(paths.Root), tier,
            Environment.ProcessorCount);
    }

    public static long? FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return root == null ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return null; }
    }
}
