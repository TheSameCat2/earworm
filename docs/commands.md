# Commands reference

Every way to talk to earworm: slash commands, the `@mention` shortcut, and the implicit reactions.

## Permission tiers

- **🟢 Anyone** — any guild member in a voice channel can run this.
- **🟡 In-voice** — requires the caller to be in the same voice channel as the bot (or any voice channel if the bot isn't connected).
- **🔴 DJ** — requires the configured DJ role, or Administrator. Set the role with `/config dj-role @Role`.
- **🔵 Requester or DJ** — original track requester gets a pass; otherwise DJ.
- **⚙ Manage Roles or Admin** — `/config dj-role` and the channel settings, to prevent self-promotion.
- **👑 Bot owner** — `/admin …`, restricted to the user IDs in `Bot.OwnerUserIds`.

All non-admin commands additionally require the guild to be a whitelisted tenant (see [Admin](#admin-bot-owners)). In a non-whitelisted guild they reply with an ephemeral "not available here."

---

## Playback

### `/play <query>`  🟢

Queue a track by URL or search term.

```
/play https://www.youtube.com/watch?v=dQw4w9WgXcQ
/play https://soundcloud.com/artist/track-name
/play discord music bot is fixed
```

- **URLs**: YouTube, YouTube Music, SoundCloud, Bandcamp, Twitch, Vimeo, and public direct audio file URLs. Loopback, private-network, single-label, `.local`, `.internal`, and credential-bearing URLs are rejected so `/play` cannot be used to make Lavalink probe internal services.

  This application-level check cannot eliminate DNS rebinding because Lavalink
  resolves the hostname in a separate process after validation. Before serving
  mutually untrusted tenants, enforce Lavalink egress policy (or use an
  allowlisting proxy): allow its required public media destinations and the
  Earworm TTS endpoint, while denying other private, link-local, and cloud
  metadata destinations.
- **Search terms**: query goes to YouTube search (`ytsearch:`), first result is queued.
- **Auto-join**: if the bot isn't in voice, it joins your channel before queueing.

Playlist URLs are gated to DJs only — a single paste can flood the queue otherwise. The error message will tell you if this is why a `/play` was rejected.

### `@earworm <query>`  🟢

Same as `/play`, but as a chat mention. Mention behavior:

- Plain text → search query
- URL → resolve and queue
- MP3/M4A/OGG/WAV/FLAC attachment → queue the attached file

Reactions tell you what happened:
- ⏳ (hourglass) — working on it
- ✅ — queued successfully
- ❌ — failed (see the reply message for why)

### `/start-worm [channel]`  🟢

Make the bot join a voice channel and start playing. Without arguments, it joins the channel you're in.

```
/start-worm
/start-worm channel: #music
```

Errors gracefully if the specified channel isn't a voice/stage channel.

### `/stop-worm`  🔴

Stop playback and disconnect from voice. Clears the active player but leaves the queue intact — you can `/start-worm` again to resume.

### `/pause`  🟡 🔴
### `/resume`  🟡 🔴

What it says. The bot will also auto-pause if you server-mute it, and resume on unmute.

### `/skip`  🟡 🔴

Skip the currently playing track. The next queued track starts immediately. If the queue is empty after skip, the bot starts the idle-disconnect timer (default 2 minutes).

### `/previous`  🟡 🔴

Re-queue the last track from history at the front of the queue and skip to it. Useful when someone shouts "wait, play that again."

### `/seek <position>`  🟡 🔴

Jump to a position in the current track.

```
/seek 1:30        # mm:ss
/seek 0:01:30     # hh:mm:ss
/seek 90          # raw seconds
```

---

## Queue management

### `/queue`  🟢

Show the currently playing track and up to the next 10 queued tracks. Embed includes title, artist, duration, requester. If more than 10 tracks are queued, you'll see "...and N more."

### `/remove <position>`  🟡 🔵

Remove the track at the given queue position (1-indexed; 1 is the next track up).

```
/remove 3
```

Non-DJs can only remove tracks they personally queued. DJs and Admins can remove anything.

### `/move <from> <to>`  🔴

Move a track from one queue position to another. Useful for prioritizing.

```
/move 5 1     # move the 5th track to play next
```

### `/clear-worm`  🔴

Nuke the entire queue. Doesn't stop the currently playing track.

---

## Snapshots

### `/save`  🔴

Capture the current queue + currently playing track as a snapshot. There's one snapshot slot — saving overwrites the previous one.

Use cases:
- Pre-emptively saving a curated queue before letting chaos commence
- Bookmarking "the good queue from movie night"

### `/restore`  🔴

Replace the current queue with the saved snapshot. If you're not in voice when you call this, the bot joins your channel automatically.

Snapshots survive bot restarts (stored in SQLite).

---

## Info

### `/history [limit]`  🟢

Show recently played tracks. Default 20, max value configurable via `Persistence.HistoryMaxN` (default 100).

```
/history          # last 20
/history limit: 50
```

Tracks are shown with relative timestamps ("3 minutes ago", "yesterday"), duration, requester, and source. Failed tracks are excluded.

### `/stats`  🟢

Global server statistics + top-listener leaderboards.

- **Global metrics**: total tracks queued, completed, total listening time.
- **Request sources**: count of tracks from YouTube URLs vs SoundCloud vs MP3 uploads vs search queries.
- **Top 5 listeners by total listening time**
- **Top 5 queuers by tracks queued**

---

## DJ commentary

### `/djon`  🔴
### `/djoff`  🔴

Toggle the AI DJ. When on, the bot rolls a random cadence (1..`Dj.MaxGapTracks`) and injects a generated radio intro before that many tracks have elapsed.

Each commentary cycle costs:
- 1 Gemini API call (~500 tokens, fast model)
- 1 ElevenLabs API call (the TTS rendering — bills per character, not per call)
- ~200 KB of temporary disk for the MP3
- 5-10 seconds of voice channel time (the intro itself)

If you want less commentary without disabling entirely, increase `Dj.MaxGapTracks` in `conf/earworm.yaml`.

---

## Configuration

### `/config dj-role <role>`  ⚙

Set the role that grants DJ permissions on destructive commands. Required for `/skip`, `/clear-worm`, `/move`, etc.

```
/config dj-role role: @DJs
```

Requires the caller to have `Manage Roles` or `Administrator`. This is deliberate — without this rule a DJ could remove their own role from the bot's allowlist and lock everyone else out.

### `/config logging-channel <channel>`  🔴

Set a text channel where the bot posts failure notices (e.g., "Couldn't play X — yt-dlp HTTP 410"). Without this set, failures only appear in stdout/Docker logs.

```
/config logging-channel channel: #bot-ops
```

### `/config now-playing-channel <channel>`  ⚙

Set the text channel where "Now Playing" embeds are posted when a track starts. Each guild configures its own. Without this set, no Now Playing embeds are posted.

```
/config now-playing-channel channel: #now-playing
```

### `/config show`  🟢

Display the current settings — DJ enabled state, configured DJ role, configured logging channel, configured now-playing channel.

---

## Admin (bot owners)

These commands manage the multi-tenant whitelist and are restricted to the bot owners listed in `Bot.OwnerUserIds`. earworm only serves guilds that have been admitted as tenants; all the commands above are gated on the calling guild being whitelisted.

`OwnerUserIds` defaults to an empty list. This is fine for an existing
single-tenant deployment, but at least one owner must be configured before a
second server can be admitted or tenant access can be managed.

### `/admin add-server <guild-id>`  👑

Whitelist a Discord guild as a tenant. Slash commands are registered to that guild immediately (instant propagation).

### `/admin list-servers`  👑

List every tenant guild with its plan and status.

### `/admin remove-server <guild-id>`  👑

Suspend a tenant (soft delete — status becomes `suspended`). Commands stop working there; the data is retained. The final active tenant cannot be suspended; admit another server first.

---

## What's missing (vs the PRD)

Some features described in the original spec aren't currently implemented:

- **Volume control** — Lavalink supports per-player volume but earworm doesn't expose a `/volume` command.
- **`/queue` track position** — shows `0:00 / 3:45` always; reading the live position from Lavalink's player needs an async path the current UI doesn't have.
- **Search picker** — text searches always queue the first YouTube result; no "pick from 5 results" UI.

PRs welcome.
