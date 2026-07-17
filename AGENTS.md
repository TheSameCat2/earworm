# Repository guide

Earworm is a .NET 10 Discord music bot. DSharpPlus handles Discord, Lavalink owns audio playback, and SQLite persists tenant state. Keep changes focused and preserve this split.

## Layout

- `src/Earworm/Program.cs`: composition root and ordered startup/shutdown.
- `src/Earworm/Discord/`: gateway listeners, permission attributes, and commands.
- `src/Earworm/Domain/`: per-guild queue, player, and AI DJ behavior.
- `src/Earworm/Persistence/`: repositories and embedded, numbered SQL migrations.
- `src/Earworm/Health/`: HTTP health, metrics, and TTS endpoints.
- `tests/Earworm.Tests/`: xUnit tests; mirror the source area being changed.
- `docs/` and `conf/`: user documentation and tracked example configuration.

## Implementation rules

- Preserve tenant isolation. Key persisted and runtime state by guild ID, create stateful engines through `PerGuildRegistry<T>`, and filter process-wide Lavalink events by guild.
- Keep Earworm's queue as the source of truth; do not introduce a second Lavalink-managed queue.
- Respect the DI lifecycle in `Program.cs`: repositories and event bridges are process-wide singletons, while queue/player/DJ engines are per guild. Constructor-subscribed listeners must be eagerly resolved before gateway events.
- Use `async`/`await` rather than blocking waits, `ILogger<T>` rather than console output, and DI-managed `HttpClient` instances rather than `new HttpClient()`.
- Prefer sealed classes and records where existing code does. Comments should explain non-obvious reasons, not restate code.
- Add a new sequential migration for schema changes; do not rewrite an applied migration. Include `guild_id` in tenant-owned data and add persistence coverage.
- For commands, preserve `[WhitelistedGuild]` and the appropriate authorization/voice attributes. Update `docs/commands.md` for user-facing command changes.
- When configuration changes, update `EarwormConfig.cs`, `conf/earworm.example.yaml`, and `docs/configuration.md`; also update deployment examples when behavior differs in containers.
- Never commit `.env`, `conf/earworm.yaml`, API tokens, databases, caches, or generated TTS files.

## Validation

Use xUnit, FluentAssertions, and NSubstitute. Add regression tests for fixes and extend composition/persistence tests when changing DI, repositories, or migrations. Unit tests must not require Discord, Lavalink, Gemini, or ElevenLabs; smoke-test those integrations manually when relevant.

Run from the repository root:

```sh
dotnet restore
dotnet build src/Earworm/Earworm.csproj --no-restore --configuration Release
dotnet test --no-restore --configuration Release --verbosity normal
```

Do not introduce new warnings; CI intentionally does not treat the repository's known warnings as errors. Run `dotnet format` when touching C# formatting.
