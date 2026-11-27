using System.IO.Compression;
using System.Text;

namespace Zapret2.Core;

/// <summary>Safely installs the runtime bundle embedded in the portable application.</summary>
public static class BundleInstaller
{
    private static readonly string[] RequiredFiles =
    [
        Path.Combine("zapret-winws", "winws.exe"),
        Path.Combine("zapret-winws", "winws2.exe"),
        Path.Combine("zapret-winws", "WinDivert.dll"),
        Path.Combine("zapret-winws", "WinDivert64.sys"),
        Path.Combine("zapret-winws", "cygwin1.dll"),
        Path.Combine("zapret-winws", "lua", "zapret-lib.lua"),
        Path.Combine("blockcheck", "blockcheck.cmd"),
        Path.Combine("blockcheck", "blockcheck2.cmd"),
        Path.Combine("cygwin", "bin", "bash.exe"),
        Path.Combine("cygwin", "bin", "cygpath.exe")
    ];

    public static string Install(Stream archiveStream, string installRoot, string version)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        if (string.IsNullOrWhiteSpace(installRoot)) throw new ArgumentException("Install root is required.", nameof(installRoot));
        if (string.IsNullOrWhiteSpace(version) || version.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Bundle version is invalid.", nameof(version));
        var final = Path.Combine(Path.GetFullPath(installRoot), "bundle-" + version);
        if (IsValid(final, version)) return final;
        Directory.CreateDirectory(installRoot);
        var staging = final + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);
            long totalBytes = 0;
            var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.FullName)) continue;
                var normalized = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalized) || normalized.Split(Path.DirectorySeparatorChar).Any(part => part == "..") || normalized.Contains(':'))
                    throw new InvalidDataException("Bundle archive contains an unsafe path.");
                var destination = Path.GetFullPath(Path.Combine(staging, normalized));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Bundle archive entry escapes the installation directory.");
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType == 0xA000) throw new InvalidDataException("Symbolic links are not allowed in the runtime bundle.");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(destination); continue; }
                totalBytes += entry.Length;
                if (totalBytes > 300L * 1024 * 1024) throw new InvalidDataException("Runtime bundle exceeds the 300 MiB safety limit.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            foreach (var relative in RequiredFiles)
                if (!File.Exists(Path.Combine(staging, relative)))
                    throw new InvalidDataException("Runtime bundle is incomplete: " + relative);
            File.WriteAllText(Path.Combine(staging, "bundle.version"), version, new UTF8Encoding(false));
            var backup = final + ".previous";
            if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            if (Directory.Exists(final)) Directory.Move(final, backup);
            try
            {
                Directory.Move(staging, final);
                if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
            }
            catch
            {
                if (!Directory.Exists(final) && Directory.Exists(backup)) Directory.Move(backup, final);
                throw;
            }
            return final;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static bool IsValid(string directory, string version)
    {
        if (!File.Exists(Path.Combine(directory, "bundle.version"))) return false;
        try
        {
            if (!File.ReadAllText(Path.Combine(directory, "bundle.version")).Equals(version, StringComparison.Ordinal)) return false;
            return RequiredFiles.All(path => File.Exists(Path.Combine(directory, path)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
