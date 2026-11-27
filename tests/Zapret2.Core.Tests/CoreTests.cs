using System.Net;
using System.IO.Compression;
using System.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using Zapret2.Core;

namespace Zapret2.Core.Tests;

public sealed class CoreTests
{
    [Fact]
    public void TokenizeKeepsQuotedPathsTogether()
    {
        var args = ArgumentParser.Tokenize("--lua-init=@\"C:\\Program Files\\zapret\\x.lua\" --flag");
        Assert.Equal(2, args.Count);
        Assert.Equal("--lua-init=@C:\\Program Files\\zapret\\x.lua", args[0]);
    }

    [Fact]
    public void TokenizeRejectsUnclosedQuote()
    {
        Assert.Throws<FormatException>(() => ArgumentParser.Tokenize("--value=\"broken"));
    }

    [Fact]
    public void RenderReplacesBinAndLuaAndPreservesSpaces()
    {
        var components = new Components
        {
            Winws2 = @"C:\Program Files\zapret\winws2.exe",
            LuaDirectory = @"C:\Program Files\zapret\lua"
        };
        var result = ArgumentParser.Render("--lua-init=@\"{LUA}\\zapret-lib.lua\" --binary={BIN}\\winws2.exe", components);
        Assert.Equal(2, result.Count);
        Assert.Contains("Program Files", result[0]);
        Assert.EndsWith("zapret-lib.lua", result[0]);
    }

    [Fact]
    public void RenderRejectsUnknownVariables()
    {
        Assert.Throws<FormatException>(() => ArgumentParser.Render("--x={UNKNOWN}", new Components()));
    }

    [Fact]
    public void ImportCmdConvertsBundleVariables()
    {
        var input = "start \"zapret\" /min \"%~dp0winws2.exe\" --wf-tcp-out=80,443 ^\r\n --lua-init=@\"%LUA%zapret-lib.lua\" --lua-desync=fake --filter-l7=@\"%~dp0lua\\filter.lua\"\r\n";
        var (args, warnings) = ArgumentParser.ImportCmd(input);
        Assert.Contains("{BIN}", args);
        Assert.Contains("{LUA}", args);
        Assert.Empty(warnings);
    }

