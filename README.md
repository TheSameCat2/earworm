# 🎧 earworm

A Discord music bot with an AI DJ that actually talks. Drop YouTube links, mention the bot, queue a thousand tracks if you want — and every few songs a generated radio voice ducks in to introduce the next one. Built on Lavalink so the audio path is rock-solid even when Discord shuffles their voice gateway.

```
  ___  __ _ _ ____      _____  _ __ _ __ ___
 / _ \/ _` | '__\ \ /\ / / _ \| '__| '_ ` _ \
|  __/ (_| | |   \ V  V / (_) | |  | | | | | |
 \___|\__,_|_|    \_/\_/ \___/|_|  |_| |_| |_|
```

---

## What makes it different

Most Discord music bots stopped at "queue a track and play it." earworm does that plus:

- **🎙 AI DJ** — A Gemini-written, ElevenLabs-voiced radio DJ that introduces tracks at random intervals. Configurable persona, voice, cadence. Toggle with `/djon` / `/djoff`.
- **💾 Queue snapshots** — `/save` captures the current queue + playing track; `/restore` brings it all back, even across bot restarts. Great for "movie night" or "Friday raid playlist."
- **📊 Real stats** — `/stats` shows top listeners by time, top queuers, source-type breakdown. Persisted in SQLite.
- **🤖 Mention to queue** — `@earworm <youtube-url>` or `@earworm The Veldt by Deadmau5` skips the slash-command dance. Drop an MP3 attachment and it'll queue that too.
- **🛡 DJ-role gated destructive commands** — `/skip`, `/clear-worm`, `/move`, etc. require a configurable role. Set it once with `/config dj-role @RoleName`.
- **🏠 Auto-disconnect** — leaves the channel when nobody's listening (configurable grace period).
- **♻️ Rebuilt on Lavalink** — voice transmission delegated to a battle-tested Java service. When YouTube breaks player JS, you bump one plugin version and you're back. No more "did Discord change voice gateway again?"

---

## Quick start

You don't need to clone the repo. There's a pre-built image on GitHub Container Registry — just grab one of the example compose files and go.

```fish
# 1. Pick the example that fits your setup
#    Generic (any Linux host):
curl -O https://raw.githubusercontent.com/TheSameCat2/earworm/main/docs/examples/docker-compose.example.yml -o docker-compose.yml
#    Unraid:
# curl -O https://raw.githubusercontent.com/TheSameCat2/earworm/main/docs/examples/docker-compose.unraid.yml -o docker-compose.yml

# 2. Set up secrets
curl -O https://raw.githubusercontent.com/TheSameCat2/earworm/main/.env.example -o .env
$EDITOR .env

# 3. Grab the two config files the bot + Lavalink need
mkdir -p conf/lavalink
curl -o conf/earworm.yaml \
  https://raw.githubusercontent.com/TheSameCat2/earworm/main/conf/earworm.example.yaml
curl -o conf/lavalink/application.yml \
  https://raw.githubusercontent.com/TheSameCat2/earworm/main/conf/lavalink/application.yml
$EDITOR conf/earworm.yaml   # set Discord.GuildId + Dj.Tts.VoiceId

