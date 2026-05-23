# Local development

Running earworm from source on your dev machine. The bot itself runs as a regular `dotnet` process; Lavalink (the audio server) runs in Docker. This is the setup you want for iterating on code — fast restarts, full debugger access, real Discord traffic.

## Prerequisites

- **.NET 10 SDK** — `dotnet --version` should print `10.x`.
- **Docker** — to run Lavalink. You don't need Docker Compose for dev unless you want it.
- **A Discord bot token, a Gemini API key, an ElevenLabs API key + voice ID** — see [discord-bot-setup.md](discord-bot-setup.md) for the bot token; the other two are obtained from the respective provider dashboards.
- **Linux only**: a firewall sane enough to let the Docker bridge talk to the host. On `ufw` you'll need one allow rule (see step 4).

## 1. Clone and build

```fish
git clone https://github.com/TheSameCat2/earworm.git
cd earworm
dotnet build src/Earworm/Earworm.csproj
```

If the build fails on package restore, your .NET SDK is too old.

## 2. Set up secrets

```fish
cp .env.example .env
$EDITOR .env
```

Fill in:

- `EARWORM_DISCORD_BOT_TOKEN` — from the Discord developer portal
- `EARWORM_GEMINI_API_KEY` — from [Google AI Studio](https://aistudio.google.com/apikey)
- `EARWORM_ELEVENLABS_API_KEY` — from [ElevenLabs](https://elevenlabs.io/app/settings/api-keys)
- `LAVALINK_PASSWORD` — leave as `youshallnotpass` for dev

The bot reads `.env` from CWD on startup if present (via DotNetEnv). Real shell env vars take precedence — handy for one-off overrides.

## 3. Fill in `conf/earworm.yaml`

You need two server-specific values:

```yaml
Discord:
  GuildId: "YOUR_DISCORD_SERVER_ID"   # right-click your server → Copy ID

Dj:
  Tts:
    VoiceId: "YOUR_ELEVENLABS_VOICE_ID"  # from elevenlabs.io/app/voice-lab
```

To get the Discord server ID: enable Developer Mode in Discord (Settings → Advanced), then right-click your server icon → Copy Server ID.

To get the ElevenLabs voice ID: in the ElevenLabs voice library, click any voice → the URL contains the ID (`/voice-lab/share/<id>` or similar) or use the API to list.

The rest of the config has working defaults — see [configuration.md](configuration.md) for the full reference.

## 4. Start Lavalink

Lavalink is a Java audio server. We use the `fredboat/lavalink:dev` image with the YouTube source plugin (configured via `conf/lavalink/application.yml`).

```fish
docker run --rm -d --name earworm-lavalink \
  -p 2333:2333 \
  --add-host host.docker.internal:host-gateway \
  -e LAVALINK_SERVER_PASSWORD=youshallnotpass \
  -e SERVER_ADDRESS=0.0.0.0 \
  -v $PWD/conf/lavalink/application.yml:/opt/Lavalink/application.yml:ro \
  fredboat/lavalink:dev
```

Two flags are non-obvious and **both are required**:

- **`--add-host host.docker.internal:host-gateway`** — lets Lavalink reach back to your host (where the bot is running) by hostname. Without this, DJ commentary can't be fetched.
- **`-v $PWD/conf/lavalink/application.yml:/opt/Lavalink/application.yml:ro`** — mounts our plugin config. Without this, Lavalink starts with default config and YouTube playback fails (Lavalink v4 doesn't include YouTube out of the box).

First boot will be slow (~30 seconds) — Lavalink downloads the YouTube plugin jar. Tail logs until you see `Started Launcher`:

```fish
docker logs -f earworm-lavalink
```

## 5. Allow the Docker bridge through `ufw` (Linux only)

If you're on a Linux distro with `ufw` enabled (CachyOS, Ubuntu Desktop, etc.), the firewall will silently drop traffic from the Docker container → host port 8080. The bot won't be able to serve DJ TTS files to Lavalink.

```fish
sudo ufw allow in on docker0 to any port 8080 proto tcp
sudo ufw reload
```

Verify Lavalink can reach the bot:

```fish
# In another terminal, start the bot first (next step), then:
docker exec earworm-lavalink wget -qO- --timeout=5 \
  "http://host.docker.internal:8080/health"
# expect: {"status":"ok"}
```

If you don't have `ufw` (or your distro uses something else), check [troubleshooting.md](troubleshooting.md#dj-commentary-doesnt-play) for the equivalent fix.

## 6. Run the bot

```fish
dotnet run --project src/Earworm/Earworm.csproj
```

You should see:

```
Initializing earworm Discord Music Bot (Lavalink edition)...
Configuration loaded and verified successfully.
[...migrations, queue hydration...]
Connected to Discord Gateway. Bot is ready.
Synchronized with 1 guilds.
Lavalink audio service started.
earworm services started. Press Ctrl+C to shut down.
```

If anything blows up before that, see [troubleshooting.md](troubleshooting.md).

## 7. Smoke test in Discord

In your Discord server:

```
/clear-worm                        # flush any stale queue from previous runs
/start-worm                        # bot joins your voice channel
/play https://www.youtube.com/watch?v=dQw4w9WgXcQ
/queue                             # confirm the track is queued + playing
/skip                              # confirm DJ-only commands work
/stop-worm                         # bot leaves voice
```

You'll need to be in a voice channel for `/start-worm` to work (or specify a channel with `/start-worm channel: #general`).

To exercise the AI DJ:

```
/djon                              # enable the DJ engine in settings
/play <track>                      # cadence is 1..4 random, may not fire on first
```

Watch the bot logs for `DJ cadence reached` and `Staged DJ commentary at ...`. The DJ TTS will play before the next music track.

## Iteration loop

```fish
# Edit code. Ctrl+C the bot. Restart:
dotnet run --project src/Earworm/Earworm.csproj
```

Lavalink doesn't need to restart for bot code changes. Restart Lavalink only if you change `conf/lavalink/application.yml` (e.g., to update the YouTube plugin version).

For faster iteration, `dotnet watch run` will hot-reload on file changes, though it can be finicky with DSharpPlus's gateway connection across reloads — restarting cleanly is usually safer.

## Tests

```fish
dotnet test
```

Should pass all 8 tests. The CompositionRootTests catch DI wiring issues that would otherwise crash the bot at startup.

## Stopping cleanly

```fish
# Bot: Ctrl+C in the dotnet run terminal — gracefully disconnects voice + gateway.
# Lavalink:
docker stop earworm-lavalink
```

Stopping Lavalink while the bot is running will spam reconnect warnings in the bot log. Stop the bot first if you can.
