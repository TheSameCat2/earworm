# Deployment

Running earworm in production. The reference deployment is **Docker Compose** with the bot and Lavalink in a shared compose network. There are notes for Unraid (the original target) further down.

## Two ways to get the bot image

| Path | When to use | Files needed |
|---|---|---|
| **Pull from GHCR** | You just want to host earworm without touching the code | One of the compose files in [`docs/examples/`](examples/) |
| **Build from source** | You're contributing or modifying the code | The root `docker-compose.yml` (uses `build: context: .`) |

Both paths produce a working bot — the pull-from-GHCR path is faster (no .NET SDK needed on the host) and what most self-hosters want.

GitHub Actions publishes the image to `ghcr.io/<owner>/earworm:latest` on every push to `main`, and to `:vX.Y.Z` for tagged releases. Pin to a version tag for production; use `:latest` if you don't mind the occasional surprise.

## Prerequisites

- **Docker + Docker Compose v2** (`docker compose version` should print v2.x)
- **~1 GB RAM**, **~2 GB disk** (most of which is the Lavalink JVM + caching headroom)
- **A Discord bot token + Gemini API key + ElevenLabs API key + voice ID** — see [discord-bot-setup.md](discord-bot-setup.md)
- **A host directory for persistent state**. The reference compose file uses `/mnt/user/appdata/earworm/` (Unraid convention). Adjust to taste.

## Layout

The reference compose stack:

```
                       ┌────────────────────────────┐
                       │       earworm-net          │
                       │     (docker bridge)        │
                       │                            │
   discord.com ──TLS── │ ┌──────────┐  ┌─────────┐  │ ── 127.0.0.1:8080
                       │ │ earworm  │──│lavalink │  │      /health
                       │ │  (.NET)  │  │ (Java)  │  │
                       │ └──────────┘  └─────────┘  │
                       │     │              │       │
                       └─────┼──────────────┼───────┘
                             │              │
                  /mnt/.../data          (no host volume —
                  /mnt/.../conf          plugins persist in
                                         a docker volume)
```

The bot talks to Lavalink as `http://lavalink:2333` (service name on the bridge). Lavalink fetches TTS audio from the bot as `http://earworm:8080/tts/<id>.mp3`. Neither port is published to the host LAN; only the bot's health port is exposed, and only on `127.0.0.1`.

## 1. Host setup

```bash
# Create state directories. Adjust paths to your host.
sudo mkdir -p /mnt/user/appdata/earworm/{data,conf}
sudo chown -R $USER:$USER /mnt/user/appdata/earworm

# Clone the repo somewhere convenient (not necessarily the mount path).
git clone https://github.com/TheSameCat2/earworm.git
cd earworm
```

## 2. Configure

Copy and edit secrets:

```bash
cp .env.example .env
$EDITOR .env
```

Fill in:

- `EARWORM_DISCORD_BOT_TOKEN`
- `EARWORM_GEMINI_API_KEY`
- `EARWORM_ELEVENLABS_API_KEY`
- `LAVALINK_PASSWORD` — **change this from the default for production.** A long random string is fine; both the Lavalink container and the bot need the same value (compose wires them up from the single env var).

Copy and edit runtime config:

```bash
cp conf/earworm.yaml /mnt/user/appdata/earworm/conf/earworm.yaml
$EDITOR /mnt/user/appdata/earworm/conf/earworm.yaml
```

Fill in `Discord.GuildId` and `Dj.Tts.VoiceId`. See [configuration.md](configuration.md) for the rest of the keys.

The Lavalink plugin config (`conf/lavalink/application.yml`) is **mounted directly from the repo**, not the appdata dir. If you want to customize it (e.g. bump the YouTube plugin version), edit it in the repo. The compose file picks it up automatically.

## 3. Build (or pull) and launch

If you're using the **pull-from-GHCR** path with the upstream image:

```bash
docker compose pull            # pulls the latest published image
docker compose up -d
```

If you forked and want to host your own build, update the `image:` line in your compose file to point at your own GHCR namespace before running `docker compose pull`.

If you're **building from source**, use the root compose file:

```bash
docker compose build
docker compose up -d
```

First launch is slow (~1 minute) — Lavalink downloads its plugin jar. Watch progress:

```bash
docker compose logs -f
```

You're done when you see:

```
earworm-lavalink  | Started Launcher in ...s
earworm           | earworm services started. Press Ctrl+C to shut down.
```

## 4. Verify