# 4. Launch
docker compose up -d
docker compose logs -f earworm
```

You'll need a Discord bot token before any of this works. Five-minute walkthrough: **[docs/discord-bot-setup.md](docs/discord-bot-setup.md)**.

Want to build from source instead? Clone the repo and use the root `docker-compose.yml` (which builds the image locally). Or run without Docker entirely: **[docs/local-dev.md](docs/local-dev.md)**.

---

## Commands

The headline ones — full reference at **[docs/commands.md](docs/commands.md)**.

| Command | What it does | Who can run it |
|---|---|---|
| `/play <query>` | Queue a YouTube/SoundCloud URL, search term, or attachment | Anyone in voice |
| `@earworm <query>` | Same as `/play` but as a chat mention | Anyone in voice |
| `/queue` | View what's playing and what's up next | Anyone |
| `/skip` | Skip the current track | DJ |
| `/pause` / `/resume` | What it says on the tin | DJ |
| `/seek 1:23` | Jump to a position (mm:ss or seconds) | DJ |
| `/start-worm` / `/stop-worm` | Connect / disconnect from voice | Anyone / DJ |
| `/save` / `/restore` | Snapshot the queue, load it later | DJ |
| `/djon` / `/djoff` | Toggle the AI DJ | DJ |
| `/history [N]` | Last N tracks played (defaults to 20) | Anyone |
| `/stats` | Top listeners + top queuers leaderboards | Anyone |

> "DJ" = a Discord role you assign with `/config dj-role @Role`. Server Administrators always bypass.

---

## How the AI DJ actually works

Every track ending, earworm rolls a die between 1 and `Dj.MaxGapTracks` (default 4). When the counter hits that number:

1. **Gemini 2.5 Flash** generates a ~30-word intro for the upcoming track based on your configured persona prompt
2. **ElevenLabs** renders it to MP3 with your chosen voice (default config is a friendly west-coast radio DJ)
3. The MP3 gets staged on disk and Lavalink fetches it via an HTTP endpoint
4. Lavalink plays the intro, then immediately plays the music track

You can tune the cadence, the persona prompt, the TTS voice and model, all in `conf/earworm.yaml` under `Dj:`. See **[docs/configuration.md#dj](docs/configuration.md#dj-config)**.

---

## Architecture, in one paragraph

The bot is a .NET 10 program that handles Discord interactions (slash commands, messages, voice-state events) via **DSharpPlus 4.5.1**. The voice/audio half is delegated to a **Lavalink** Java service running in a sibling container — the bot tells Lavalink "play this URL," Lavalink streams Opus to Discord. SQLite holds the queue, history, snapshots, settings, and metrics. ASP.NET Core minimal API serves `/health`, `/metrics`, and `/tts/{id}.mp3` (for the DJ TTS files Lavalink fetches). Gemini and ElevenLabs are reached over HTTPS from inside the bot process.

Full architecture write-up with a diagram: **[docs/architecture.md](docs/architecture.md)**.

---

## Documentation

| Doc | What's in it |
|---|---|
| [docs/discord-bot-setup.md](docs/discord-bot-setup.md) | Creating the Discord application, OAuth permissions, inviting the bot |
| [docs/local-dev.md](docs/local-dev.md) | Running the bot from source on your dev machine (with Lavalink in Docker) |
| [docs/deployment.md](docs/deployment.md) | Production deployment via Docker Compose, including Unraid notes |
| [docs/configuration.md](docs/configuration.md) | Every key in `conf/earworm.yaml` explained |
| [docs/commands.md](docs/commands.md) | Every slash command, every `@mention` behavior |
| [docs/architecture.md](docs/architecture.md) | How the pieces fit together, why it's built this way |
| [docs/troubleshooting.md](docs/troubleshooting.md) | Common failure modes and fixes — read this when you get stuck |
| [docs/examples/](docs/examples/) | Drop-in Compose files (`docker-compose.example.yml`, `docker-compose.unraid.yml`) |

---

## Requirements

- **Discord**: a server you can manage, plus a bot application (see setup doc)
- **API keys**:
  - Discord bot token
  - [Google AI Studio](https://aistudio.google.com/apikey) Gemini API key
  - [ElevenLabs](https://elevenlabs.io/app/settings/api-keys) API key + a voice ID
- **For Docker deployment**: Docker + Docker Compose, ~1 GB RAM, ~2 GB disk
- **For local dev**: .NET 10 SDK, Docker (just to run Lavalink), some `ufw`/firewall savvy on Linux

---

## Why "earworm"?

Because that's what a good DJ leaves you with.

---

## Contributing

Issues and PRs welcome. The codebase is small (~5,000 lines) and follows fairly conventional .NET patterns:

- `Discord/` — slash commands, attribute permission checks, the gateway wrapper
- `Domain/Player/` — the Lavalink-backed PlayerEngine
- `Domain/Queue/` — queue state + persistence
- `Domain/DJ/` — Gemini + ElevenLabs + cadence logic
- `Persistence/` — SQLite schema and repositories
- `Health/` — the ASP.NET endpoint hosting `/health`, `/tts/{id}`, `/metrics`

Run `dotnet test` before opening a PR. The CompositionRootTests catch DI wiring bugs that would otherwise blow up at startup.

---

## License

MIT — see [LICENSE.md](LICENSE.md). Contributions are accepted under the same terms; see [CONTRIBUTING.md](CONTRIBUTING.md).
