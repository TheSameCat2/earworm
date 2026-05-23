using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using DSharpPlus;
using Lavalink4NET;
using Earworm.Config;
using Earworm.Discord;
using Earworm.Health;

namespace Earworm.Tests;

/// <summary>
/// Black-box tests for the /health endpoint. Spins up the real ASP.NET host on
/// an ephemeral port and probes it over HTTP, mocking the dependencies whose
/// readiness drives the response.
/// </summary>
public sealed class HealthEndpointTests
{
    private static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static DiscordGateway BuildGateway(bool ready)
    {
        // DiscordGateway is sealed and IsReady is set from a private field via
        // a gateway event. Build the real instance with a placeholder client and
        // flip _isReady via reflection so we test the property the endpoint reads.
        var client = new DiscordClient(new DiscordConfiguration
        {
            Token = "placeholder",
            TokenType = TokenType.Bot,
            Intents = DiscordIntents.AllUnprivileged,
            LoggerFactory = NullLoggerFactory.Instance,
            MinimumLogLevel = LogLevel.None
        });
        var gateway = new DiscordGateway(client, NullLogger<DiscordGateway>.Instance);
        if (ready)
        {
            var field = typeof(DiscordGateway).GetField("_isReady", BindingFlags.NonPublic | BindingFlags.Instance);
            field!.SetValue(gateway, true);
        }
        return gateway;
    }

    private static IAudioService BuildAudioService(bool ready)
    {
        var svc = Substitute.For<IAudioService>();
        if (ready)
        {
            svc.WaitForReadyAsync(Arg.Any<CancellationToken>()).Returns(ValueTask.CompletedTask);
        }
        else
        {
            // Pending task — IsCompletedSuccessfully will be false.
            var tcs = new TaskCompletionSource();
            svc.WaitForReadyAsync(Arg.Any<CancellationToken>()).Returns(new ValueTask(tcs.Task));
        }
        return svc;
    }

    private static EarwormConfig BuildConfig(int port) => new()
    {
        Ops = new OpsConfig { HttpPort = port }
    };

    private static async Task<(HttpStatusCode Status, JsonDocument Body)> ProbeAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await http.GetAsync($"http://127.0.0.1:{port}/health");
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text));
    }

    [Fact]
    public async Task Health_Returns_200_When_Both_Discord_And_Lavalink_Ready()
    {
        int port = FindFreePort();
        var endpoint = new HealthEndpoint(
            BuildConfig(port),
            BuildGateway(ready: true),
            BuildAudioService(ready: true),
            NullLoggerFactory.Instance,
            NullLogger<HealthEndpoint>.Instance);

        await endpoint.StartAsync();
        try
        {
            var (status, body) = await ProbeAsync(port);
            status.Should().Be(HttpStatusCode.OK);
            body.RootElement.GetProperty("status").GetString().Should().Be("ok");
            body.RootElement.GetProperty("discord").GetString().Should().Be("ok");
            body.RootElement.GetProperty("lavalink").GetString().Should().Be("ok");
        }
        finally
        {
            await endpoint.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_Returns_503_Degraded_When_Lavalink_Down()
    {
        int port = FindFreePort();
        var endpoint = new HealthEndpoint(
            BuildConfig(port),
            BuildGateway(ready: true),
            BuildAudioService(ready: false),
            NullLoggerFactory.Instance,
            NullLogger<HealthEndpoint>.Instance);

        await endpoint.StartAsync();
        try
        {
            var (status, body) = await ProbeAsync(port);
            status.Should().Be(HttpStatusCode.ServiceUnavailable);
            body.RootElement.GetProperty("status").GetString().Should().Be("degraded");
            body.RootElement.GetProperty("discord").GetString().Should().Be("ok");
            body.RootElement.GetProperty("lavalink").GetString().Should().Be("down");
        }
        finally
        {
            await endpoint.DisposeAsync();
        }
    }

    [Fact]
    public async Task Health_Returns_503_Starting_When_Discord_Not_Ready()
    {
        int port = FindFreePort();
        var endpoint = new HealthEndpoint(
            BuildConfig(port),
            BuildGateway(ready: false),
            BuildAudioService(ready: true),
            NullLoggerFactory.Instance,
            NullLogger<HealthEndpoint>.Instance);

        await endpoint.StartAsync();
        try
        {
            var (status, body) = await ProbeAsync(port);
            status.Should().Be(HttpStatusCode.ServiceUnavailable);
            body.RootElement.GetProperty("status").GetString().Should().Be("starting");
            body.RootElement.GetProperty("discord").GetString().Should().Be("starting");
            body.RootElement.GetProperty("lavalink").GetString().Should().Be("ok");
        }
        finally
        {
            await endpoint.DisposeAsync();
        }
    }
}
