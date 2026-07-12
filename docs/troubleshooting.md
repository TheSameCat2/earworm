# Troubleshooting

The bot's most common failure modes and their fixes, organized roughly by where they fail in the stack.

If you're not sure where the problem is: **read the bot's logs first**. Most failures print enough context to identify the issue. The Lavalink container's logs (`docker logs earworm-lavalink`) are also worth checking for any audio-related issue.

---

## Startup

### `Configuration error: discord.guild_id is required in conf/earworm.yaml`

`conf/earworm.yaml` is either missing or has `Discord.GuildId` blank. Fill it in with the Discord server ID (Developer Mode → right-click server → Copy Server ID).

If your YAML has it set but the bot still complains: check the **key style**. Keys are PascalCase. `guild_id: "..."` (snake_case) gets silently ignored and falls back to the empty default. Use `GuildId: "..."`.

### `EARWORM_DISCORD_BOT_TOKEN environment variable is missing`

Either:
- You didn't export the variable (locally), or
- Your `.env` file is missing or doesn't contain it, or
- You started Docker Compose without `.env` in the same directory as `docker-compose.yml`

Verify with `echo $EARWORM_DISCORD_BOT_TOKEN | wc -c`. A real Discord token is ~70 characters.

### `Authentication failed. Check your token and try again.`

The token is set but Discord rejected it. Causes in order of likelihood:

1. **Wrong token** — copy-paste error, or you copied the application ID instead.
2. **Token rotated** — you (or someone) hit "Reset Token" in the developer portal. Old tokens stop working immediately.
3. **Wrong application** — the token is valid but for a different bot than the one you invited.

Fix: Developer Portal → Bot → Reset Token → copy → update `.env`/secrets → restart.

### `Failed to start Lavalink audio service. Is the Lavalink server running and reachable?`

The bot reached the configured `Lavalink.Host:Lavalink.Port` but couldn't complete the handshake. Causes:

1. **Lavalink container not running** — `docker ps` should show `earworm-lavalink`.
2. **Wrong host/port** — for Docker Compose the host should be `lavalink` (service name); locally it's `localhost`. Compose overrides this via `EARWORM_Lavalink__Host`.
3. **Password mismatch** — bot's `Lavalink.Password` (or `EARWORM_Lavalink__Password`) must match the Lavalink container's `LAVALINK_SERVER_PASSWORD` env var.
4. **Lavalink still booting** — first boot downloads plugin jars; can take 30+ seconds. `docker logs earworm-lavalink` should show `Started Launcher in N.NNs` when ready.

---

## Discord

### Slash commands don't appear

Slash commands register on the bot's `Ready` event, **scoped to the guild ID in config**. They appear instantly for that guild — no propagation delay.

If they're not showing up:

1. Confirm `Discord.GuildId` matches the server you're testing in.
2. Confirm the bot is online (the green dot in the member list).
3. Confirm the bot was invited with the `applications.commands` scope. If not, re-run the OAuth flow with the invite URL from [discord-bot-setup.md](discord-bot-setup.md).
4. Try kicking and re-inviting the bot. Sometimes Discord caches stale scope data.

### `@earworm <something>` does nothing

You forgot to enable **Message Content Intent** in the Discord developer portal. Without it, `e.Message.Content` arrives as an empty string and the URL regex matches nothing — silently.

Fix: Developer Portal → Bot → scroll to Privileged Gateway Intents → enable Message Content Intent → save → restart the bot.

### `Bot can't connect to this channel`

The bot has guild-level VIEW_CHANNEL but the specific voice channel has a permission override denying it. Edit the voice channel's permissions: explicitly grant the bot's role View Channel + Connect + Speak.

### The bot reacts with ❌ on @mention

Look at the reply message — it'll tell you why. Common ones:

- "You must be in a voice channel to queue music." → join voice first.
- "Playlist queuing is restricted to DJs" → use a single-track URL or get DJ.
- "Private or local track URLs are not allowed" → Earworm intentionally blocks direct internal-network audio URLs. Use a publicly reachable source; the bot-generated DJ TTS route is handled separately and is unaffected.
- "Couldn't queue that: ..." → Lavalink couldn't resolve the URL. See "Music doesn't play" below.

---

## Music playback

