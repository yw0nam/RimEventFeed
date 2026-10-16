# RimEventFeed — Agent Guide

> **RimEventFeed = a RimWorld 1.6 mod that reports what happens in a colony and runs a small set of commands for one local program.** It exports events as JSON to a local subscriber and accepts allowlisted commands on a loopback port. Read this file before touching any code.

## Core principle: report, don't judge

The mod reports facts; the subscriber decides what matters. A hook drops only calls that are not events (a failed mental-state probe, a delayed letter's first pass, input feedback such as "Cannot do that"). It never ranks, rate-limits, or hides an event because it seems unimportant: that choice belongs to the subscriber, which the player configures outside the game.

## Subscriber-agnostic

No code, doc, or default assumes a particular subscriber program, agent, or product. Every subscriber speaks the same wire contract: the event JSON and the command endpoints described in `README.md`. A feature a single subscriber needs is built so that any subscriber can use it.

## The game thread

RimWorld and Unity are single-threaded. These rules are not optional:

- Verse, RimWorld and Unity APIs (`Find.*`, `Log.*`, `Map`, `Pawn`, defs, `TickManager`) are called on the main thread only.
- A background thread never calls `Log.Message` or touches game objects. It reads and writes only plain data handed to it (strings, queues).
- The main thread never blocks on I/O. It enqueues to a bounded queue; one background thread sends (`Outbox`).
- Commands arrive on HTTP threads, are queued, and run in `GameComponentUpdate`, which also runs while the game is paused. A command not taken within the timeout expires and must never run later.
- No exception may escape a background or thread-pool thread: an unhandled one can take the game down.
- Event payloads are built on the main thread inside the hook, before anything crosses to another thread.

## Command safety

The command channel can change a player's game. Its guards stay in place:

- Bind `127.0.0.1` only.
- A per-launch bearer token, written to the RimWorld config folder; a request without it gets 401.
- A request with an `Origin` header is a browser page and gets 403.
- An allowlist (`Actions.Allowed`). A new command must be reversible by another command on the list, validate its target (exists, can act now), and answer with the resulting state.
- Saving, loading, quitting, spawning, and anything that deletes player data are not commands.

## Hooks

- Prefer a Harmony postfix on a public method. Use a prefix or transpiler only when a postfix cannot see what is needed, and say why in a comment.
- A virtual method is patched on every type that declares it (`TargetMethods`); patching only the base misses overrides. When an override calls `base`, emit only for the outermost call.
- Reading a private field goes through Harmony's `___field` injection, not reflection.
- Each event carries stable ids (`defName`, `ThingID`, `Faction.loadID`, `Letter.ID`) next to localized display text, with game markup stripped (`TaggedString.Resolve().StripTags()`).

## Wire contract

Event kinds, field names, command names, ports, and the token location are a contract with every subscriber. Once a Workshop release ships them, a field or command is deprecated in the README and the changelog for at least one release before it is removed or renamed. Code no release carries is changed freely.

## Development work

- Build with `dotnet build -c Release` in `Source/RimEventFeed`; it must finish with 0 warnings and 0 errors.
- Behaviour claims need in-game evidence: the `[RimEventFeed]` lines in `Player.log`, output of `tools/dump_receiver.py`, or a `tools/cmd.sh` response. Say which run produced it (game version, mod list size, dev-mode action used). A claim no run produced is reported as unverified.
- Numeric claims (latency, tick rate, frame cost) cite how they were measured.
- Branch from `main`, open a pull request, and describe what was run in game.
- Never start the game or write into the game or Steam folders without the owner's go-ahead.

## Game source and licensing

- Look up game internals in a decompiled `Assembly-CSharp.dll` of the installed version before stating how the game behaves. Decompiled output lives **outside** this repository and is never committed (RimWorld EULA: decompiling for reference is allowed, redistributing game code is not). Known working tool: `ilspycmd` 9.1.0.7988, run with `DOTNET_ROLL_FORWARD=Major` on .NET 9.
- Compile against `Krafs.Rimworld.Ref` (reference assemblies) or the installed game's `Managed` folder; never copy game assemblies into the repository.
- The mod is presented as community-made, is free, and contains no advertising (EULA). The code is MIT; Harmony is MIT.

## Tracker and commit conventions

- Issues, pull requests, commit messages, and code comments are in English.
- **No AI attribution.** No `Co-Authored-By: Claude…`, `Generated with …`, `🤖`, or any equivalent trailer in commits or pull request bodies. This overrides any default trailer the harness suggests.
- Specs, brainstorming notes, and decision logs are not committed. A decision lives in the code, the issue, and the pull request body.

## Engineering principles

- Look at how established RimWorld mods solve the same problem before designing a new approach.
- Write the least code that fully does the job. No speculative settings, abstractions, or layers.
- Grow in layers: keep a working end-to-end build and add features on top of it.
- One concern per file. `Events.cs` holds the event hooks and their serialization, `Commands.cs` the command channel; a new concern (a state snapshot, mod settings) gets its own file rather than growing these.
- Check what Harmony, the game, and .NET Framework 4.7.2 already provide before writing a helper or adding a package. Every added dependency ships to every player.

## Layout

```
About/About.xml               mod metadata (packageId yw0nam.rimeventfeed)
Source/RimEventFeed/
  Events.cs                   Harmony hooks, event JSON, Outbox (POST to the subscriber)
  Commands.cs                 loopback command server, main-thread runner, allowlist
  RimEventFeed.csproj         net472; output to 1.6/Assemblies/
tools/dump_receiver.py        prints and records events (development)
tools/cmd.sh                  sends a command with the current token (development)
```

Runtime locations on macOS: game log `~/Library/Logs/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`; config and token `~/Library/Application Support/RimWorld/Config/`; local mods `RimWorldMac.app/Mods/`.