    [Fact]
    public void StorePersistsProfilesAndUniqueNames()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            store.Load();
            Assert.True(store.Config.Profiles.Count >= 8);
            store.Config.Profiles.Add(new Profile { Name = "Custom", Args = "--x" });
            Assert.Equal("Custom (2)", store.UniqueName("Custom"));
            store.Save();
            var loaded = new ProfileStore(dir);
            loaded.Load();
            Assert.Contains(loaded.Config.Profiles, p => p.Name == "Custom" && p.Args == "--x");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
    [Fact]
    public void StoreReadsPreviousPythonConfigSchema()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"),
                """{"base_dir":"C:\\zapret","active":"Custom","seeded":[],"profiles":[{"name":"Custom","args":"--wf-tcp-out=443","note":"saved"}]}""");
            var store = new ProfileStore(dir);
            store.Load();
            Assert.Equal(@"C:\zapret", store.Config.BaseDirectory);
            Assert.Equal("Custom", store.Config.ActiveProfile);
            Assert.Contains(store.Config.Profiles, p => p.Name == "Custom" && p.Note == "saved");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void StoreRestoresBackupWhenPrimaryConfigIsCorrupted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ broken json");
            File.WriteAllText(Path.Combine(dir, "config.json.bak"),
                """{"base_dir":"D:\\zapret","active":"Recovered","seeded":[],"profiles":[{"name":"Recovered","args":"--wf-tcp-out=443","note":"backup"}]}""");

            var store = new ProfileStore(dir);
            store.Load();

            Assert.Equal(@"D:\zapret", store.Config.BaseDirectory);
            Assert.Contains(store.Config.Profiles, p => p.Name == "Recovered" && p.Note == "backup");
            Assert.True(File.Exists(Path.Combine(dir, "config.broken")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ImportProfilesAddsUniqueNamesWithoutOverwritingExistingProfiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var importPath = Path.Combine(dir, "import.json");
        try
        {
            Directory.CreateDirectory(dir);
            var store = new ProfileStore(dir);
            store.Load();
            store.Config.Profiles.Add(new Profile { Name = "Custom", Args = "--existing" });
            File.WriteAllText(importPath,
                """[{"name":"Custom","args":"--imported","note":"import"},{"name":"Extra","args":"--extra"}]""");

            var added = store.ImportProfiles(importPath);

            Assert.Equal(2, added);
            Assert.Contains(store.Config.Profiles, p => p.Name == "Custom" && p.Args == "--existing");
            Assert.Contains(store.Config.Profiles, p => p.Name == "Custom (2)" && p.Args == "--imported");
            Assert.Contains(store.Config.Profiles, p => p.Name == "Extra" && p.Args == "--extra");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ExportAndImportProfilesPreservesArgumentsAndNotes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var store = new ProfileStore(Path.Combine(dir, "store"));
            store.Load();
            store.Config.Profiles.Add(new Profile { Name = "Portable test", Args = "--lua-init=@\"{LUA}\\test.lua\"", Note = "preserved" });
            var exportPath = Path.Combine(dir, "profiles.json");
            store.ExportProfiles(exportPath);

            var imported = new ProfileStore(Path.Combine(dir, "imported"));
            imported.Load();
            var count = imported.ImportProfiles(exportPath);

            Assert.True(count >= 1);
            Assert.Contains(imported.Config.Profiles, p => p.Name == "Portable test" && p.Note == "preserved" && p.Args.Contains("test.lua"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void UniqueNameNormalizesEmptyNames()
    {
        var store = new ProfileStore(Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N")));
        Assert.Equal("Новый профиль", store.UniqueName("  "));
    }

    [Fact]
    public void SaveKeepsPythonCompatibleLowercaseProfileFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            store.Load();
            store.Config.Profiles.Add(new Profile { Name = "Compat", Args = "--x", Note = "note" });
            store.Save();
            var json = File.ReadAllText(store.ConfigPath);
            Assert.Contains("\"name\": \"Compat\"", json);
            Assert.Contains("\"args\": \"--x\"", json);
            Assert.Contains("\"note\": \"note\"", json);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ImportCmdHandlesCaretContinuationsAndWarnsAboutUnknownCmdVariables()
    {
        var input = "@echo off\nset BIN=%~dp0bin\\\nstart \"z\" /min \"%BIN%winws2.exe\" --wf-tcp-out=80,443 ^\r\n  --lua-init=@\"%LUA%zapret-lib.lua\" ^\r\n  --lua-desync=fake:blob=x\n";
        var (args, warnings) = ArgumentParser.ImportCmd(input);
        Assert.Contains("--wf-tcp-out=80,443", args);
        Assert.Contains("--lua-init=@\"{LUA}\\zapret-lib.lua\"", args);
        Assert.Empty(warnings);

        var (_, unknownWarnings) = ArgumentParser.ImportCmd("winws2.exe --x=%UNKNOWN%value\n");
        Assert.NotEmpty(unknownWarnings);
        Assert.Contains("%UNKNOWN%", unknownWarnings);
    }

    [Fact]
    public void BuiltinStrategiesRenderRequiredBaseArguments()
    {
        Assert.True(StrategyCatalog.All.Count >= 8);
        Assert.Equal(StrategyCatalog.All.Count, StrategyCatalog.All.Select(s => s.Key).Distinct().Count());
        var components = new Components
        {
            Winws2 = @"C:\zapret\zapret-winws\winws2.exe",
            LuaDirectory = @"C:\zapret\zapret-winws\lua"
        };
        foreach (var strategy in StrategyCatalog.All)
        {
            var args = ArgumentParser.Render(strategy.Args, components);
            Assert.Contains("--wf-tcp-out=80,443", args);
            Assert.Contains(args, item => item.StartsWith("--lua-desync=", StringComparison.Ordinal));
            Assert.Contains("--new", args);
            Assert.False(string.IsNullOrWhiteSpace(strategy.Note));
        }
    }

    [Fact]
    public void ComponentDiscoveryFindsBundleFilesCaseInsensitively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "zapret-winws", "lua"));
            Directory.CreateDirectory(Path.Combine(dir, "blockcheck"));
            File.WriteAllText(Path.Combine(dir, "zapret-winws", "WINWS2.EXE"), "");
            File.WriteAllText(Path.Combine(dir, "zapret-winws", "lua", "Zapret-Lib.lua"), "");
            File.WriteAllText(Path.Combine(dir, "blockcheck", "blockcheck2.cmd"), "");

            var components = ComponentDiscovery.Find(dir);

            Assert.EndsWith("WINWS2.EXE", components.Winws2);
            Assert.EndsWith("lua", components.LuaDirectory);
            Assert.EndsWith("blockcheck2.cmd", components.Blockcheck2);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FinderSearchesOnlySuppliedRootsAndHonorsDepth()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var deep = Path.Combine(dir, "one", "two", "three", "four", "five");
            Directory.CreateDirectory(deep);
            var exe = Path.Combine(deep, "winws2.exe");
            File.WriteAllText(exe, "");
            Assert.Empty(WinwsFinder.Find(timeBudgetSeconds: 3, deep: false, searchRoots: [dir]));
            Assert.Contains(exe, WinwsFinder.Find(timeBudgetSeconds: 3, deep: true, searchRoots: [dir]));
            Assert.Empty(WinwsFinder.Find(timeBudgetSeconds: 0, deep: true, searchRoots: [dir]));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void RestoreBuiltinsAddsRemovedProfilesWithoutChangingCustomizedOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(dir);
            store.Load();
            var first = store.Config.Profiles[0];
            first.Args = "--my-custom-args";
            store.Config.Profiles.RemoveAt(1);
            store.Save();

            var added = store.SeedBuiltins(force: true);

            Assert.Equal(1, added);
            Assert.Equal("--my-custom-args", first.Args);
            Assert.Equal(StrategyCatalog.All.Count, store.Config.Profiles.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ImportProfilesRejectsOversizedEntryWithoutPartialMutation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var store = new ProfileStore(Path.Combine(dir, "store"));
            store.Load();
            var before = store.Config.Profiles.Count;
            var importPath = Path.Combine(dir, "bad.json");
            File.WriteAllText(importPath, System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new Profile { Name = "Would be added", Args = "--ok" },
                new Profile { Name = "Too big", Args = new string('x', 256_001) }
            }));

            Assert.Throws<InvalidDataException>(() => store.ImportProfiles(importPath));
            Assert.Equal(before, store.Config.Profiles.Count);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TokenizePreservesEmptyQuotedArgumentWhenRequested()
    {
        var tokens = ArgumentParser.Tokenize("--name=\"two words\" \"\"", keepQuotes: true);
        Assert.Equal(2, tokens.Count);
        Assert.Equal("--name=\"two words\"", tokens[0]);
        Assert.Equal("\"\"", tokens[1]);
    }

    [Fact]
    public void FinderPrefersCompleteBundleOverStandaloneExecutable()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var standalone = Path.Combine(dir, "old", "winws2.exe");
            var completeExe = Path.Combine(dir, "zapret-win-bundle", "zapret-winws", "winws2.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(standalone)!);
            Directory.CreateDirectory(Path.GetDirectoryName(completeExe)!);
            Directory.CreateDirectory(Path.Combine(dir, "zapret-win-bundle", "zapret-winws", "lua"));
            Directory.CreateDirectory(Path.Combine(dir, "zapret-win-bundle", "blockcheck"));
            File.WriteAllText(standalone, "");
            File.WriteAllText(completeExe, "");
            File.WriteAllText(Path.Combine(dir, "zapret-win-bundle", "zapret-winws", "lua", "zapret-lib.lua"), "");
            File.WriteAllText(Path.Combine(dir, "zapret-win-bundle", "blockcheck", "blockcheck2.cmd"), "");

            var found = WinwsFinder.Find(timeBudgetSeconds: 5, deep: true, searchRoots: [dir]);

            Assert.NotEmpty(found);
            Assert.Equal(completeExe, found[0]);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task RunnerCapturesBothOutputStreamsAndExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var runner = new WinwsRunner();
        var output = new ConcurrentQueue<string>();
        var exited = new TaskCompletionSource<(int? Code, bool Requested)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runner.OutputReceived += line => output.Enqueue(line);
        runner.Exited += (code, requested) => exited.TrySetResult((code, requested));

        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        runner.Start(commandProcessor,
            ["/d", "/c", "echo zapret2-stdout & echo zapret2-stderr 1>&2"]);

        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.Code);
        Assert.False(result.Requested);
        Assert.Contains(output, line => line.Contains("zapret2-stdout", StringComparison.Ordinal));
        Assert.Contains(output, line => line.Contains("zapret2-stderr", StringComparison.Ordinal));
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task RunnerStopWaitsForProcessAndOutputPump()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var runner = new WinwsRunner();
        var exited = new TaskCompletionSource<(int? Code, bool Requested)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Exited += (code, requested) => exited.TrySetResult((code, requested));

        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        runner.Start(commandProcessor, ["/d", "/c", "ping -n 30 127.0.0.1 > nul"]);
        await Task.Delay(300);

        await runner.StopAsync();
        var result = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Requested);
        Assert.False(runner.IsRunning);
    }

    private static void CreateRuntimeDependencies(string directory)
    {
        foreach (var name in new[] { "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
            File.WriteAllText(Path.Combine(directory, name), "test runtime");
    }

    [Fact]
    public void LaunchPreflightFindsMissingReferencedLuaFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var lua = Path.Combine(dir, "lua");
            Directory.CreateDirectory(lua);
            var exe = Path.Combine(dir, "winws2.exe");
            File.WriteAllText(exe, "test executable placeholder");
            CreateRuntimeDependencies(dir);
            File.WriteAllText(Path.Combine(lua, "zapret-lib.lua"), "-- test");

            var components = new Components { Winws2 = exe, LuaDirectory = lua };
            var errors = LaunchPreflight.Validate(
                "--lua-init=@\"{LUA}\\zapret-lib.lua\" --lua-init=@\"{LUA}\\zapret-antidpi.lua\"",
                components);

            Assert.Single(errors);
            Assert.Contains("zapret-antidpi.lua", errors[0], StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void LaunchPreflightDoesNotRequireLuaWhenProfileDoesNotReferenceIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var exe = Path.Combine(dir, "winws2.exe");
            File.WriteAllText(exe, "test executable placeholder");
            CreateRuntimeDependencies(dir);

            var errors = LaunchPreflight.Validate("--wf-tcp-out=80,443",
                new Components { Winws2 = exe });

            Assert.Empty(errors);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void TrafficScopeAnalyzerWarnsForGlobalPortFilters()
    {
        var warnings = TrafficScopeAnalyzer.GetWarnings(
            "--wf-tcp-out=80,443 --wf-udp-out=443 --lua-init=@\"{LUA}\\zapret-lib.lua\"");

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("других программ", StringComparison.Ordinal));
    }

    [Fact]
    public void TrafficScopeAnalyzerDoesNotWarnForProfilesWithoutPortFilters()
    {
        Assert.Empty(TrafficScopeAnalyzer.GetWarnings("--debug"));
    }

    [Fact]
    public async Task ListUpdaterBacksUpManagedFilesAndPreservesUserLists()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var lists = Path.Combine(root, "lists");
        Directory.CreateDirectory(lists);
        try
        {
            File.WriteAllText(Path.Combine(lists, "list-general.txt"), "old.shared.example\n");
            File.WriteAllText(Path.Combine(lists, "list-general-user.txt"), "my.private.example\n");

            var handler = new StaticListHandler(name => name switch
            {
                "ipset-all.txt" => "1.1.1.0/24\n",
                "ipset-exclude.txt" => "10.0.0.0/8\n",
                "list-general.txt" => "new.shared.example\n",
                "list-google.txt" => "youtube.com\n",
                "list-exclude.txt" => "steampowered.com\n",
                _ => null
            });
            using var client = new HttpClient(handler);
            using var updater = new ListUpdateService(client);

            var result = await updater.UpdateAsync(root);

            Assert.Equal(5, result.UpdatedFiles.Count);
            Assert.Equal("new.shared.example\n", File.ReadAllText(Path.Combine(lists, "list-general.txt")));
            Assert.Equal("my.private.example\n", File.ReadAllText(Path.Combine(lists, "list-general-user.txt")));
            Assert.True(Directory.Exists(result.BackupDirectory));
            Assert.Equal("old.shared.example\n",
                File.ReadAllText(Path.Combine(result.BackupDirectory, "list-general.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task ListUpdaterRejectsInvalidIpSetBeforeChangingLocalFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var lists = Path.Combine(root, "lists");
        Directory.CreateDirectory(lists);
        try
        {
            var original = Path.Combine(lists, "list-general.txt");
            File.WriteAllText(original, "keep-this.example\n");

            var handler = new StaticListHandler(name => name == "ipset-all.txt"
                ? "this-is-not-an-ip\n"
                : name switch
                {
                    "ipset-exclude.txt" => "10.0.0.0/8\n",
                    "list-general.txt" => "new.example\n",
                    "list-google.txt" => "youtube.com\n",
                    "list-exclude.txt" => "steampowered.com\n",
                    _ => null
                });
            using var client = new HttpClient(handler);
            using var updater = new ListUpdateService(client);

            await Assert.ThrowsAsync<InvalidDataException>(() => updater.UpdateAsync(root));

            Assert.Equal("keep-this.example\n", File.ReadAllText(original));
            Assert.False(Directory.Exists(Path.Combine(lists, ".backups")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SavingUserListCreatesBackupAndCannotOverwriteManagedList()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var lists = Path.Combine(root, "lists");
        Directory.CreateDirectory(lists);
        try
        {
            var userPath = Path.Combine(lists, "list-general-user.txt");
            File.WriteAllText(userPath, "old.user.example\n");

            ListUpdateService.SaveUserList(root, "list-general-user.txt", "new.user.example\n");

            Assert.Equal("new.user.example\n", File.ReadAllText(userPath));
            var backup = Directory.GetFiles(Path.Combine(lists, ".backups"), "list-general-user.txt", SearchOption.AllDirectories);
            Assert.Single(backup);
            Assert.Equal("old.user.example\n", File.ReadAllText(backup[0]));
            Assert.Throws<ArgumentException>(() =>
                ListUpdateService.SaveUserList(root, "list-general.txt", "should-not-write.example"));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private sealed class StaticListHandler(Func<string, string?> getContent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "";
            var name = path.EndsWith("/.service/ipset-service.txt", StringComparison.Ordinal)
                ? "ipset-all.txt"
                : Path.GetFileName(path);
            var content = getContent(name);
            if (content is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "text/plain")
            });
        }
    }

    [Fact]
    public void DiagnosticLogStorePersistsSessionsAndPrunesOldest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DiagnosticLogStore(directory, maxSessions: 2);
            var first = store.StartSession("first session");
            store.Append("winws2 output line");
            Assert.Contains("winws2 output line", File.ReadAllText(first));

            Thread.Sleep(20);
            var second = store.StartSession("second session");
            Thread.Sleep(20);
            var third = store.StartSession("third session");

            var sessions = store.GetSessions();
            Assert.Equal(2, sessions.Count);
            Assert.DoesNotContain(sessions, item => item.Path == first);
            Assert.Contains(sessions, item => item.Path == second);
            Assert.Contains(sessions, item => item.Path == third);
            Assert.Contains("third session", File.ReadAllText(third));
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [Fact]
    public void FlowsealImporterExtractsFullMultilineCommandWithoutExecutingBatch()
    {
        const string source = """
@echo off
cd /d "%~dp0"
start "zapret" /min "%BIN%winws2.exe" --debug=0 --wf-tcp-out=80,443,%GameFilterTCP% ^
--lua-init=@"%LUA%blobs.lua" ^
--hostlist="%LISTS%list-general.txt" ^
--new ^
--filter-tcp=%GameFilterTCP%
""";

        var args = FlowsealClassicImporter.ExtractArguments(source, "test.bat");

        Assert.Contains("--debug=0", args, StringComparison.Ordinal);
        Assert.Contains("--lua-init=@{LUA}\\blobs.lua", args, StringComparison.Ordinal);
        Assert.Contains("--hostlist={LISTS}\\list-general.txt", args, StringComparison.Ordinal);
        Assert.Contains("--wf-tcp-out=80,443,{GAMEFILTERTCP}", args, StringComparison.Ordinal);
        Assert.Contains("--filter-tcp={GAMEFILTERTCP}", args, StringComparison.Ordinal);
        Assert.DoesNotContain("%", args, StringComparison.Ordinal);
    }

    [Fact]
    public void FlowsealImporterRejectsBatchWithoutWinwsCommand()
    {
        Assert.Throws<InvalidDataException>(() =>
            FlowsealClassicImporter.ExtractArguments("echo nothing", "invalid.bat"));
    }

    [Fact]
    public void GameFilterSettingsDefaultsToDisabledWhenSettingsFileIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "bin");
        Directory.CreateDirectory(bin);
        try
        {
            var ports = GameFilterSettings.Read(bin);
            Assert.Equal("12", ports.Tcp);
            Assert.Equal("12", ports.Udp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void GameFilterSettingsReadsEnabledTcpAndUdpRanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "bin");
        var utils = Path.Combine(root, "utils");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(utils);
        try
        {
            File.WriteAllText(Path.Combine(utils, "game_filter.enabled"),
                "mode=all\ntcp=27000-27100,30000\nudp=27000-27100\n");

            var ports = GameFilterSettings.Read(bin);

            Assert.Equal("27000-27100,30000", ports.Tcp);
            Assert.Equal("27000-27100", ports.Udp);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ArgumentParserResolvesListsAndGameFilterVariables()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "bin");
        var lua = Path.Combine(root, "lua");
        var lists = Path.Combine(root, "lists");
        var utils = Path.Combine(root, "utils");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(lua);
        Directory.CreateDirectory(lists);
        Directory.CreateDirectory(utils);
        try
        {
            File.WriteAllText(Path.Combine(bin, "winws2.exe"), "");
            File.WriteAllText(Path.Combine(utils, "game_filter.enabled"), "mode=tcp\ntcp=3000-4000\n");

            var components = new Components
            {
                Winws2 = Path.Combine(bin, "winws2.exe"),
                LuaDirectory = lua
            };
            var args = ArgumentParser.Render(
                "--hostlist={LISTS}\\list-general.txt --wf-tcp-out=80,{GAMEFILTERTCP} --wf-udp-out=443,{GAMEFILTERUDP}",
                components);

            Assert.Equal($"--hostlist={Path.Combine(lists, "list-general.txt")}", args[0]);
            Assert.Equal("--wf-tcp-out=80,3000-4000", args[1]);
            Assert.Equal("--wf-udp-out=443,12", args[2]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void FlowsealClassicImporterExtractsArgumentsWithoutExecutingBatchCommands()
    {
        var script = """
            @echo off
            start "zapret" /min "%BIN%winws2.exe" --wf-tcp-out=80,%GameFilterTCP% ^
            --wf-udp-out=443,%GameFilterUDP% ^
            --lua-init=@"%LUA%zapret-lib.lua" ^
            --hostlist="%LISTS%list-general.txt"
            """;

        var args = FlowsealClassicImporter.ExtractArguments(script, "fixture.bat");

        Assert.Contains("{GAMEFILTERTCP}", args);
        Assert.Contains("{GAMEFILTERUDP}", args);
        Assert.Contains("{LUA}\\zapret-lib.lua", args);
        Assert.Contains("{LISTS}\\list-general.txt", args);
        Assert.DoesNotContain("%", args);
    }

    [Fact]
    public void FlowsealClassicImporterRejectsShellOperatorsOutsideWinwsArguments()
    {
        var script = """
            start "zapret" /min "%BIN%winws2.exe" --wf-tcp-out=80 ^
            & del important.txt
            """;

        Assert.Throws<InvalidDataException>(() => FlowsealClassicImporter.ExtractArguments(script, "unsafe.bat"));
    }

    [Fact]
    public void GameFilterSettingsReadsModesAndDefaultsToDisabledWhenFlagIsMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var binary = Path.Combine(root, "zapret-winws");
        var utils = Path.Combine(root, "utils");
        Directory.CreateDirectory(binary);
        Directory.CreateDirectory(utils);
        try
        {
            var settings = Path.Combine(utils, "game_filter.enabled");
            Assert.Equal(new GameFilterPorts("12", "12"), GameFilterSettings.Read(binary));

            File.WriteAllText(settings, "mode=all\ntcp=2053,2083-2087\nudp=50000-50100\n");
            Assert.Equal(new GameFilterPorts("2053,2083-2087", "50000-50100"), GameFilterSettings.Read(binary));

            File.WriteAllText(settings, "udp\n");
            Assert.Equal(new GameFilterPorts("12", "1024-65535"), GameFilterSettings.Read(binary));

            File.WriteAllText(settings, "mode=all\ntcp=70000\nudp=50000-50100\n");
            Assert.Equal(new GameFilterPorts("1024-65535", "50000-50100"), GameFilterSettings.Read(binary));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void ArgumentParserResolvesFlowsealListsAndGameFilterPlaceholders()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var binary = Path.Combine(root, "zapret-winws");
        var lua = Path.Combine(binary, "lua");
        var lists = Path.Combine(root, "lists");
        var utils = Path.Combine(root, "utils");
        Directory.CreateDirectory(lua);
        Directory.CreateDirectory(lists);
        Directory.CreateDirectory(utils);
        try
        {
            File.WriteAllText(Path.Combine(utils, "game_filter.enabled"), "mode=all\ntcp=1000-2000\nudp=3000-4000\n");
            var components = new Components
            {
                Winws2 = Path.Combine(binary, "winws2.exe"),
                LuaDirectory = lua
            };

            var args = ArgumentParser.Render(
                "--wf-tcp-out=80,{GAMEFILTERTCP} --wf-udp-out=443,{GAMEFILTERUDP} --hostlist=\"{LISTS}\\list-general.txt\"",
                components);

            Assert.Equal("--wf-tcp-out=80,1000-2000", args[0]);
            Assert.Equal("--wf-udp-out=443,3000-4000", args[1]);
            Assert.Equal(Path.Combine(lists, "list-general.txt"), args[2]["--hostlist=".Length..]);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void LaunchPreflightFindsMissingFlowsealUserLists()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var binary = Path.Combine(root, "zapret-winws");
        var lua = Path.Combine(binary, "lua");
        var lists = Path.Combine(root, "lists");
        Directory.CreateDirectory(lua);
        Directory.CreateDirectory(lists);
        try
        {
            var exe = Path.Combine(binary, "winws2.exe");
            File.WriteAllText(exe, "test");
            CreateRuntimeDependencies(binary);
            File.WriteAllText(Path.Combine(lua, "zapret-lib.lua"), "-- test");
            File.WriteAllText(Path.Combine(lists, "list-general.txt"), "example.com\n");
            var components = new Components { Winws2 = exe, LuaDirectory = lua };
            const string template = "--lua-init=@\"{LUA}\\zapret-lib.lua\" --hostlist=\"{LISTS}\\list-general.txt\" --hostlist=\"{LISTS}\\list-general-user.txt\"";

            var errors = LaunchPreflight.Validate(template, components);
            Assert.Single(errors);
            Assert.Contains("list-general-user.txt", errors[0], StringComparison.Ordinal);

            ListUpdateService.EnsureUserLists(root);
            Assert.Empty(LaunchPreflight.Validate(template, components));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task FlowsealClassicImporterDownloadsAllPinnedProfilesAsText()
    {
        var script = """
            start "zapret" /min "%BIN%winws2.exe" --wf-tcp-out=80,%GameFilterTCP% ^
            --lua-init=@"%LUA%zapret-lib.lua" ^
            --hostlist="%LISTS%list-general.txt"
            """;
        using var client = new HttpClient(new FlowsealScriptHandler(script));

        var profiles = await FlowsealClassicImporter.DownloadAsync(client);

        Assert.Equal(22, profiles.Count);
        Assert.All(profiles, profile =>
        {
            Assert.StartsWith("Flowseal · ", profile.Name, StringComparison.Ordinal);
            Assert.Contains("{LUA}", profile.Args);
            Assert.Contains("{LISTS}", profile.Args);
            Assert.Contains(FlowsealClassicImporter.SourceCommit, profile.Note, StringComparison.Ordinal);
            Assert.Contains("BAT/CMD не запускается", profile.Note, StringComparison.Ordinal);
        });
    }

    private sealed class FlowsealScriptHandler(string script) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!string.Equals(request.RequestUri?.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(script, Encoding.UTF8, "text/plain")
            });
        }
    }

    [Fact]
    public void ProfileImpactAnalyzerExplainsFlowsealDomainAndIpFilters()
    {
        const string profile = """
            --wf-tcp-out=80,443
            --wf-udp-out=443
            --hostlist="{LISTS}\list-general.txt"
            --hostlist-domains=discord.media
            --hostlist-exclude="{LISTS}\list-exclude.txt"
            --ipset="{LISTS}\ipset-all.txt"
            --ipset-exclude="{LISTS}\ipset-exclude.txt"
            """;

        var impact = ProfileImpactAnalyzer.Analyze(profile);
        var text = impact.ToDisplayText();

        Assert.Contains("80,443", impact.TcpPortFilters);
        Assert.Contains("list-general.txt", impact.IncludedHostlists);
        Assert.Contains("discord.media", impact.IncludedDomains);
        Assert.Contains("list-exclude.txt", impact.ExcludedHostlists);
        Assert.Contains("ipset-all.txt", impact.IncludedIpSets);
        Assert.Contains("ipset-exclude.txt", impact.ExcludedIpSets);
        Assert.Contains("Доменные исключения", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComponentDiscoveryFindsBothEnginesAndBothBlockcheckScripts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(root, "zapret-winws");
        var lua = Path.Combine(bin, "lua");
        var checks = Path.Combine(root, "blockcheck");
        Directory.CreateDirectory(lua);
        Directory.CreateDirectory(checks);
        try
        {
            foreach (var name in new[] { "winws.exe", "winws2.exe", "WinDivert.dll", "WinDivert64.sys", "cygwin1.dll" })
                File.WriteAllText(Path.Combine(bin, name), "");
            File.WriteAllText(Path.Combine(lua, "zapret-lib.lua"), "");
            File.WriteAllText(Path.Combine(checks, "blockcheck.cmd"), "");
            File.WriteAllText(Path.Combine(checks, "blockcheck2.cmd"), "");
            var found = ComponentDiscovery.Find(root);
            Assert.NotNull(found.Winws);
            Assert.NotNull(found.Winws2);
            Assert.NotNull(found.Blockcheck);
            Assert.NotNull(found.Blockcheck2);
            Assert.Equal(bin, found.BinaryDirectory);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void LaunchPreflightValidatesTheSelectedEngineAndItsRuntimeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var classic = Path.Combine(root, "winws.exe");
            var next = Path.Combine(root, "winws2.exe");
            File.WriteAllText(classic, "");
            File.WriteAllText(next, "");
            CreateRuntimeDependencies(root);
            var components = new Components { Winws = classic, Winws2 = next };
            Assert.Empty(LaunchPreflight.Validate("", components, "winws"));
            Assert.Empty(LaunchPreflight.Validate("", components, "winws2"));
            File.Delete(Path.Combine(root, "WinDivert64.sys"));
            Assert.Contains(LaunchPreflight.Validate("", components, "winws"), e => e.Contains("WinDivert64.sys", StringComparison.Ordinal));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void BundleInstallerExtractsRequiredFilesAndRejectsPathTraversal()
    {
        static MemoryStream MakeArchive(bool traversal)
        {
            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                var paths = new[]
                {
                    "zapret-winws/winws.exe", "zapret-winws/winws2.exe",
                    "zapret-winws/WinDivert.dll", "zapret-winws/WinDivert64.sys",
                    "zapret-winws/cygwin1.dll", "zapret-winws/lua/zapret-lib.lua",
                    "blockcheck/blockcheck.cmd", "blockcheck/blockcheck2.cmd",
                    "cygwin/bin/bash.exe", "cygwin/bin/cygpath.exe"
                };
                foreach (var path in paths)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(path).Open());
                    writer.Write("test");
                }
                if (traversal)
                {
                    using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open());
                    writer.Write("bad");
                }
            }
            stream.Position = 0;
            return stream;
        }

        var root = Path.Combine(Path.GetTempPath(), "Zapret2Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = MakeArchive(false);
            var installed = BundleInstaller.Install(archive, root, "test-version");
            Assert.True(File.Exists(Path.Combine(installed, "zapret-winws", "winws.exe")));
            Assert.True(File.Exists(Path.Combine(installed, "blockcheck", "blockcheck2.cmd")));
            using var second = MakeArchive(false);
            Assert.Equal(installed, BundleInstaller.Install(second, root, "test-version"));
            using var malicious = MakeArchive(true);
            Assert.Throws<InvalidDataException>(() => BundleInstaller.Install(malicious, root, "bad-version"));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }


}