### `Lavalink returned no result for query: <url>`

Lavalink couldn't load the URL. **Check Lavalink's logs first** — the bot only sees "no result," Lavalink has the actual reason.

```fish
docker logs earworm-lavalink --tail 100 | grep -i error
```

Common causes:

#### `Must find sig function from script: /s/player/<hash>/player_embed...`

YouTube updated their player JavaScript and the youtube-source plugin doesn't know how to decipher the new signature. Bump the plugin version in `conf/lavalink/application.yml`:

```yaml
lavalink:
  plugins:
    - dependency: "dev.lavalink.youtube:youtube-plugin:1.18.1"  # ← bump this
```

Latest version: https://github.com/lavalink-devs/youtube-source/releases

Then `docker restart earworm-lavalink` (or `docker compose restart lavalink`).

If the very latest plugin still has the same error, YouTube cut their update inside the plugin authors' response window. Wait for the next plugin release — usually within hours.

#### `Video unavailable` / `This video is not available`

The video is region-locked, age-restricted, private, or removed. Try the URL in a browser — if you can't watch it, neither can Lavalink. Age-restricted videos may work via OAuth (advanced plugin config; not covered here).

#### `Connection timed out` (for non-YouTube URLs)

Lavalink couldn't reach the upstream (SoundCloud, the file URL, etc.). Check the Lavalink container's outbound network — most often a corporate VPN or DNS issue.

### Music plays but DJ commentary doesn't

Multi-step pipeline; check each step:

1. **Is DJ enabled in settings?** Run `/config show` in Discord. If "AI DJ Commentary" shows Disabled, run `/djon` and try again.
2. **Did the cadence roll?** Look for `DJ cadence reached (X/Y)` in bot logs. If `X < Y`, the random cadence didn't hit this cycle — queue another track and try again, or set `Dj.MaxGapTracks: 1` temporarily to force every track to trigger commentary.
3. **Did Gemini fail?** Look for `Gemini API error` in bot logs. See "Gemini 404" below.
4. **Did ElevenLabs fail?** Look for `ElevenLabs API error`. Usually a wrong voice ID or out-of-quota.
5. **Was the file staged?** Look for `Staged DJ commentary at <url>`. If yes, the file was rendered and saved.
6. **Did Lavalink fail to fetch it?** Look for `Lavalink could not load TTS URL`. This is the most common failure mode — see "TTS URL unreachable" below.

### TTS URL unreachable (`Lavalink could not load TTS URL`)

Lavalink can't fetch the staged TTS file from the bot's HTTP endpoint. Diagnose from inside the Lavalink container:

```fish
# 1. Does the hostname resolve?
docker exec earworm-lavalink getent hosts host.docker.internal
# Expect: an IP, e.g. 172.17.0.1 host.docker.internal

# 2. Can Lavalink reach the bot?
docker exec earworm-lavalink wget -qO- --timeout=5 \
  "http://host.docker.internal:8080/health"
# Expect: {"status":"ok"}
```

| Step 1 result | Step 2 result | Diagnosis + Fix |
|---|---|---|
| (no output) | (anything) | The container was started without `--add-host host.docker.internal:host-gateway`. Stop the container and re-run with that flag (see [local-dev.md](local-dev.md)). |
| IP shown | `{"status":"ok"}` | Network is fine. Different issue — recheck the bot's `Dj.TtsServeBaseUrl` config (should match the hostname Lavalink uses). |
| IP shown | (hangs forever) | Host firewall silently dropping. On Linux with ufw: `sudo ufw allow in on docker0 to any port 8080 proto tcp && sudo ufw reload`. |
| IP shown | "connection refused" | Bot isn't running or not listening on 0.0.0.0. Confirm with `ss -tlnp \| grep 8080` on the host. |
| IP shown | "404" | The bot's `/tts/<file>` route is rejecting the filename. Either the staged file was deleted before fetch, or the filename doesn't match the regex (shouldn't happen — it's bot-generated). Check bot logs for the staged URL. |

In **Docker Compose**, Lavalink reaches the bot at `http://earworm:8080` (service name on the shared network) — `host.docker.internal` isn't used. The compose file sets `EARWORM_Dj__TtsServeBaseUrl=http://earworm:8080` automatically.

