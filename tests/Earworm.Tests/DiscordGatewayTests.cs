using System;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.EventArgs;
using Earworm.Discord;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Earworm.Tests;

public sealed class DiscordGatewayTests
{
    [Fact]
    public async Task Readiness_Follows_Ready_Close_Resume_And_Zombie_Events()
    {
        using var client = new DiscordClient(new DiscordConfiguration
        {
            Token = "placeholder",
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.AllUnprivileged,
            LoggerFactory = NullLoggerFactory.Instance,
            MinimumLogLevel = LogLevel.None
        });
        var gateway = new DiscordGateway(client, NullLogger<DiscordGateway>.Instance);

        gateway.IsReady.Should().BeFalse();

        await InvokeHandlerAsync(gateway, "OnReadyAsync", typeof(ReadyEventArgs));
        gateway.IsReady.Should().BeTrue();

        await InvokeHandlerAsync(gateway, "OnSocketClosedAsync", typeof(SocketCloseEventArgs));
        gateway.IsReady.Should().BeFalse();

        await InvokeHandlerAsync(gateway, "OnResumedAsync", typeof(ReadyEventArgs));
        gateway.IsReady.Should().BeTrue();

        await InvokeHandlerAsync(gateway, "OnZombiedAsync", typeof(ZombiedEventArgs));
        gateway.IsReady.Should().BeFalse();
    }

    private static async Task InvokeHandlerAsync(
        DiscordGateway gateway,
        string methodName,
        Type eventArgsType)
    {
        var handler = typeof(DiscordGateway).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic);
        handler.Should().NotBeNull();

        // DSharpPlus exposes some event-argument constructors as internal, so
        // instantiate them through reflection exactly as its dispatcher does.
        object eventArgs = Activator.CreateInstance(eventArgsType, nonPublic: true)!;
        var task = (Task?)handler!.Invoke(gateway, new[] { gateway.Client, eventArgs });
        task.Should().NotBeNull();
        await task!;
    }
}
