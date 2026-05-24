using System;
using System.IO;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Earworm.Config;
using Earworm.Domain.Player;
using Earworm.Domain.Queue;
using Earworm.Domain.Telemetry;
using Earworm.Persistence;
using Earworm.Persistence.Schema;
using Earworm.Persistence.Repositories;

namespace Earworm.Tests.Persistence;

public sealed class PersistenceTests
{
    private async Task RunTestWithDbAsync(Func<StateStore, Task> testFunc)
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"earworm_test_{Guid.NewGuid():N}.db");
        var config = new EarwormConfig
        {
            Persistence = new PersistenceConfig
            {
                SqlitePath = dbPath,
                HistoryRetentionCount = 100
            }
        };

        var stateStoreLogger = NullLogger<StateStore>.Instance;
        var migratorLogger = NullLogger<SchemaMigrator>.Instance;

        var stateStore = new StateStore(config, stateStoreLogger);
        
        // Execute migrations
        var migrator = new SchemaMigrator(stateStore.ConnectionString, migratorLogger);
        migrator.Migrate();

        try
        {
            await testFunc(stateStore);
        }
        finally
        {
            stateStore.Dispose();
            // Force garbage collection to release file handles before deleting
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (File.Exists(dbPath))
            {
                try
                {
                    File.Delete(dbPath);
                }
                catch
                {
                    // Ignore cleanup failure
                }
            }
        }
    }

    [Fact]
    public async Task SettingsRepository_ShouldSaveAndRetrieveCorrectValues()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            // Arrange
            var repo = new SettingsRepository(stateStore);

            // Assert Defaults
            (await repo.IsDjEnabledAsync()).Should().BeFalse();
            (await repo.GetDjRoleIdAsync()).Should().BeNull();
            (await repo.GetLoggingChannelIdAsync()).Should().BeNull();

            // Act
            await repo.SetDjEnabledAsync(true);
            await repo.SetDjRoleIdAsync(123456789UL);
            await repo.SetLoggingChannelIdAsync(987654321UL);

            // Assert Updated Values
            (await repo.IsDjEnabledAsync()).Should().BeTrue();
            (await repo.GetDjRoleIdAsync()).Should().Be(123456789UL);
            (await repo.GetLoggingChannelIdAsync()).Should().Be(987654321UL);

            // Act: Reset values to null
            await repo.SetDjRoleIdAsync(null);
            await repo.SetLoggingChannelIdAsync(null);

            // Assert Resets
            (await repo.GetDjRoleIdAsync()).Should().BeNull();
            (await repo.GetLoggingChannelIdAsync()).Should().BeNull();
        });
    }

    [Fact]
    public async Task QueueRepository_ShouldManageActiveQueueAndPlaybackState()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            // Arrange
            var repo = new QueueRepository(stateStore);
            var item1 = new QueueItem
            {
                SourceType = "youtube",
                SourceId = "t1",
                Title = "Track One",
                Artist = "Artist A",
                DurationSeconds = 120,
                RequestedByUserId = "111",
                RequestedByDisplayName = "Alice",
                QueuedAt = DateTimeOffset.UtcNow,
                GuildId = "guild123"
            };

            var item2 = new QueueItem
            {
                SourceType = "youtube",
                SourceId = "t2",
                Title = "Track Two",
                Artist = "Artist B",
                DurationSeconds = 200,
                RequestedByUserId = "222",
                RequestedByDisplayName = "Bob",
                QueuedAt = DateTimeOffset.UtcNow.AddSeconds(1),
                GuildId = "guild123"
            };

            // Act: Add Tracks
            long id1 = await repo.AddTrackAsync(item1, 0);
            long id2 = await repo.AddTrackAsync(item2, 1);

            // Assert Get Queue
            var queue = await repo.GetQueueAsync();
            queue.Should().HaveCount(2);
            queue[0].Position.Should().Be(0);
            queue[0].SourceId.Should().Be("t1");
            queue[0].QueueItemId.Should().Be(id1);
            queue[1].Position.Should().Be(1);
            queue[1].SourceId.Should().Be("t2");
            queue[1].QueueItemId.Should().Be(id2);

            // Act: Move t2 to position 0 (by id, not by old position)
            await repo.MoveTrackAsync(id2, 0);

            var queueAfterMove = await repo.GetQueueAsync();
            queueAfterMove[0].SourceId.Should().Be("t2");
            queueAfterMove[0].Position.Should().Be(0);
            queueAfterMove[1].SourceId.Should().Be("t1");
            queueAfterMove[1].Position.Should().Be(1);

            // Act: Remove t2 by ID
            await repo.RemoveTrackAsync(id2);

            var queueAfterRemove = await repo.GetQueueAsync();
            queueAfterRemove.Should().HaveCount(1);
            queueAfterRemove[0].SourceId.Should().Be("t1");
            // Position gaps are intentional: the DB skips renumbering on delete.
            // QueueManager maintains dense in-memory positions for display.

            // Playback State singleton tests
            var defaultState = await repo.GetPlaybackStateAsync();
            defaultState.IsPlaying.Should().BeFalse();

            var newState = new PlaybackState
            {
                IsPlaying = true,
                IsPaused = false,
                CurrentSourceType = "youtube",
                CurrentSourceId = "playing_now",
                CurrentTitle = "Now Playing Title",
                CurrentArtist = "Some Artist",
                CurrentDurationSeconds = 300,
                CurrentRequestedByUserId = "333",
                CurrentRequestedByDisplayName = "Charlie",
                CurrentPositionMs = 5000,
                VoiceChannelId = "vc1",
                VoiceGuildId = "guild123",
                UpdatedAt = DateTimeOffset.UtcNow
            };

            await repo.UpdatePlaybackStateAsync(newState);

            var updatedState = await repo.GetPlaybackStateAsync();
            updatedState.IsPlaying.Should().BeTrue();
            updatedState.CurrentSourceId.Should().Be("playing_now");
            updatedState.CurrentPositionMs.Should().Be(5000);

            // Update position ms
            await repo.UpdatePlaybackPositionAsync(12000);
            var updatedPosState = await repo.GetPlaybackStateAsync();
            updatedPosState.CurrentPositionMs.Should().Be(12000);

            // Clear queue
            await repo.ClearQueueAsync();
            (await repo.GetQueueAsync()).Should().BeEmpty();
        });
    }

    [Fact]
    public async Task SnapshotRepository_ShouldSaveAndRestoreSuccessfully()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            // Arrange
            var queueRepo = new QueueRepository(stateStore);
            var snapshotRepo = new SnapshotRepository(stateStore);

            (await snapshotRepo.HasSnapshotAsync()).Should().BeFalse();

            // Set up active state
            var item = new QueueItem
            {
                SourceType = "youtube",
                SourceId = "queued_in_snapshot",
                Title = "Queued Track",
                Artist = "Queue Artist",
                RequestedByUserId = "u1",
                RequestedByDisplayName = "User 1",
                QueuedAt = DateTimeOffset.UtcNow,
                GuildId = "guild123"
            };
            await queueRepo.AddTrackAsync(item, 0);

            var activeState = new PlaybackState
            {
                IsPlaying = true,
                IsPaused = false,
                CurrentSourceType = "youtube",
                CurrentSourceId = "playing_in_snapshot",
                CurrentTitle = "Playing Track",
                CurrentArtist = "Playing Artist",
                CurrentDurationSeconds = 180,
                CurrentRequestedByUserId = "u2",
                CurrentRequestedByDisplayName = "User 2",
                CurrentPositionMs = 45000,
                VoiceChannelId = "vc2",
                VoiceGuildId = "guild123",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await queueRepo.UpdatePlaybackStateAsync(activeState);

            // Act: Save snapshot
            await snapshotRepo.SaveSnapshotAsync("admin1");

            // Assert snapshot exists
            (await snapshotRepo.HasSnapshotAsync()).Should().BeTrue();

            // Act: Mutate/Clear active queue & state to verify restore overwrites correctly
            await queueRepo.ClearQueueAsync();
            await queueRepo.UpdatePlaybackStateAsync(new PlaybackState
            {
                IsPlaying = false,
                IsPaused = false,
                CurrentPositionMs = 0,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            // Restore snapshot
            var restored = await snapshotRepo.RestoreSnapshotAsync();
            restored.Should().NotBeNull();

            var restoredPlay = restored!.Value.PlaybackState;
            var restoredQueue = restored.Value.QueueItems;

            // Assert restorations
            restoredPlay.IsPlaying.Should().BeTrue();
            restoredPlay.CurrentSourceId.Should().Be("playing_in_snapshot");
            restoredPlay.CurrentPositionMs.Should().Be(45000);

            restoredQueue.Should().HaveCount(1);
            restoredQueue[0].SourceId.Should().Be("queued_in_snapshot");
            restoredQueue[0].Position.Should().Be(0);

            // Check db active states are updated
            var dbPlay = await queueRepo.GetPlaybackStateAsync();
            dbPlay.CurrentSourceId.Should().Be("playing_in_snapshot");

            var dbQueue = await queueRepo.GetQueueAsync();
            dbQueue.Should().HaveCount(1);

            // Act: Clear snapshot
            await snapshotRepo.ClearSnapshotAsync();
            (await snapshotRepo.HasSnapshotAsync()).Should().BeFalse();
        });
    }

    [Fact]
    public async Task SnapshotRepository_RestoreIsFieldIdenticalToSavedState()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            var queueRepo = new QueueRepository(stateStore);
            var snapshotRepo = new SnapshotRepository(stateStore);

            var item1 = new QueueItem
            {
                SourceType = "youtube",
                SourceId = "snap_track_1",
                Title = "Snapshot Track One",
                Artist = "Snap Artist A",
                DurationSeconds = 240,
                RequestedByUserId = "u10",
                RequestedByDisplayName = "SnapUser10",
                QueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
                GuildId = "guild_snap"
            };
            var item2 = new QueueItem
            {
                SourceType = "soundcloud",
                SourceId = "snap_track_2",
                Title = null,
                Artist = null,
                DurationSeconds = null,
                RequestedByUserId = "u11",
                RequestedByDisplayName = "SnapUser11",
                QueuedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_001_000),
                GuildId = "guild_snap"
            };
            await queueRepo.AddTrackAsync(item1, 0);
            await queueRepo.AddTrackAsync(item2, 1);

            var originalState = new PlaybackState
            {
                IsPlaying = true,
                IsPaused = false,
                CurrentSourceType = "youtube",
                CurrentSourceId = "snap_playing",
                CurrentTitle = "Snap Playing Title",
                CurrentArtist = "Snap Playing Artist",
                CurrentDurationSeconds = 333,
                CurrentRequestedByUserId = "u12",
                CurrentRequestedByDisplayName = "SnapUser12",
                CurrentPositionMs = 77777,
                VoiceChannelId = "vc_snap",
                VoiceGuildId = "guild_snap",
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await queueRepo.UpdatePlaybackStateAsync(originalState);

            await snapshotRepo.SaveSnapshotAsync("snap_admin");

            // Mutate active state to something completely different
            await queueRepo.ClearQueueAsync();
            await queueRepo.UpdatePlaybackStateAsync(new PlaybackState
            {
                IsPlaying = false,
                IsPaused = false,
                CurrentSourceType = null,
                CurrentSourceId = null,
                CurrentTitle = null,
                CurrentArtist = null,
                CurrentDurationSeconds = null,
                CurrentRequestedByUserId = null,
                CurrentRequestedByDisplayName = null,
                CurrentPositionMs = 0,
                VoiceChannelId = null,
                VoiceGuildId = null,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            (await queueRepo.GetQueueAsync()).Should().BeEmpty();
            (await queueRepo.GetPlaybackStateAsync()).CurrentSourceId.Should().BeNull();

            // Restore and assert byte-identical field values
            var restored = await snapshotRepo.RestoreSnapshotAsync();
            restored.Should().NotBeNull();

            var ps = restored!.Value.PlaybackState;
            ps.IsPlaying.Should().Be(originalState.IsPlaying);
            ps.IsPaused.Should().Be(originalState.IsPaused);
            ps.CurrentSourceType.Should().Be(originalState.CurrentSourceType);
            ps.CurrentSourceId.Should().Be(originalState.CurrentSourceId);
            ps.CurrentTitle.Should().Be(originalState.CurrentTitle);
            ps.CurrentArtist.Should().Be(originalState.CurrentArtist);
            ps.CurrentDurationSeconds.Should().Be(originalState.CurrentDurationSeconds);
            ps.CurrentRequestedByUserId.Should().Be(originalState.CurrentRequestedByUserId);
            ps.CurrentRequestedByDisplayName.Should().Be(originalState.CurrentRequestedByDisplayName);
            ps.CurrentPositionMs.Should().Be(originalState.CurrentPositionMs);
            ps.VoiceChannelId.Should().Be(originalState.VoiceChannelId);
            ps.VoiceGuildId.Should().Be(originalState.VoiceGuildId);

            var qi = restored.Value.QueueItems;
            qi.Should().HaveCount(2);
            qi[0].SourceType.Should().Be(item1.SourceType);
            qi[0].SourceId.Should().Be(item1.SourceId);
            qi[0].Title.Should().Be(item1.Title);
            qi[0].Artist.Should().Be(item1.Artist);
            qi[0].DurationSeconds.Should().Be(item1.DurationSeconds);
            qi[0].RequestedByUserId.Should().Be(item1.RequestedByUserId);
            qi[0].RequestedByDisplayName.Should().Be(item1.RequestedByDisplayName);
            qi[0].QueuedAt.ToUnixTimeMilliseconds().Should().Be(item1.QueuedAt.ToUnixTimeMilliseconds());
            qi[0].GuildId.Should().Be(item1.GuildId);
            qi[0].Position.Should().Be(0);

            qi[1].SourceType.Should().Be(item2.SourceType);
            qi[1].SourceId.Should().Be(item2.SourceId);
            qi[1].Title.Should().BeNull();
            qi[1].Artist.Should().BeNull();
            qi[1].DurationSeconds.Should().BeNull();
            qi[1].RequestedByUserId.Should().Be(item2.RequestedByUserId);
            qi[1].RequestedByDisplayName.Should().Be(item2.RequestedByDisplayName);
            qi[1].QueuedAt.ToUnixTimeMilliseconds().Should().Be(item2.QueuedAt.ToUnixTimeMilliseconds());
            qi[1].GuildId.Should().Be(item2.GuildId);
            qi[1].Position.Should().Be(1);

            // Also verify no-snapshot case returns null without side effects
            await snapshotRepo.ClearSnapshotAsync();
            var noRestore = await snapshotRepo.RestoreSnapshotAsync();
            noRestore.Should().BeNull();
        });
    }

    [Fact]
    public async Task HistoryRepository_ShouldLogAndPruneHistoryCorrectly()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            // Arrange
            var repo = new HistoryRepository(stateStore);
            
            var entry1 = new PlayHistoryEntry
            {
                PlayedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                SourceType = "youtube",
                SourceId = "h1",
                Title = "Title 1",
                Artist = "Artist 1",
                DurationSeconds = 100,
                PlayedSeconds = 100,
                RequestedByUserId = "userA",
                RequestedByDisplayName = "User A",
                Skipped = false,
                Failed = false,
                GuildId = "guild123"
            };

            var entry2 = new PlayHistoryEntry
            {
                PlayedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                SourceType = "youtube",
                SourceId = "h2",
                Title = "Title 2",
                Artist = "Artist 2",
                DurationSeconds = 200,
                PlayedSeconds = 50,
                RequestedByUserId = "userB",
                RequestedByDisplayName = "User B",
                Skipped = true,
                Failed = false,
                GuildId = "guild123"
            };

            var entry3 = new PlayHistoryEntry
            {
                PlayedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                SourceType = "youtube",
                SourceId = "h3",
                Title = "Title 3",
                Artist = "Artist 3",
                DurationSeconds = 150,
                PlayedSeconds = 10,
                RequestedByUserId = "userA",
                RequestedByDisplayName = "User A",
                Skipped = false,
                Failed = true,
                FailureReason = "FFmpeg extraction error",
                GuildId = "guild123"
            };

            // Act & Assert insertions
            await repo.AddHistoryEntryAsync(entry1, retentionCount: 3);
            await repo.AddHistoryEntryAsync(entry2, retentionCount: 3);
            await repo.AddHistoryEntryAsync(entry3, retentionCount: 3);

            var recent = await repo.GetRecentHistoryAsync(limit: 10);
            recent.Should().HaveCount(3);
            recent[0].SourceId.Should().Be("h3"); // played_at desc
            recent[1].SourceId.Should().Be("h2");
            recent[2].SourceId.Should().Be("h1");
            recent[1].Skipped.Should().BeTrue();
            recent[0].Failed.Should().BeTrue();
            recent[0].FailureReason.Should().Be("FFmpeg extraction error");

            // Act: Insert a 4th entry with retention limit of 3
            var entry4 = new PlayHistoryEntry
            {
                PlayedAt = DateTimeOffset.UtcNow,
                SourceType = "youtube",
                SourceId = "h4",
                Title = "Title 4",
                Artist = "Artist 4",
                RequestedByUserId = "userC",
                RequestedByDisplayName = "User C",
                GuildId = "guild123"
            };
            await repo.AddHistoryEntryAsync(entry4, retentionCount: 3);

            // Assert that h1 was pruned (since retention cap is 3 and h4 is newest)
            var postPrune = await repo.GetRecentHistoryAsync(limit: 10);
            postPrune.Should().HaveCount(3);
            postPrune[0].SourceId.Should().Be("h4");
            postPrune[1].SourceId.Should().Be("h3");
            postPrune[2].SourceId.Should().Be("h2");

            var h1Search = postPrune.Find(x => x.SourceId == "h1");
            h1Search.Should().BeNull();
        });
    }

    [Fact]
    public async Task MetricsRepository_ShouldLogGlobalAndUserMetricsCorrectly()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            // Arrange
            var repo = new MetricsRepository(stateStore);

            // 1. Global Metrics
            await repo.IncrementGlobalMetricAsync("songs_played", 1);
            await repo.IncrementGlobalMetricAsync("songs_played", 2);
            await repo.IncrementGlobalMetricAsync("bytes_streamed", 500000);

            (await repo.GetGlobalMetricAsync("songs_played")).Should().Be(3);
            (await repo.GetGlobalMetricAsync("bytes_streamed")).Should().Be(500000);
            (await repo.GetGlobalMetricAsync("non_existent")).Should().Be(0);

            var globalDict = await repo.GetGlobalMetricsAsync();
            globalDict.Should().HaveCount(2);
            globalDict["songs_played"].Should().Be(3);
            globalDict["bytes_streamed"].Should().Be(500000);

            // 2. User Metrics (UPSERT validation)
            await repo.IncrementUserMetricAsync("user1", "Alice", "tracks_queued", 1);
            await repo.IncrementUserMetricAsync("user1", "Alice", "tracks_queued", 2);
            await repo.IncrementUserMetricAsync("user1", "Alice", "listening_seconds", 300);
            await repo.IncrementUserMetricAsync("user1", "Alice", "requests_youtube", 3);

            await repo.IncrementUserMetricAsync("user2", "Bob", "tracks_queued", 5);
            await repo.IncrementUserMetricAsync("user2", "Bob", "listening_seconds", 1200);

            var user1Metrics = await repo.GetUserMetricsAsync("user1");
            user1Metrics.Should().NotBeNull();
            user1Metrics!.DisplayNameLastSeen.Should().Be("Alice");
            user1Metrics.TracksQueued.Should().Be(3);
            user1Metrics.ListeningSeconds.Should().Be(300);
            user1Metrics.RequestsYoutube.Should().Be(3);
            user1Metrics.RequestsSoundcloud.Should().Be(0); // default

            var user2Metrics = await repo.GetUserMetricsAsync("user2");
            user2Metrics!.DisplayNameLastSeen.Should().Be("Bob");
            user2Metrics.TracksQueued.Should().Be(5);
            user2Metrics.ListeningSeconds.Should().Be(1200);

            // Top Users Lists
            var topListening = await repo.GetTopUsersByListeningTimeAsync(5);
            topListening.Should().HaveCount(2);
            topListening[0].UserId.Should().Be("user2"); // 1200 seconds > 300 seconds
            topListening[1].UserId.Should().Be("user1");

            var topQueuers = await repo.GetTopUsersByTracksQueuedAsync(5);
            topQueuers.Should().HaveCount(2);
            topQueuers[0].UserId.Should().Be("user2"); // 5 tracks > 3 tracks
            topQueuers[1].UserId.Should().Be("user1");

            // Invalid column validation
            Func<Task> act = async () => await repo.IncrementUserMetricAsync("user1", "Alice", "invalid_col_name", 1);
            await act.Should().ThrowAsync<ArgumentException>();
        });
    }

    [Fact]
    public async Task QueueRepository_MoveTrack_AcrossLargeQueue_ProducesCorrectOrder()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            const int N = 50;
            var repo = new QueueRepository(stateStore);

            // Insert N tracks with sequential positions
            var ids = new long[N];
            for (int i = 0; i < N; i++)
            {
                ids[i] = await repo.AddTrackAsync(new QueueItem
                {
                    SourceType = "youtube",
                    SourceId = $"track_{i}",
                    Title = $"Track {i}",
                    Artist = "Artist",
                    RequestedByUserId = "u1",
                    RequestedByDisplayName = "User",
                    QueuedAt = DateTimeOffset.UtcNow,
                    GuildId = "g1"
                }, i);
            }

            // Move the last item (index 49) to the front (position 0)
            await repo.MoveTrackAsync(ids[N - 1], 0);

            var queue = await repo.GetQueueAsync();
            queue.Should().HaveCount(N);
            queue[0].SourceId.Should().Be($"track_{N - 1}", "last track moved to front");
            for (int i = 1; i < N; i++)
            {
                queue[i].SourceId.Should().Be($"track_{i - 1}", $"original track {i - 1} should now be at index {i}");
            }

            // Move item from index 0 (track_49) to the middle (position 25)
            await repo.MoveTrackAsync(ids[N - 1], 25);

            var queue2 = await repo.GetQueueAsync();
            queue2.Should().HaveCount(N);
            queue2[25].SourceId.Should().Be($"track_{N - 1}", "moved item should be at index 25");
            // Items before the moved item: track_0..track_24
            for (int i = 0; i < 25; i++)
            {
                queue2[i].SourceId.Should().Be($"track_{i}");
            }
            // Items after the moved item: track_25..track_48
            for (int i = 26; i < N; i++)
            {
                queue2[i].SourceId.Should().Be($"track_{i - 1}");
            }
        });
    }

    [Fact]
    public async Task QueueRepository_RemoveTrack_LeavesGapsAndOrderIsPreservedByPosition()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            const int N = 20;
            var repo = new QueueRepository(stateStore);

            var ids = new long[N];
            for (int i = 0; i < N; i++)
            {
                ids[i] = await repo.AddTrackAsync(new QueueItem
                {
                    SourceType = "youtube",
                    SourceId = $"track_{i}",
                    Title = $"Track {i}",
                    Artist = "Artist",
                    RequestedByUserId = "u1",
                    RequestedByDisplayName = "User",
                    QueuedAt = DateTimeOffset.UtcNow,
                    GuildId = "g1"
                }, i);
            }

            // Dequeue first 5 items (simulating front-of-queue pops)
            for (int i = 0; i < 5; i++)
            {
                await repo.RemoveTrackAsync(ids[i]);
            }

            // Remaining items must be in correct relative order by position column,
            // even though positions are no longer dense (gaps exist).
            var remaining = await repo.GetQueueAsync();
            remaining.Should().HaveCount(N - 5);
            for (int i = 0; i < remaining.Count; i++)
            {
                remaining[i].SourceId.Should().Be($"track_{i + 5}", $"item at index {i} should be track_{i + 5}");
            }

            // Compact positions and verify dense 0-based assignment
            await repo.CompactPositionsAsync();
            var compacted = await repo.GetQueueAsync();
            compacted.Should().HaveCount(N - 5);
            for (int i = 0; i < compacted.Count; i++)
            {
                compacted[i].Position.Should().Be(i, $"after compact, position at index {i} must equal {i}");
                compacted[i].SourceId.Should().Be($"track_{i + 5}");
            }
        });
    }

    [Fact]
    public async Task MetricsRepository_IncrementBatchAsync_ShouldApplyAllIncrementsInOneRoundTrip()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            var repo = new MetricsRepository(stateStore);

            // 5 increments: 2 user (different columns), 1 user listening_seconds, 2 global.
            var batch = new List<MetricIncrement>
            {
                new MetricIncrement("tracks_queued",    2,   "userA", "Alice"),
                new MetricIncrement("listening_seconds", 90, "userA", "Alice"),
                new MetricIncrement("requests_youtube",  3,  "userA", "Alice"),
                new MetricIncrement("tracks_queued",    2),
                new MetricIncrement("listening_seconds", 90),
            };

            await repo.IncrementBatchAsync(batch);

            // Per-user assertions
            var userA = await repo.GetUserMetricsAsync("userA");
            userA.Should().NotBeNull();
            userA!.TracksQueued.Should().Be(2);
            userA.ListeningSeconds.Should().Be(90);
            userA.RequestsYoutube.Should().Be(3);

            // Global assertions
            (await repo.GetGlobalMetricAsync("tracks_queued")).Should().Be(2);
            (await repo.GetGlobalMetricAsync("listening_seconds")).Should().Be(90);

            // A second batch accumulates correctly (upsert semantics)
            await repo.IncrementBatchAsync(new[]
            {
                new MetricIncrement("tracks_queued", 1, "userA", "Alice"),
                new MetricIncrement("tracks_queued", 1),
            });

            var userAAfter = await repo.GetUserMetricsAsync("userA");
            userAAfter!.TracksQueued.Should().Be(3);
            (await repo.GetGlobalMetricAsync("tracks_queued")).Should().Be(3);

            // Invalid column should throw before reaching the DB
            Func<Task> badBatch = async () => await repo.IncrementBatchAsync(new[]
            {
                new MetricIncrement("bad_column", 1, "userA", "Alice"),
            });
            await badBatch.Should().ThrowAsync<ArgumentException>();
        });
    }

    [Fact]
    public async Task StateStore_ConcurrentWrites_ShouldExecuteSequentiallyWithoutLockingExceptions()
    {
        await RunTestWithDbAsync(async stateStore =>
        {
            // Arrange
            var repo = new MetricsRepository(stateStore);
            const int totalTasks = 100;
            var tasks = new List<Task>();

            // Act: Enqueue 100 concurrent write increments
            for (int i = 0; i < totalTasks; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    await repo.IncrementGlobalMetricAsync("concurrent_counter", 1);
                }));
            }

            // Await all tasks to finish writing concurrently
            await Task.WhenAll(tasks);

            // Assert: Confirm counter matches exactly, and zero SQLITE_BUSY exceptions occurred
            var finalCount = await repo.GetGlobalMetricAsync("concurrent_counter");
            finalCount.Should().Be(totalTasks);
        });
    }
}