### Gemini 404: "models not found"

The `Dj.GeminiModel` value in your config doesn't correspond to a model your API key has access to. The PRD's original placeholder `gemini-3.1-flash` doesn't exist.

Check what your key supports:

```fish
curl -s "https://generativelanguage.googleapis.com/v1beta/models?key=$EARWORM_GEMINI_API_KEY" \
  | python3 -m json.tool | grep -E '"name"|"supportedGenerationMethods"' | head -40
```

Look for a model whose `supportedGenerationMethods` includes `generateContent`. Good defaults:

- `gemini-2.5-flash` — fast, cheap, free-tier accessible (recommended)
- `gemini-2.5-pro` — slower but higher quality
- `gemini-2.0-flash` — older but reliable

Update `Dj.GeminiModel` in `conf/earworm.yaml` and restart the bot.

### ElevenLabs 401 / 403

API key invalid or revoked. Generate a new one at https://elevenlabs.io/app/settings/api-keys and update `EARWORM_ELEVENLABS_API_KEY`.

### ElevenLabs 422 "Unprocessable Entity"

Usually a bad voice ID or model ID. Get a real voice ID from your ElevenLabs voice library (the URL contains the ID).

---

## Voice channel quirks

### Bot joins and leaves immediately

Auto-disconnect timers fire if the channel is empty (no humans) or the queue is empty. Check `AutoBehavior.EmptyChannelGraceSeconds` and `AutoBehavior.IdleDisconnectSeconds` — defaults are 120s each. Set to a large number to effectively disable.

### Bot is in voice but no audio

If the bot member is visible in your voice channel but you hear nothing:

1. Check `/queue` — is anything actually queued? An empty queue produces no audio.
2. Check Lavalink logs for the active track — `docker logs earworm-lavalink | grep -i "started"`. If Lavalink isn't logging track starts, the bot isn't sending it work.
3. Check Discord client volume — accidentally muting the bot user in client-side voice settings is a thing.
4. Server-mute the bot, unmute. Sometimes shakes loose a stuck UDP stream.

### Voice gateway "VOICE_CHANNEL_START_TIME_UPDATE" unknown event warning

Cosmetic. DSharpPlus 4.5.1 doesn't know about a newer Discord event that doesn't affect functionality. Ignore.

---

## Container / Compose

### `docker compose up` fails with "required variable LAVALINK_PASSWORD not set"

Your `.env` is either missing or doesn't define `LAVALINK_PASSWORD`. Copy from `.env.example`:

```fish
cp .env.example .env
$EDITOR .env  # fill in all 4 required values
```

### Container exits immediately

```fish
docker compose logs earworm | tail -30
```

…tells you why. The most common ones:

- `Authentication failed` → bad bot token
- `Configuration error: discord.guild_id is required` → `/mnt/.../conf/earworm.yaml` wasn't mounted or wasn't filled in
- `Failed to start Lavalink audio service` → Lavalink container hasn't finished booting yet. Try `docker compose restart earworm` 30 seconds after the initial `up`.

### "Did Lavalink finish booting?"

Look for this exact line in `docker compose logs lavalink`:

```
Started Launcher in 12.345 seconds
```

…then the YouTube plugin loads:

```
Loaded plugin youtube-plugin
```

The bot will fail to start until both are visible.

---

## State

### Stale tracks in queue after restart

The queue is persisted to SQLite. When the bot restarts, any tracks that were pending get hydrated back into the queue. To flush them:

```
/clear-worm
```

Or wipe `data/earworm.db` to reset everything (queue, history, snapshots, settings, metrics). The schema gets recreated on next startup.

### History / stats are wrong

Per-user metrics depend on display names being stable. If a user changes their nickname mid-session, you may see two entries for them in `/stats`. This is by design — the bot records the display name at the time of the action.

---

## When all else fails

1. **Save the logs**. `docker compose logs > /tmp/earworm-logs.txt`, or pipe bot stdout to a file.
2. **Check the issue tracker** for whatever's bothering you.
3. **Open a new issue** with: the failing command, the relevant log excerpt, your `conf/earworm.yaml` (with secrets redacted), the bot version (commit hash), the Lavalink plugin version, and your deployment mode (local-dev vs compose vs other).
