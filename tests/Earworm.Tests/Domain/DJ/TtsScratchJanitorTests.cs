using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Earworm;
using Earworm.Config;
using Earworm.Domain.DJ;

namespace Earworm.Tests.Domain.DJ;

public sealed class TtsScratchJanitorTests : IDisposable
{
    private readonly string _scratchDir;

    public TtsScratchJanitorTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), $"earworm-tts-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_scratchDir))
            Directory.Delete(_scratchDir, recursive: true);
    }

    private TtsScratchJanitor BuildJanitor(int maxAgeMinutes = 60, int maxFiles = 100)
    {
        var config = new EarwormConfig
        {
            Discord = new DiscordConfig { GuildId = "1" },
            Dj = new DjConfig
            {
                Tts = new TtsConfig { VoiceId = "test" },
                TtsScratchDirectory = _scratchDir,
                TtsScratchMaxAgeMinutes = maxAgeMinutes,
                TtsScratchMaxFiles = maxFiles,
            },
        };
        var logger = NullLogger<TtsScratchJanitor>.Instance;
        var shutdown = new ShutdownLifetime();
        return new TtsScratchJanitor(config, logger, shutdown);
    }

    private string WriteFile(string name)
    {
        var path = Path.Combine(_scratchDir, name);
        File.WriteAllText(path, "fake-mp3-data");
        return path;
    }

    // ── SweepOnStartup ──────────────────────────────────────────────────────

    [Fact]
    public void SweepOnStartup_DeletesAllMp3Files()
    {
        WriteFile("aaa.mp3");
        WriteFile("bbb.mp3");
        WriteFile("ccc.mp3");

        BuildJanitor().SweepOnStartup();

        Directory.GetFiles(_scratchDir, "*.mp3").Should().BeEmpty();
    }

    [Fact]
    public void SweepOnStartup_DoesNotDeleteNonMp3Files()
    {
        WriteFile("aaa.mp3");
        var txtPath = WriteFile("notes.txt");
        var logPath = WriteFile("debug.log");

        BuildJanitor().SweepOnStartup();

        File.Exists(txtPath).Should().BeTrue();
        File.Exists(logPath).Should().BeTrue();
    }

    [Fact]
    public void SweepOnStartup_EmptyDirectory_DoesNotThrow()
    {
        var janitor = BuildJanitor();
        var act = () => janitor.SweepOnStartup();
        act.Should().NotThrow();
    }

    [Fact]
    public void SweepOnStartup_NonExistentDirectory_DoesNotThrow()
    {
        var config = new EarwormConfig
        {
            Discord = new DiscordConfig { GuildId = "1" },
            Dj = new DjConfig
            {
                Tts = new TtsConfig { VoiceId = "test" },
                TtsScratchDirectory = Path.Combine(Path.GetTempPath(), $"nonexistent-{Guid.NewGuid():N}"),
            },
        };
        var janitor = new TtsScratchJanitor(config, NullLogger<TtsScratchJanitor>.Instance, new ShutdownLifetime());
        var act = () => janitor.SweepOnStartup();
        act.Should().NotThrow();
    }

    // ── RunRetentionPass — age-based ────────────────────────────────────────

    [Fact]
    public void RetentionPass_DeletesFilesOlderThanMaxAge()
    {
        var oldFile = WriteFile("old.mp3");
        var newFile = WriteFile("new.mp3");

        // Make old file appear 2 hours old.
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow - TimeSpan.FromHours(2));
        File.SetLastWriteTimeUtc(newFile, DateTime.UtcNow);

        BuildJanitor(maxAgeMinutes: 60).RunRetentionPass();

        File.Exists(oldFile).Should().BeFalse("file older than 60 min should be deleted");
        File.Exists(newFile).Should().BeTrue("recent file should be kept");
    }

    [Fact]
    public void RetentionPass_KeepsFilesYoungerThanMaxAge()
    {
        var recentFile = WriteFile("recent.mp3");
        File.SetLastWriteTimeUtc(recentFile, DateTime.UtcNow - TimeSpan.FromMinutes(30));

        BuildJanitor(maxAgeMinutes: 60).RunRetentionPass();

        File.Exists(recentFile).Should().BeTrue();
    }

    // ── RunRetentionPass — count-based ──────────────────────────────────────

    [Fact]
    public void RetentionPass_DeletesOldestExcessFilesWhenCountExceedsCap()
    {
        // Write 5 files with staggered mtimes (oldest first).
        var files = Enumerable.Range(1, 5).Select(i =>
        {
            var path = WriteFile($"file{i:D2}.mp3");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromMinutes(10 - i));
            return path;
        }).ToList();

        // Cap at 3 — the 2 oldest should be removed.
        BuildJanitor(maxAgeMinutes: 9999, maxFiles: 3).RunRetentionPass();

        // Oldest two (file01, file02) should be gone; newest three kept.
        File.Exists(files[0]).Should().BeFalse("oldest file should be pruned");
        File.Exists(files[1]).Should().BeFalse("second-oldest file should be pruned");
        File.Exists(files[2]).Should().BeTrue("third file should be kept");
        File.Exists(files[3]).Should().BeTrue("fourth file should be kept");
        File.Exists(files[4]).Should().BeTrue("fifth (newest) file should be kept");
    }

    [Fact]
    public void RetentionPass_DoesNotDeleteFilesWhenCountBelowCap()
    {
        WriteFile("a.mp3");
        WriteFile("b.mp3");

        BuildJanitor(maxAgeMinutes: 9999, maxFiles: 100).RunRetentionPass();

        Directory.GetFiles(_scratchDir, "*.mp3").Should().HaveCount(2);
    }

    [Fact]
    public void RetentionPass_EmptyDirectory_DoesNotThrow()
    {
        var janitor = BuildJanitor();
        var act = () => janitor.RunRetentionPass();
        act.Should().NotThrow();
    }

    [Fact]
    public void RetentionPass_SkipsFilesYoungerThanMinAge_EvenWhenCountCapIsExceeded()
    {
        // Regression: count-based retention could delete a file Lavalink is still
        // streaming when the scratch directory overflows. Files younger than the
        // minimum-age window must be protected from BOTH age and count pruning.
        // Here every file is brand-new (within the default 2-minute min-age), so
        // even though the count cap (3) is exceeded by 5 files, none are deleted.
        WriteFile("a.mp3");
        WriteFile("b.mp3");
        WriteFile("c.mp3");
        WriteFile("d.mp3");
        WriteFile("e.mp3");

        BuildJanitor(maxAgeMinutes: 9999, maxFiles: 3).RunRetentionPass();

        Directory.GetFiles(_scratchDir, "*.mp3").Should().HaveCount(5,
            "files younger than the minimum-age window are protected from the count cap");
    }
}
