# Discord bot setup

One-time walkthrough for getting earworm registered with Discord and invited
to your server. Once this is done, you only need to come back here if you
rotate the bot token or change permissions.

Companion docs: [local-dev.md](local-dev.md), [deployment.md](deployment.md),
[configuration.md](configuration.md), [troubleshooting.md](troubleshooting.md).

---

## TL;DR

- **Application type**: standard Discord application with a Bot user.
- **Privileged intent to enable**: **Message Content Intent** (and only that one).
- **OAuth2 scopes**: `bot`, `applications.commands`.
- **Permission integer**: `2150714432`.
- **Invite URL**:
  ```
  https://discord.com/oauth2/authorize?client_id=YOUR_APPLICATION_ID&permissions=2150714432&scope=bot+applications.commands
  ```
- **Env var the running bot expects**: `EARWORM_DISCORD_BOT_TOKEN`.

---

## 1. Create the application

1. Open <https://discord.com/developers/applications>.
2. Click **New Application**. Name it `earworm` (or whatever you like — this is
   only visible to you and shows up as the bot's username by default).
3. Accept the developer ToS.

You'll land on the **General Information** page. Copy the **Application ID**
— you'll need it for the invite URL in step 4.

## 2. Configure the bot user

Modern Discord applications come with a bot user already attached. You don't
need to "add" one anymore.

In the left sidebar, open **Bot**. On this page:

1. Optional: set a username and avatar.
2. **Privileged Gateway Intents** — scroll down and enable:
   - ✅ **Message Content Intent**
   - ❌ Server Members Intent (leave off)
   - ❌ Presence Intent (leave off)
3. Hit **Save Changes**.

> **Why Message Content is required**: the `@earworm <youtube-url>` flow reads
> the message text to extract URLs and attachments. Without this intent,
> `MessageCreateEventArgs.Message.Content` arrives as an empty string for any
> message that doesn't @mention the bot directly *and* that the bot didn't
> author — Discord's privacy rules. The slash-command path (`/play`) works
> without it, but you'd lose the headline UX of "just paste a link and ping
> the bot".

> **Why Server Members is not needed**: slash command interactions pre-populate
> the invoking user's role list, and our @mention handler fetches the member
> on demand via REST (`Guild.GetMemberAsync`). We never need a pre-cached
> member list.

## 3. Grab the bot token

Still on the **Bot** page, click **Reset Token** (or **Copy** if you've got
one already). Treat this like a password — anyone who has it can act as the
bot.

Set it as an environment variable for the running container/process:

```bash
export EARWORM_DISCORD_BOT_TOKEN="MTI0...your token..."
```

In production this lives in `docker-compose.yml`'s `environment:` block (read
from `.env` in the same directory). See the top-level `docker-compose.yml`
for the full set of required env vars.

## 4. Generate the invite URL

You can use Discord's URL generator under **OAuth2 → URL Generator** in the
sidebar — tick the scopes and permissions below — or just paste the URL
directly.

### Scopes
- ✅ `bot`
- ✅ `applications.commands`

### Bot permissions

| Permission | Bit pos | Decimal | Why earworm needs it |
|---|---|---|---|
| View Channel | 10 | 1024 | See the voice channel listeners are in, and the text channels where commands are typed. |
| Send Messages | 11 | 2048 | Post slash-command responses, the now-playing embed, and failure notices. |
| Embed Links | 14 | 16384 | `/queue`, `/history`, `/stats`, and now-playing all use Discord embeds. Without this, the embeds silently render as plain text. |
| Read Message History | 16 | 65536 | Required for the @mention reaction flow (`:hourglass:` → `:white_check_mark:` / `:x:`). Discord ties reaction perms to history. |
| Add Reactions | 6 | 64 | Same flow — reacting to the user's message as queueing progress feedback. |
| Connect | 20 | 1048576 | Join voice channels. |
| Speak | 21 | 2097152 | Transmit audio in voice. |
| Use Application Commands | 31 | 2147483648 | Slash commands. |

**Sum: `2150714432`.**

> **Verify the number yourself before authorizing** — if I (or a future
> editor) ever change this table without re-summing carefully, the URL can
> silently grant Administrator or other dangerous bits. Run:
>
> ```bash
> python3 -c 'print(sum(1 << b for b in [6, 10, 11, 14, 16, 20, 21, 31]))'
> # → 2150714432
> ```
>
> And the Discord consent screen should show **exactly 8 permissions**:
> Add Reactions, View Channels, Send Messages, Embed Links, Read Message
> History, Connect, Speak, Use Application Commands. If you see
> **Administrator** or any **Manage X**, stop — the URL is wrong.

Things earworm deliberately does **not** ask for:

- ❌ **Administrator** — never request this for a music bot. The PRD §4 role
  model assumes the bot has only the minimum it needs.
