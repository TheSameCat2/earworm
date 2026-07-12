using System;
using System.Collections.Generic;
using System.IO;
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
using Earworm.Domain.Tenants;
using Earworm.Health;
using Earworm.Persistence;
using Earworm.Persistence.Repositories;

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

    private static EarwormConfig BuildConfig(int port, string sqlitePath) => new()
    {
        Ops = new OpsConfig { HttpPort = port },
        Persistence = new PersistenceConfig { SqlitePath = sqlitePath }
    };

    private static async Task<(HttpStatusCode Status, JsonDocument Body)> ProbeAsync(int port, string path = "/health")
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var resp = await http.GetAsync($"http://127.0.0.1:{port}{path}");
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text));
    }

    private sealed class TestEndpoint : IAsyncDisposable
    {
        private readonly string _dbPath;

        public int Port { get; }
        public StateStore Store { get; }
        public DiscordGateway Gateway { get; }
        public HealthEndpoint Endpoint { get; }

        public TestEndpoint(bool discordReady, bool lavalinkReady, ITenantService? tenants = null)
        {
            Port = FindFreePort();
            _dbPath = Path.Combine(Path.GetTempPath(), $"earworm-health-{Guid.NewGuid():N}.db");
            var config = BuildConfig(Port, _dbPath);

            if (tenants is null)
            {
                tenants = Substitute.For<ITenantService>();
                tenants.GetAllTenantsAsync().Returns(
                    Task.FromResult<IReadOnlyList<TenantRow>>(Array.Empty<TenantRow>()));
            }

            Store = new StateStore(config, NullLogger<StateStore>.Instance);
            Gateway = BuildGateway(discordReady);
            Endpoint = new HealthEndpoint(
                config,
                Gateway,
                BuildAudioService(lavalinkReady),
                tenants,
                Store,
                NullLoggerFactory.Instance,
                NullLogger<HealthEndpoint>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await Endpoint.DisposeAsync();
            Store.Dispose();
            Gateway.Client.Dispose();
            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try { File.Delete(_dbPath + suffix); } catch { /* best-effort test cleanup */ }
            }
        }
    }

    [Fact]
    public async Task Health_Returns_200_When_Both_Discord_And_Lavalink_Ready()
    {
        await using var endpoint = new TestEndpoint(discordReady: true, lavalinkReady: true);
        await endpoint.Endpoint.StartAsync();

        var (status, body) = await ProbeAsync(endpoint.Port);
        status.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("ok");
        body.RootElement.GetProperty("discord").GetString().Should().Be("ok");
        body.RootElement.GetProperty("lavalink").GetString().Should().Be("ok");
        body.RootElement.GetProperty("tenantStore").GetString().Should().Be("ok");
        body.RootElement.GetProperty("writer").GetString().Should().Be("ok");
        body.RootElement.GetProperty("pendingWrites").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Health_Returns_503_Degraded_When_Lavalink_Down()
    {
        await using var endpoint = new TestEndpoint(discordReady: true, lavalinkReady: false);
        await endpoint.Endpoint.StartAsync();

        var (status, body) = await ProbeAsync(endpoint.Port);
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.RootElement.GetProperty("status").GetString().Should().Be("degraded");
        body.RootElement.GetProperty("discord").GetString().Should().Be("ok");
        body.RootElement.GetProperty("lavalink").GetString().Should().Be("down");
    }

    [Fact]
    public async Task Health_Returns_503_Starting_When_Discord_Not_Ready()
    {
        await using var endpoint = new TestEndpoint(discordReady: false, lavalinkReady: true);
        await endpoint.Endpoint.StartAsync();

        var (status, body) = await ProbeAsync(endpoint.Port);
        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.RootElement.GetProperty("status").GetString().Should().Be("starting");
        body.RootElement.GetProperty("discord").GetString().Should().Be("starting");
        body.RootElement.GetProperty("lavalink").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Live_Returns_200_Without_Probing_Downstream_Dependencies()
    {
        var tenants = Substitute.For<ITenantService>();
        tenants.GetAllTenantsAsync().Returns(
            Task.FromException<IReadOnlyList<TenantRow>>(new IOException("database unavailable")));
        await using var endpoint = new TestEndpoint(
            discordReady: false,
            lavalinkReady: false,
            tenants);
        await endpoint.Endpoint.StartAsync();

        var (status, body) = await ProbeAsync(endpoint.Port, "/live");

        status.Should().Be(HttpStatusCode.OK);
        body.RootElement.GetProperty("status").GetString().Should().Be("ok");
        await tenants.DidNotReceive().GetAllTenantsAsync();
    }

    [Fact]
    public async Task Health_Returns_503_When_Tenant_Store_Is_Down()
    {
        var tenants = Substitute.For<ITenantService>();
        tenants.GetAllTenantsAsync().Returns(
            Task.FromException<IReadOnlyList<TenantRow>>(new IOException("database unavailable")));
        await using var endpoint = new TestEndpoint(
            discordReady: true,
            lavalinkReady: true,
            tenants);
        await endpoint.Endpoint.StartAsync();

        var (status, body) = await ProbeAsync(endpoint.Port);

        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.RootElement.GetProperty("status").GetString().Should().Be("degraded");
        body.RootElement.GetProperty("tenants").GetInt32().Should().Be(-1);
        body.RootElement.GetProperty("tenantStore").GetString().Should().Be("down");
    }

    [Fact]
    public async Task Health_Returns_503_When_Sqlite_Writer_Is_Down()
    {
        await using var endpoint = new TestEndpoint(discordReady: true, lavalinkReady: true);
        endpoint.Store.Dispose();
        await endpoint.Endpoint.StartAsync();

        var (status, body) = await ProbeAsync(endpoint.Port);

        status.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.RootElement.GetProperty("status").GetString().Should().Be("degraded");
        body.RootElement.GetProperty("writer").GetString().Should().Be("down");
    }
}
