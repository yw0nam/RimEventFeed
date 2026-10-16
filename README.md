# RimEventFeed

A community-made RimWorld mod that turns what happens in a colony into a feed another program can subscribe to, and accepts a small set of reversible commands from that program.

Status: early research build. It is used to measure event shape and command latency; the subscriber side and the stream shape are still being decided ([yw0nam/YUI#1462](https://github.com/yw0nam/YUI/issues/1462)).

Requires RimWorld 1.6 and [Harmony](https://steamcommunity.com/sharedfiles/filedetails/?id=2009463077).

## Events

Each event is written to `Player.log` as `[RimEventFeed] {json}` and POSTed to `http://127.0.0.1:8799/events`. A background thread posts; if nothing listens, the event is dropped and the game keeps running.

```json
{ "kind": "death", "ticksGame": 12836, "sentAtUnixMs": 1792120466154,
  "fields": { "pawn": { "thingId": "Human351", "def": "Human", "label": "Tommy",
                        "faction": { "loadId": 14, "def": "PlayerColony", "name": "…" },
                        "kindDef": "Colonist", "isColonist": true },
              "damageDef": "Blunt", "instigator": { "thingId": "Human355", "…": "…" },
              "hediffDef": "MissingBodyPart" } }
```

| kind | hook | fields |
|---|---|---|
| `letter` | `LetterStack.ReceiveLetter` (delayed letters once, on arrival) | letterId, letterDef, letterClass, label, text, targets |
| `incident` | `IncidentWorker.TryExecute` | incidentDef, category, succeeded, points, forced, faction, target |
| `mentalBreak` | `MentalBreakWorker.TryStart`, every declaring subclass | mentalBreakDef, workerClass, intensity, mentalStateDef, reason, causedByMood, pawn |
| `mentalState` | `MentalStateHandler.TryStartMentalState` | mentalStateDef, label, reason, causedByMood, pawn |
| `death` | `Pawn_HealthTracker.NotifyPlayerOfKilled` | pawn, damageDef, instigator, hediffDef |
| `message` | `Messages.Message(Message, bool)`, input feedback excluded | messageDef, text, targets |
| `command` | a command run through the command channel | action, args, status, result |

Ids and defNames are stable across languages; `label` and `text` are in the game's language with markup stripped. One game event often arrives as several kinds at the same tick (a raid is `letter` + `incident`; a mental break is `letter` + `mentalState` + `mentalBreak`), so a subscriber groups by tick and subject.

## Commands

`http://127.0.0.1:8798/`, loopback only.

- Each launch writes a new token to `<RimWorld config folder>/RimEventFeed/token`; send it as `Authorization: Bearer <token>`.
- Requests carrying an `Origin` header (a browser page) are refused with 403.
- POST must carry `Content-Length` (the game's `HttpListener` answers 411 otherwise).
- Commands run on the game's main thread; one that does not run within 5 s returns 503 and is discarded.

| action | method | effect |
|---|---|---|
| `colonists` | GET | free colonists with thingId, label, drafted, downed, mentalState |
| `pause` / `resume` | POST | toggle pause |
| `speed?value=1\|2\|3` | POST | set game speed |
| `draft?pawn=<thingId>` / `undraft?pawn=<thingId>` | POST | draft or release a colonist |

With **Run in background** off (the game's default), the game loop stops while another window has focus, so no event is emitted and no command runs. Turn it on in Options for use beside another app.

## Build

```sh
cd Source/RimEventFeed
dotnet build -c Release     # writes 1.6/Assemblies/RimEventFeed.dll
```

Compiles against `Krafs.Rimworld.Ref` 1.6.4871 by default; `-p:UseLocalDlls=true` uses the installed game's `Managed` folder (macOS path in the csproj). Copy `About/` and `1.6/` into the game's `Mods/RimEventFeed/` and restart the game.

## Tools

- `tools/dump_receiver.py [port]` prints every event and appends it to `tools/events.jsonl`.
- `tools/cmd.sh <GET|POST> <action>[?query]` sends a command with the current token.