- ❌ **Manage Roles** / **Manage Channels** / **Manage Messages** / **Manage
  Server** — the bot never edits roles, channels, or other people's messages.
  `/config dj-role` is gated on the *caller's* permissions (PRD §7), not
  the bot's.
- ❌ **Mention Everyone** — the now-playing embed explicitly disables pings
  via `AllowedMentions.None` (PRD §8).
- ❌ **Priority Speaker**, **Stream**, **Use Voice Activity Detection** —
  none of these apply to a Opus-frame-emitting music bot.
- ❌ **Move Members**, **Mute Members**, **Deafen Members** — we react to
  server-mute, we don't impose it.

### The URL

Replace `YOUR_APPLICATION_ID` with the value from step 1:

```
https://discord.com/oauth2/authorize?client_id=YOUR_APPLICATION_ID&permissions=2150714432&scope=bot+applications.commands
```

## 5. Invite the bot to your server

Paste the URL into a browser logged into the Discord account that has the
**Manage Server** permission on your target server. Discord will show a
consent screen listing the permissions; tick the server, click **Authorize**,
and complete the captcha.

The bot will appear in your member list, offline (it has no token yet to
log in).

## 6. Start the bot

Either run locally (development):

```bash
EARWORM_DISCORD_BOT_TOKEN="..." \
EARWORM_GEMINI_API_KEY="..." \
EARWORM_ELEVENLABS_API_KEY="..." \
dotnet run --project src/Earworm/Earworm.csproj
```

…or via the container (production):

```bash
docker compose up -d
```

You should see in stdout (or `docker compose logs`):

```
Initializing earworm Discord Music Bot...
Configuration loaded and verified successfully.
Successfully connected to Discord Gateway! Bot is ready.
Completed downloading guild data. Bot has synchronized with 1 guilds.
```

The bot's status in the member list will flip to online.

## 7. First-time in-server configuration

Some settings live in Discord rather than in `conf/earworm.yaml` (so they can
be changed without redeploying). Run these once after the bot joins:

```
/config dj-role @YourDjRole       # who can run destructive commands
/config logging-channel #ops      # where track-failure notices land
/config show                      # confirm both are set
```

The first command requires the **invoker** to hold `MANAGE_ROLES` or
`ADMINISTRATOR` on the server (PRD §7 chicken-and-egg rule — a DJ can't
reassign the DJ role to themselves once the role exists).

## 8. Smoke test

```
/clear-worm                       # flush anything carried over from prior runs
/start-worm                       # bot joins your voice channel
/play https://www.youtube.com/watch?v=dQw4w9WgXcQ
                                  # or just @earworm with the URL
/queue                            # confirm the track is queued
/skip                             # confirm DJ-only commands work
/stop-worm                        # bot leaves voice
```

If `/play` resolves but no audio plays, the most likely cause is that the
sibling **Lavalink** container isn't running or can't load the URL — see
[troubleshooting.md](troubleshooting.md#music-doesnt-play). earworm doesn't
ship yt-dlp/ffmpeg itself; voice transmission is delegated to Lavalink.

---

## Troubleshooting

### "Unknown interaction" or commands don't appear
Slash commands register on the **Ready** event, scoped to the guild ID in
`Discord.GuildId`. They appear instantly for that guild. Globally-registered
commands take up to an hour to propagate, but earworm doesn't register
globally — if your guild ID is wrong, commands won't appear at all.

### Mentions queue nothing, no errors in logs
You forgot to enable **Message Content Intent** in the developer portal
(step 2). The bot receives the `message_create` event but `.Content` is
empty so the URL regex finds nothing.

### Docker container restart loop
The container HEALTHCHECK probes `http://localhost:8080/health`. If you've
changed `ops.http_port` in `conf/earworm.yaml`, update the `HEALTHCHECK`
line in `Dockerfile` to match, or remove the port mapping line in
`docker-compose.yml` and let Docker resolve via localhost inside the
container.

### `Authentication failed. Check your token and try again.` at startup
The `EARWORM_DISCORD_BOT_TOKEN` env var is empty, malformed, or for a
different application than the one you invited. Reset and re-copy from the
**Bot** page in the developer portal.

### `/start-worm` fails with "Bot can't connect to this channel"
The bot has VIEW_CHANNEL on the guild but the voice channel has a channel-
level permission override denying it. Edit the voice channel's permissions
and explicitly grant the bot's role View Channel + Connect + Speak.

---

## Rotating the bot token

If the token is exposed (committed to git, posted in a screenshot, etc.):

1. **Developer Portal → Bot → Reset Token**. The old token is invalidated
   immediately.
2. Update `EARWORM_DISCORD_BOT_TOKEN` in your `.env` / secrets store.
3. Restart the container.

No re-invite needed — the application identity is unchanged, only the
authentication secret rotated.
