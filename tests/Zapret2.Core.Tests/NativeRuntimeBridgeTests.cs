using Zapret2.Core;
using Xunit;

namespace Zapret2.Core.Tests;

public sealed class NativeRuntimeBridgeTests
{
    [Fact]
    public void MissingRustLibraryUsesManagedFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2NativeTests", Guid.NewGuid().ToString("N"));
        var binary = Path.Combine(root, "zapret-winws");
        var lua = Path.Combine(binary, "lua");
        var blockcheck = Path.Combine(root, "blockcheck");
        Directory.CreateDirectory(lua);
        Directory.CreateDirectory(blockcheck);

        try
        {
            var winws2 = Path.Combine(binary, "winws2.exe");
            var luaFile = Path.Combine(lua, "zapret-lib.lua");
            var blockcheck2 = Path.Combine(blockcheck, "blockcheck2.cmd");
            File.WriteAllText(winws2, "test");
            File.WriteAllText(luaFile, "-- test");
            File.WriteAllText(blockcheck2, "@echo off");

            var components = new Components
            {
                Winws2 = winws2,
                LuaDirectory = lua,
                Blockcheck2 = blockcheck2
            };

            var result = NativeRuntimeBridge.Probe(components, Path.Combine(root, "missing-native.dll"));

            Assert.False(result.NativeAvailable);
            Assert.Equal(3, result.PresentCount);
            Assert.Equal(RuntimeComponentFlags.Winws2 | RuntimeComponentFlags.Blockcheck2 |
                RuntimeComponentFlags.LuaLibrary, result.Present);
            Assert.Equal(RuntimeComponentFlags.All & ~result.Present, result.Missing);
            Assert.Contains("not available", result.FallbackReason);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NativeProbeMatchesManagedProbeWhenRustLibraryIsBuilt()
    {
        var nativeLibrary = Path.Combine(AppContext.BaseDirectory, "zapret2_native.dll");
        if (!File.Exists(nativeLibrary)) return; // Local C#-only builds retain the managed fallback.

        var root = Path.Combine(Path.GetTempPath(), "Zapret2NativeParity", Guid.NewGuid().ToString("N"));
        var binary = Path.Combine(root, "zapret-winws");
        var lua = Path.Combine(binary, "lua");
        var blockcheck = Path.Combine(root, "blockcheck");
        Directory.CreateDirectory(lua);
        Directory.CreateDirectory(blockcheck);

        try
        {
            var winws = Path.Combine(binary, "winws.exe");
            var winws2 = Path.Combine(binary, "winws2.exe");
            var blockcheck1 = Path.Combine(blockcheck, "blockcheck.cmd");
            var blockcheck2 = Path.Combine(blockcheck, "blockcheck2.cmd");
            var luaFile = Path.Combine(lua, "zapret-lib.lua");
            var divertDll = Path.Combine(binary, "WinDivert.dll");
            var divertDriver = Path.Combine(binary, "WinDivert64.sys");
            foreach (var path in new[] { winws, winws2, blockcheck1, blockcheck2, luaFile, divertDll, divertDriver })
                File.WriteAllText(path, "test");

            var components = new Components
            {
                Winws = winws,
                Winws2 = winws2,
                Blockcheck = blockcheck1,
                Blockcheck2 = blockcheck2,
                LuaDirectory = lua
            };

            var result = NativeRuntimeBridge.Probe(components, nativeLibrary);

            Assert.True(result.NativeAvailable, result.FallbackReason);
            Assert.Equal(RuntimeComponentFlags.All, result.Present);
            Assert.Equal(7, result.PresentCount);
            Assert.Equal(RuntimeComponentFlags.None, result.Missing);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EmptyComponentsReportAllRuntimeFilesMissing()
    {
        var result = NativeRuntimeBridge.Probe(new Components(), null);

        Assert.False(result.NativeAvailable);
        Assert.Equal(0, result.PresentCount);
        Assert.Equal(7, result.TotalCount);
        Assert.Equal(RuntimeComponentFlags.All, result.Missing);
    }
}
