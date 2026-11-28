using System.Text;
using Zapret2.Core;

namespace Zapret2.Core.Tests;

public sealed class DiagnosticSessionAnalyzerTests
{
    [Fact]
    public void AnalyzeSummarizesApplicationSessionWithoutClaimingNetworkSuccess()
    {
        var path = CreateLog(
            "Zapret2 Control Center diagnostic session",
            "Started: 2026-11-28T10:00:00+01:00",
            "Session: Discord compatibility check",
            "",
            "[2026-11-28 10:00:03.000 +01:00] Engine started",
            "[2026-11-28 10:00:05.000 +01:00] Warning: test profile has broad filters",
            "[2026-11-28 10:00:09.000 +01:00] Error: WinDivert device busy",
            "[2026-11-28 10:00:10.000 +01:00] Engine stopped");

        try
        {
            var result = DiagnosticSessionAnalyzer.Analyze(path);

            Assert.Equal("Discord compatibility check", result.SessionTitle);
            Assert.Equal(4, result.TotalLines);
            Assert.Equal(1, result.PotentialErrorLines);
            Assert.Equal(1, result.PotentialWarningLines);
            Assert.Equal(TimeSpan.FromSeconds(10), result.Duration);
            Assert.Equal(64, result.Sha256.Length);
            Assert.Equal(new FileInfo(path).Length, result.SizeBytes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CompareReportsDeltasAndContentChange()
    {
        var first = CreateLog(
            "Started: 2026-11-28T10:00:00+00:00",
            "Session: before",
            "[2026-11-28 10:00:01.000 +00:00] Engine started");
        var second = CreateLog(
            "Started: 2026-11-28T11:00:00+00:00",
            "Session: after",
            "[2026-11-28 11:00:02.000 +00:00] Engine started",
            "[2026-11-28 11:00:03.000 +00:00] Warning: profile note",
            "[2026-11-28 11:00:04.000 +00:00] Error: process exited");

        try
        {
            var result = DiagnosticSessionAnalyzer.Compare(first, second);

            Assert.Equal(2, result.LineDelta);
            Assert.Equal(1, result.PotentialErrorDelta);
            Assert.Equal(1, result.PotentialWarningDelta);
            Assert.Equal(TimeSpan.FromSeconds(3), result.DurationDelta);
            Assert.True(result.ContentChanged);
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
        }
    }

    [Fact]
    public void AnalyzeRejectsInvalidUtf8()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [0xC3, 0x28]);
            Assert.Throws<DecoderFallbackException>(() => DiagnosticSessionAnalyzer.Analyze(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnalyzeRejectsOversizedFilesBeforeReadingThem()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (var stream = File.Create(path))
                stream.SetLength(DiagnosticSessionAnalyzer.MaximumFileSizeBytes + 1);
            Assert.Throws<InvalidDataException>(() => DiagnosticSessionAnalyzer.Analyze(path));
        }
        finally { File.Delete(path); }
    }

    private static string CreateLog(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "zapret2-session-" + Guid.NewGuid().ToString("N") + ".log");
        File.WriteAllText(path, string.Join(Environment.NewLine, lines), new UTF8Encoding(false));
        return path;
    }
}