```bash
# Bot health check responds 200
curl -s http://127.0.0.1:8080/health
# → {"status":"ok"}

# Lavalink can fetch from the bot (sanity check for the network)
docker exec earworm-lavalink wget -qO- --timeout=5 http://earworm:8080/health
# → {"status":"ok"}
```

Then in Discord: `/start-worm`, `/play <url>`, you know the drill.

## 5. One-time in-server config

The DJ role and logging channel live in the bot's database (not in YAML) so they can be changed without redeploying:

```
/config dj-role @DJs                 # requires Manage Roles or Administrator
/config logging-channel #bot-ops
/config show                         # verify
```

## Day-2 operations

### Upgrading the bot

**Pull-from-GHCR path** (most users):

```bash
cd /path/to/your/compose/dir
docker compose pull earworm
docker compose up -d earworm
```

**Build-from-source path** (contributors):

```bash
cd /path/to/earworm
git pull
docker compose build earworm
docker compose up -d earworm
```

The Lavalink container is unaffected unless you also changed `conf/lavalink/application.yml`. Bot restart preserves the queue (it lives in SQLite at `data/earworm.db`).

### Upgrading the YouTube plugin

When YouTube ships a new player JS, the youtube-source plugin needs an update. Symptom: tracks fail to load with a "sig function not found" error in Lavalink's logs.

Edit `conf/lavalink/application.yml`:

```yaml
lavalink:
  plugins:
    - dependency: "dev.lavalink.youtube:youtube-plugin:1.18.1"  # bump version
```

Then:

```bash
docker compose restart lavalink
```

Lavalink re-downloads the new jar on startup.

### Rotating the Lavalink password

```bash
$EDITOR .env                          # change LAVALINK_PASSWORD
docker compose up -d                  # restarts both containers with the new value
```

### Backups

The bot's state lives in `/mnt/user/appdata/earworm/data/earworm.db`. SQLite — back it up however you back up files. A nightly `cp` to a backup directory plus log rotation is fine; the bot already keeps schema migrations idempotent so restoring from a snapshot is "stop bot → replace .db → start bot."

The Lavalink plugin volume (`earworm-lavalink-plugins`) is regenerable — Lavalink redownloads jars if it's empty.

### Resource tuning

The compose file caps the bot's memory at 1 GB. Lavalink defaults to `-Xmx512M`. Both are conservative; bump if you see OOMs in logs.

Reduce Lavalink RAM further by editing the `_JAVA_OPTIONS` in the compose file. Don't go below 256 MB — the JVM allocates more than you'd expect, and the youtube-source plugin holds open HTTP connections.

## Unraid notes

Use **[`docs/examples/docker-compose.unraid.yml`](examples/docker-compose.unraid.yml)** as your starting point — it's pre-wired with `/mnt/user/appdata/earworm/...` paths and Unraid-friendly conventions. The file works as-is in Unraid's Compose Manager plugin, but a few specifics:

- **Where to put `docker-compose.yml`**: stash the repo somewhere on the array (e.g. `/mnt/user/appdata/_compose/earworm/`) and add it as a stack in Compose Manager.
- **Logging chattiness**: the json-file driver with `max-size: 10m, max-file: 3` (already in the compose) caps total log usage at ~60 MB across both containers. Adequate for most users.
- **Health monitoring**: the bot's `/health` endpoint is wired to the Dockerfile HEALTHCHECK and reflected in `docker ps`. Unraid's container UI shows healthy/unhealthy based on that.
- **Restart behavior**: `restart: unless-stopped` survives reboots. Don't use `restart: always` — it'll endlessly restart a container that crashes on config errors, masking the real problem.

## Non-Compose deployments

For Kubernetes, Nomad, or systemd you'll need to recreate three pieces:

1. **Two containers** with shared networking — bot needs to reach `lavalink:2333`, Lavalink needs to reach the bot's HTTP host (port 8080 by default).
2. **Environment variables** as listed in `docker-compose.yml`. The bot honors `EARWORM_Section__Key` for nested config (e.g. `EARWORM_Dj__TtsServeBaseUrl`).
3. **Persistent volumes** for `/data` (bot's SQLite) and `/opt/Lavalink/plugins` (Lavalink's downloaded jars).

The bot is a regular .NET 10 self-contained binary inside the Docker image — you can also publish it as native binaries (`dotnet publish -c Release -r linux-x64 --self-contained`) if you want to run it outside containers.
