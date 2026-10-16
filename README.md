# RimEventFeed

> **A live feed of your colony's events, for any program running beside RimWorld**

A RimWorld mod that reports what happens in your colony (raids, mental breaks, deaths, letters, messages) as JSON to a local program, and lets that program run a few safe commands such as pausing the game or drafting a colonist.

## 📋 About

RimEventFeed hooks the points where the game tells the player something happened and turns each of them into a small JSON event. Events go to a program on the same machine: a stream overlay, a logger, a companion app, or an AI agent watching the game with you. The mod does not decide what is important; it reports, and the program listening decides.

The same program can also send commands back over a loopback port protected by a per-launch token. Only a short allowlist of reversible commands exists.

This is a community-made mod. It is not affiliated with or endorsed by Ludeon Studios.

## ✨ Features

- 📨 **Colony events as JSON** - Letters, incidents, mental breaks, mental states, deaths, and messages
- 🔑 **Stable ids** - Every event carries `defName`, `ThingID`, and faction ids next to the localized text, so a subscriber works in any game language
- 🧹 **Clean text** - Game markup (`<color>`, name and faction tags) is stripped from letter and message text
- 🧵 **Never stalls the game** - Events are sent from a background thread; if nothing is listening they are dropped
- 🎮 **Remote commands** - Pause, resume, change speed, draft and undraft colonists, list colonists
- 🔒 **Loopback only** - Commands need a bearer token that changes every launch; browser pages are refused
- ⏸️ **Works while paused** - Commands run on the game's main thread, including while the game is paused

## 📦 Requirements

### Dependencies

1. **[Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077)** - Required for patching

### Compatibility

- RimWorld 1.6 (tested on 1.6.4871, macOS)
- Royalty, Ideology, Biotech: tested with all three active

## 🚀 Installation

