using System;
using System.Reflection;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Earworm.Config;
using Earworm.Domain.Player;

namespace Earworm.Tests.Domain.Player;

public sealed class AudioTransitionControllerTests
{
    private static EarwormConfig BuildConfigWithFade(int fadeSeconds = 5) => new()
    {
        Discord = new DiscordConfig { GuildId = "1" },
        Audio = new AudioConfig
        {
            CrossfadeSeconds = fadeSeconds,
            CrossfadeMinTrackSeconds = 0,
        },
    };

    private static CancellationTokenSource? GetCurrentLoopCts(AudioTransitionController controller)
    {
        var field = typeof(AudioTransitionController).GetField(
            "_currentLoopCts",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_currentLoopCts field must exist on AudioTransitionController");
        return (CancellationTokenSource?)field!.GetValue(controller);
    }

    private static void SetCurrentLoopCts(AudioTransitionController controller, CancellationTokenSource cts)
    {
        var field = typeof(AudioTransitionController).GetField(
            "_currentLoopCts",
            BindingFlags.Instance | BindingFlags.NonPublic);
        field.Should().NotBeNull("_currentLoopCts field must exist on AudioTransitionController");
        field!.SetValue(controller, cts);
    }

    [Fact]
    public void Cancel_DisposesCurrentCts_AfterCancelling()
    {
        // Arrange
        var config = BuildConfigWithFade();
        var controller = new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance);

        var cts = new CancellationTokenSource();
        SetCurrentLoopCts(controller, cts);

        // Verify the CTS is alive before Cancel
        var tokenBefore = cts.Token;
        tokenBefore.CanBeCanceled.Should().BeTrue();

        // Act
        controller.Cancel();

        // Assert — accessing .Token on a disposed CTS throws ObjectDisposedException
        var act = () => { _ = cts.Token; };
        act.Should().Throw<ObjectDisposedException>(
            "CancellationTokenSource must be disposed after Cancel() is called");
    }

    [Fact]
    public void Cancel_IsIdempotent_WhenCalledTwice()
    {
        // Arrange
        var config = BuildConfigWithFade();
        var controller = new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance);

        var cts = new CancellationTokenSource();
        SetCurrentLoopCts(controller, cts);

        // Act — first cancel disposes; second should not throw
        controller.Cancel();
        var secondCall = () => controller.Cancel();

        // Assert
        secondCall.Should().NotThrow("Cancel() must be safe to call multiple times");
    }

    [Fact]
    public void Cancel_ClearsFieldToNull()
    {
        // Arrange
        var config = BuildConfigWithFade();
        var controller = new AudioTransitionController(config, NullLogger<AudioTransitionController>.Instance);

        var cts = new CancellationTokenSource();
        SetCurrentLoopCts(controller, cts);

        // Act
        controller.Cancel();

        // Assert
        GetCurrentLoopCts(controller).Should().BeNull("_currentLoopCts must be null after Cancel()");
    }
}
