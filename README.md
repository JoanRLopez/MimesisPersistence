# MimesisPersistence

MelonLoader mod for **Mimesis** that persists voice data (SpeechEvents) and replay data across game sessions. When you load a save, the mimics remember the voices they recorded in previous sessions.

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20Me%20a%20Coffee-ffdd00?style=for-the-badge&logo=buy-me-a-coffee&logoColor=black)](https://buymeacoffee.com/joanr)

**Host-only**: only the host saves and injects voice data. Clients don't need the mod.

## Features

### Voice Persistence
- Saves all recorded SpeechEvents (mimic voice recordings) per save slot
- Restores them when loading a save, matched to the correct player
- Cross-session player matching via SteamID, even if DissonanceID changes between sessions

### Disconnected Player Protection
- If a player disconnects mid-session, their voice events are cached in memory
- Cached events are included when the host saves
- If the player reconnects in the same session, their events are restored to their new archive automatically

### Hallucination Voice Fallback
- In solo play, hallucination voices normally require other players' archives
- The mod falls back to the local archive so hallucinations still work with persisted voices

### Safe File I/O
- Writes to `.tmp` first, then renames (prevents corrupted files from crashes mid-save)
- Automatic `.bak` backup of previous save before overwriting
- On load, falls back to `.bak` if the main file is missing or corrupt

### Debug Tool (DEBUG build only)
- Press **F9** to open the debug panel
- Browse live and loaded SpeechEvents
- Play back voice events (Opus decode)
- View event pool status (Pending/Injected/Fallback)
- View disconnected player cache
- Force fallback injection
- Save/load events to debug file

## Architecture

### Core Files

| File | Purpose |
|------|---------|
| `MimesisPersistenceMod.cs` | MelonMod entry point, applies Harmony patches |
| `MimesisSaveManager.cs` | Save/load logic, binary serialization, safe file I/O |
| `SpeechEventPoolManager.cs` | 3-state pool (Pending/Injected/Fallback) + disconnected cache |
| `DebugAudioTester.cs` | DEBUG-only IMGUI tool for testing (F9) |

### Patches

| Patch | Hooks | Purpose |
|-------|-------|---------|
| `SpeechEventArchivePatches` | `SpeechEventArchive.OnStartClient` | Injects saved events into archives on load |
| `SpeechEventArchiveDisconnectPatches` | `SpeechEventArchive.OnStopClient` | Caches events before archive destruction (player disconnect) |
| `VoiceManagerPatches` | `VoiceManager.GetRandomOtherSpeechEventArchive` | Fallback to local archive for hallucinations |
| `MaintenanceRoomPatches` | `MaintenanceRoom.SaveGameData` | Triggers Mimesis save alongside game save |
| `PlatformMgrPatches` | `PlatformMgr.Delete` | Deletes Mimesis data when a save is deleted |

### Event Pool States

```
PENDING   ─── loaded from disk, waiting for a matching archive
INJECTED  ─── matched to a player's archive and added
FALLBACK  ─── forced into the host's local archive (debug/manual)
```

### Player Matching Flow

```
Archive starts (OnStartClient)
  │
  ├─ Level 1: Direct DissonanceID match
  │   (same ID across sessions)
  │
  ├─ Level 2: SteamID match
  │   resolve PlayerUID → SteamID → saved DissonanceID → match events
  │
  └─ Disconnected cache check
      resolve SteamID → cached DissonanceID → reclaim events
```

## Saved Data Structure

```
{SaveFolder}/MimesisData/
├── Slot0/                          (autosave)
│   ├── speech_events.bin           Voice events (v2: metadata + audio)
│   ├── speech_events.bin.bak       Backup of previous save
│   ├── replay_play.bin             Replay packets
│   ├── replay_voice.bin            Replay voice data
│   ├── player_mapping.json         SteamID → DissonanceID mapping
│   ├── player_mapping.json.bak     Backup
│   ├── metadata.json               Version, timestamp, counts
│   └── metadata.json.bak           Backup
├── Slot1/
├── Slot2/
└── Slot3/
```

### Binary Format (v2)

```
[eventCount: int32]
  for each event:
    [metaLen: int32][metadata: bytes]
    [audioLen: int32][compressedAudio: bytes]
```

## Building

1. Copy `PathConfig.props.example` to `PathConfig.props`
2. Edit `PathConfig.props` with your game installation path
3. Open `MimesisPersistence.sln` in Visual Studio
4. Build (Debug for F9 tool, Release for production)
5. Output: `bin\{Config}\MimesisPersistence.dll`

## Installation

Copy `MimesisPersistence.dll` to the game's `Mods/` folder (requires MelonLoader).

## Testing

1. Host a game
2. Play with others (talk to record voice events)
3. Return to tram or manual save in Maintenance Room
4. Check console: `[MimesisPersistence] Saved slot X. Speech=N, Play=N, Voice=N`
5. Close and reload that save
6. Check console: `[MimesisPersistence] Injected N total events into archive`

### Testing disconnected player cache

1. Host a game with another player
2. Other player talks (voice events recorded)
3. Other player disconnects
4. Check console: `[MimesisPersistence] Cached N events from disconnected player`
5. Save the game
6. Check console: `CollectAllSpeechEvents: X from live archives + Y from disconnected cache`

### Testing backup recovery

1. Save a game normally
2. Corrupt or delete `speech_events.bin` manually
3. Load that save
4. Check console: `[MimesisPersistence] Recovered from backup: speech_events.bin.bak`

## Dependencies

- [MelonLoader](https://melonwiki.xyz/) (modding framework)
- Harmony 2.x (included with MelonLoader)
- .NET Framework 4.7.2

## Acknowledgements
Thanks to everyone who shared ideas, gave feedback, or showed interest in this project.
Thanks to friends for the patience while I kept talking about mimics and voices.

And thank you for checking out this mod!
JoanR.