1. Install **Harmony** from the Steam Workshop or GitHub
2. Build RimEventFeed (see [Development](#️-development)) and copy `About/` and `1.6/` into `Mods/RimEventFeed/` in your RimWorld folder
3. Enable **RimEventFeed** in the mod list
4. Ensure load order: `Harmony → RimEventFeed`
5. Restart the game

## ⚙️ Configuration

There is no in-game settings window yet. Ports are fixed:

| What | Where |
|---|---|
| Events | `POST http://127.0.0.1:8799/events`, one JSON object per request, also logged to `Player.log` as `[RimEventFeed] {...}` |
| Commands | `http://127.0.0.1:8798/<action>` |
| Command token | `RimEventFeed/token` in the RimWorld config folder, new on every launch |

**Turn on "Run in background" in Options.** With it off (the default), the game stops while another window has focus, so no event is sent and no command runs.

### Event format

```json
{
  "kind": "death",
  "ticksGame": 12836,
  "sentAtUnixMs": 1792120466154,
  "fields": {
    "pawn": { "thingId": "Human351", "def": "Human", "label": "Tommy",
              "faction": { "loadId": 14, "def": "PlayerColony", "name": "New Arrivals" },
              "kindDef": "Colonist", "isColonist": true },
    "damageDef": "Blunt",
    "instigator": { "thingId": "Human355", "def": "Human", "label": "Joey" },
    "hediffDef": "MissingBodyPart"
  }
}
```

| kind | When | Fields |
|---|---|---|
| `letter` | A letter appears in the letter stack | letterId, letterDef, letterClass, label, text, targets |
| `incident` | An incident is attempted | incidentDef, category, succeeded, points, forced, faction, target |
| `mentalBreak` | A mental break starts | mentalBreakDef, workerClass, intensity, mentalStateDef, reason, causedByMood, pawn |
| `mentalState` | A mental state starts | mentalStateDef, label, reason, causedByMood, pawn |
| `death` | The player is notified of a death | pawn, damageDef, instigator, hediffDef |
| `message` | A message is shown at the top left | messageDef, text, targets |
| `command` | A command was run | action, args, status, result |

One game event often produces several kinds at the same tick: a raid is a `letter` and an `incident`, a mental break is a `letter`, a `mentalState`, and a `mentalBreak`. Group by tick and subject if you want one entry per event.

### Commands

Send `Authorization: Bearer <token>`. POST requests must carry a `Content-Length` (an empty body is fine).

| Action | Method | Effect |
|---|---|---|
| `colonists` | GET | Free colonists with thingId, label, drafted, downed, mentalState |
| `pause` / `resume` | POST | Pause or resume the game |
| `speed?value=1\|2\|3` | POST | Set game speed |
| `draft?pawn=<thingId>` / `undraft?pawn=<thingId>` | POST | Draft or release a colonist |

Responses are JSON with `ok`, the resulting state, and `waitedMs` (time the command waited for the main thread). A command that the game does not pick up within 5 seconds returns 503 and is discarded.

```sh
TOKEN=$(cat "$HOME/Library/Application Support/RimWorld/Config/RimEventFeed/token")
curl -X POST --data-binary @/dev/null -H "Authorization: Bearer $TOKEN" http://127.0.0.1:8798/pause
```

## 🛠️ Development

### Building from Source

```sh
cd Source/RimEventFeed

# Build against the Krafs.Rimworld.Ref reference assemblies (default)
dotnet build -c Release

# Or build against your installed game's Managed folder (macOS path in the csproj)
dotnet build -c Release -p:UseLocalDlls=true
```

The DLL is written to `1.6/Assemblies/RimEventFeed.dll`. Requires the .NET SDK (tested with 9.0); the target is .NET Framework 4.7.2.

### Project Structure

```
RimEventFeed/
├── About/              # Mod metadata
│   └── About.xml
├── Source/             # C# source code
│   └── RimEventFeed/
│       ├── Events.cs                  # Harmony hooks, event JSON, sender
│       ├── Commands.cs                # Command server, main-thread runner, allowlist
│       └── RimEventFeed.csproj
├── tools/              # Development helpers
│   ├── dump_receiver.py               # Prints and records incoming events
│   └── cmd.sh                         # Sends a command with the current token
├── 1.6/Assemblies/     # Compiled DLL for RimWorld 1.6 (build output)
└── LICENSE             # MIT License
```

### Development Roadmap

- [x] Harmony hooks for letters, incidents, mental breaks and states, deaths, messages
- [x] JSON events with stable ids and stripped markup
- [x] Background sender that never blocks the game
- [x] Token-protected loopback command channel
- [x] Pause, resume, speed, draft, undraft, colonist list
- [ ] Colony state snapshot (resources, threats, moods)
- [ ] In-game settings for ports and event kinds
- [ ] Measure and document the per-frame cost
- [ ] Steam Workshop release

## 🤝 Contributing

Contributions are welcome! This project is MIT licensed.

### Guidelines

- Keep game API calls on the main thread; background threads only send and receive data
- Every new command must be reversible by another command and validate its target
- Describe the in-game run that shows your change working (game version, what you triggered, the resulting `Player.log` lines or events)
- Never commit decompiled game code or game assemblies
- See [AGENTS.md](AGENTS.md) for the full rules

## 📄 License

This work is licensed under the **MIT License**. See [LICENSE](LICENSE) for full terms.

### Third-Party Licenses

- **Harmony**: MIT License by [Andreas Pardeike](https://github.com/pardeike/Harmony)
- **Krafs.Rimworld.Ref**: reference assemblies used at build time only, not shipped
- **RimWorld**: Proprietary license by Ludeon Studios

## 🐛 Known Issues

- Ports are fixed (8799 for events, 8798 for commands)
- Events are sent one HTTP request each; there is no stream endpoint to subscribe to yet
- One game event arrives as several event kinds; subscribers have to group them
- `incident` also reports failed attempts (`succeeded: false`)
- Tested on macOS only

## 📞 Support

- **Issues**: [GitHub Issues](https://github.com/yw0nam/RimEventFeed/issues)

## 🙏 Acknowledgments

- **Andreas Pardeike** - Creator of Harmony
- **Ludeon Studios** - RimWorld
- **[RIMAPI](https://github.com/IlyaChichkov/RIMAPI)** - Its source was a useful reference for where to hook game events
- **RimWorld Modding Community** - Documentation and support

---

**Note**: The command channel can change your game. It only listens on your own machine and needs the current token, but only run programs you trust beside it.
