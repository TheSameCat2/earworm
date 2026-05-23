# Contributing to earworm

Thanks for considering a contribution. earworm is a hobby Discord music bot, and contributions of any size are welcome — bug reports, doc improvements, new features, refactors that delete more lines than they add.

## TL;DR

1. Open an issue first for anything bigger than a one-line fix, so we can talk through approach before you write the code.
2. Fork → branch → code → `dotnet build` + `dotnet test` → PR.
3. Match the existing code style. `dotnet format` if you want to be tidy.
4. Be kind. The [Code of Conduct](CODE_OF_CONDUCT.md) applies.

## Reporting bugs

Open an issue with:

- **What you ran** (the exact slash command or @mention, or whatever startup invocation)
- **What you expected** to happen
- **What actually happened**, including the relevant bot logs and Lavalink logs (`docker logs earworm-lavalink --tail 100`)
- **Your environment**: bot version (commit hash or release tag), deployment mode (local-dev / Docker Compose / other), OS
- **Redacted `conf/earworm.yaml`** — keep API keys out, but include your structure

Before opening, search [existing issues](../../issues) — there's a good chance someone else hit the same thing. Check [docs/troubleshooting.md](docs/troubleshooting.md) too; it covers the most common failure modes.

## Suggesting features

Open an issue with the `enhancement` label describing:

- **The user-facing behavior** — what would a Discord user see/do?
- **Why it matters** — what problem does it solve, or what experience does it improve?
- **Scope hints** — would this be a one-command addition, or a bigger architectural change?

The project is intentionally narrow (single-guild, no multi-server orchestration, no web UI) — features that pull it toward "general-purpose music bot platform" are unlikely to land. Features that make the existing PRD experience tighter are very welcome.

## Development setup

See [docs/local-dev.md](docs/local-dev.md) for the full walkthrough — getting Lavalink running locally is the one non-obvious bit.

Quick path once you've cloned:

```fish
cp .env.example .env       # fill in API keys
dotnet build src/Earworm/Earworm.csproj
dotnet test                # all 8 tests should pass
dotnet run --project src/Earworm/Earworm.csproj
```

## Pull request process

1. **Fork** the repo and create a branch from `main`. Branch names like `fix/lavalink-timeout` or `feature/volume-command` are nice but not required.
2. **Code**. Keep the change focused — one logical thing per PR. If you discover unrelated cleanup, open a second PR for it.
3. **Test**:
   - `dotnet build src/Earworm/Earworm.csproj` — must produce 0 errors, 0 warnings
   - `dotnet test` — all tests must pass
   - For UI/voice changes, smoke-test in a real Discord server (the test suite doesn't cover Discord interactions)
4. **Update docs**. If you change a command, a config key, or a deployment behavior, update the relevant file in `docs/`. The [docs/commands.md](docs/commands.md) table is canonical for slash commands.
5. **Open the PR** with:
   - A description of what changed and why
   - Any screenshots or log excerpts that show it working
   - Notes on what you didn't test (so reviewers know what to verify)
6. **Respond to review**. Pushback is normal; the goal is to land good code, not to gatekeep.

PRs that don't compile or break existing tests will be asked to fix before review. PRs that change user-facing behavior without updating docs will be asked to update them.

## AI-assisted contributions

AI-assisted code is welcome — much of this repo was written that way. The single rule is that *you've read, understood, and tested everything you submit*. Treat AI output the way you'd treat a snippet from Stack Overflow: a useful starting point that becomes your responsibility the moment it lands in your PR.

In practice:

- Be able to explain any code in your PR if a reviewer asks. "The AI wrote it" isn't an answer.
- Run `dotnet build` and `dotnet test` against your branch yourself — don't rely on the AI's claim that it passed.
- Strip filler comments the AI tends to generate (`// This method does X`) — they rot quickly and add noise. The bar is the same as for hand-written code: comments earn their place by explaining *why*, not restating *what*.

We won't ask whether a contribution was AI-assisted. The signal that matters is whether the PR is good: well-scoped, tested, documented.

## Code style

The codebase follows fairly conventional modern C# (.NET 10):

- **PascalCase** for types, methods, public members. `_camelCase` for private fields.
- **Sealed classes** by default — only open them up when subclassing is genuinely intended.
- **Records** (`sealed record`) for value-shaped types (`QueueItem`, `PlaybackState`, etc.). Classes for things with behavior and lifecycle.
- **`async`/`await` everywhere**, never `.Result` or `.Wait()`. Async-over-sync deadlocks have bitten this codebase before.
- **`ILogger<T>`** for logging via DI. Never `Console.WriteLine` outside `Program.cs`'s startup banner.
- **Minimal comments**. Don't write `// increments the counter` next to `counter++`. *Do* write `// Why: we observed Discord-side stale sessions hanging ConnectAsync ...` — context that future-you would otherwise have to rediscover.

`dotnet format` will fix most low-level style issues. We don't have a CI lint gate yet, but a tidy diff helps reviewers.

## Testing

```fish
dotnet test
```

The test suite is small but load-bearing — `CompositionRootTests` in particular catches DI wiring bugs that would otherwise crash at startup. Add a test when:

- You add a new service that depends on something not previously in the graph
- You add a new repository or migration
- You fix a bug — the test should fail without your fix, pass with it

Tests that touch Discord, Lavalink, Gemini, or ElevenLabs are out of scope for the unit suite. Those need real Discord/network access; smoke-test manually.

## Commit messages

No strict format. Aim for descriptive subject lines:

- ✅ `Fix VoiceManager NRE on bot's first voice join (e.Before is null)`
- ✅ `Add /volume slash command + Lavalink player volume integration`
- ❌ `fix`
- ❌ `wip`

Conventional Commits (`feat:`, `fix:`, etc.) are fine if you prefer that style but not required.

## What gets contributions accepted faster

- **Tests** for any non-trivial change
- **A linked issue** so reviewers know the context without re-deriving it
- **Small scope** — one thing per PR
- **Docs updates** in the same PR as the code change
- **An explanation of trade-offs** if you considered multiple approaches and picked one

## What gets contributions rejected

- Adding telemetry/analytics that calls home anywhere
- Bundling cryptocurrency, ads, or monetization
- "Drive-by" rewrites of subsystems that work fine
- Removing the Lavalink dependency in favor of an in-process audio pipeline (we've been there; it doesn't work)

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE.md) — the same terms as the rest of the project.

## Questions

If you're not sure whether something is in scope, ask in the issue tracker before sinking time into a PR. Easier to course-correct early than to ask for changes after the work is done.
