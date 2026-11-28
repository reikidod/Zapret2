using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Zapret2.Core;

[Flags]
public enum RuntimeComponentFlags : ulong
{
    None = 0,
    Winws = 1UL << 0,
    Winws2 = 1UL << 1,
    Blockcheck = 1UL << 2,
    Blockcheck2 = 1UL << 3,
    LuaLibrary = 1UL << 4,
    WinDivertDll = 1UL << 5,
    WinDivertDriver = 1UL << 6,
    All = Winws | Winws2 | Blockcheck | Blockcheck2 | LuaLibrary | WinDivertDll | WinDivertDriver
}

public sealed record NativeRuntimeProbeResult(
    bool NativeAvailable,
    uint? AbiVersion,
    RuntimeComponentFlags Present,
    RuntimeComponentFlags Missing,
    string? FallbackReason)
{
    public int PresentCount => CountBits((ulong)Present);
    public int TotalCount => 7;

    private static int CountBits(ulong value)
    {
        var count = 0;
        while (value != 0) { count += (int)(value & 1); value >>= 1; }
        return count;
    }
}

/// <summary>
/// Optional Rust native bridge. C# remains authoritative and always has a
/// managed fallback when the native DLL is missing, incompatible or faulty.
/// </summary>
public static class NativeRuntimeBridge
{
    public const uint SupportedAbiVersion = 1;
    public const string EmbeddedResourceName = "Zapret2.App.Resources.zapret2_native.dll";
    private const string NativeFileName = "zapret2_native.dll";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionFunction();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProbePathsFunction(IntPtr utf8Paths, out ulong mask);

    public static string? ExtractEmbeddedLibrary(Assembly assembly, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (string.IsNullOrWhiteSpace(targetDirectory))
            throw new ArgumentException("Native library directory is required.", nameof(targetDirectory));

        using var resource = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (resource is null) return null;

        var directory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, NativeFileName);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                resource.CopyTo(output);

            if (File.Exists(target))
            {
                var currentHash = SHA256.HashData(File.ReadAllBytes(target));
                var newHash = SHA256.HashData(File.ReadAllBytes(temporary));
                if (CryptographicOperations.FixedTimeEquals(currentHash, newHash))
                {
                    File.Delete(temporary);
                    return target;
                }
            }

            File.Move(temporary, target, overwrite: true);
            return target;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static NativeRuntimeProbeResult Probe(Components components, string? nativeLibraryPath)
    {
        ArgumentNullException.ThrowIfNull(components);
        var paths = BuildPaths(components);
        var managedMask = ProbeManaged(paths);

        if (string.IsNullOrWhiteSpace(nativeLibraryPath) || !File.Exists(nativeLibraryPath))
            return CreateFallback(managedMask, "Rust native library is not available.");

        IntPtr library = IntPtr.Zero;
        IntPtr input = IntPtr.Zero;
        try
        {
            library = NativeLibrary.Load(Path.GetFullPath(nativeLibraryPath));
            var abi = Marshal.GetDelegateForFunctionPointer<AbiVersionFunction>(
                NativeLibrary.GetExport(library, "z2_abi_version"))();
            if (abi != SupportedAbiVersion)
                return CreateFallback(managedMask, $"Unsupported Rust ABI version: {abi}.", abi);

            var probe = Marshal.GetDelegateForFunctionPointer<ProbePathsFunction>(
                NativeLibrary.GetExport(library, "z2_probe_paths_utf8"));
            input = Marshal.StringToCoTaskMemUTF8(string.Join("\n", paths));
            var status = probe(input, out var rawMask);
            if (status != 0)
                return CreateFallback(managedMask, $"Rust probe returned error {status}.", abi);

            var nativeMask = (RuntimeComponentFlags)(rawMask & (ulong)RuntimeComponentFlags.All);
            if (nativeMask != managedMask)
                return CreateFallback(managedMask,
                    $"C#/Rust probe mismatch (managed={managedMask}, native={nativeMask}); managed result retained.", abi);

            return new NativeRuntimeProbeResult(true, abi, nativeMask,
                RuntimeComponentFlags.All & ~nativeMask, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidOperationException or MarshalDirectiveException)
        {
            return CreateFallback(managedMask, ex.Message);
        }
        finally
        {
            if (input != IntPtr.Zero) Marshal.FreeCoTaskMem(input);
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
        }
    }

    private static string[] BuildPaths(Components components)
    {
        var binaryDirectory = components.BinaryDirectory;
        string? InBinary(string file) => string.IsNullOrWhiteSpace(binaryDirectory)
            ? null : Path.Combine(binaryDirectory, file);

        var luaFile = string.IsNullOrWhiteSpace(components.LuaDirectory)
            ? null : Path.Combine(components.LuaDirectory, "zapret-lib.lua");

        return
        [
            components.Winws ?? "",
            components.Winws2 ?? "",
            components.Blockcheck ?? "",
            components.Blockcheck2 ?? "",
            luaFile ?? "",
            InBinary("WinDivert.dll") ?? "",
            InBinary("WinDivert64.sys") ?? ""
        ];
    }

    private static RuntimeComponentFlags ProbeManaged(IReadOnlyList<string> paths)
    {
        var mask = RuntimeComponentFlags.None;
        var flags = new[]
        {
            RuntimeComponentFlags.Winws, RuntimeComponentFlags.Winws2,
            RuntimeComponentFlags.Blockcheck, RuntimeComponentFlags.Blockcheck2,
            RuntimeComponentFlags.LuaLibrary, RuntimeComponentFlags.WinDivertDll,
            RuntimeComponentFlags.WinDivertDriver
        };
        for (var index = 0; index < flags.Length; index++)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(paths[index]) && File.Exists(paths[index]))
                    mask |= flags[index];
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Invalid or inaccessible paths are treated as missing.
            }
        }
        return mask;
    }

    private static NativeRuntimeProbeResult CreateFallback(
        RuntimeComponentFlags managedMask, string reason, uint? abiVersion = null) =>
        new(false, abiVersion, managedMask, RuntimeComponentFlags.All & ~managedMask, reason);
}
