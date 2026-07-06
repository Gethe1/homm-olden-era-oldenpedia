# OldenPedia

A Civilopedia-style in-game encyclopedia for **Heroes of Might and Magic: Olden
Era**, built as a **BepInEx 6 IL2CPP** plugin. It reads the game's own data,
localization, and visual style live at runtime and shows units, heroes,
artifacts, and skills in a themed in-game window.

Current version: **0.75.0**.

## Features

- Live data extraction — units, heroes, artifacts, and skills read from the
  game's own config registry each run; nothing hardcoded.
- Localized names and descriptions, with automatic language detection.
- Themed uGUI window matching the game's own visual style: faction/group
  accordion on the left, variant tabs and detail on the right, a search box
  that filters the list as you type.
- 3D unit portraits rendered from each unit's own in-game model (opt-in).
- Portrait icons for heroes, artifacts, and skills.
- Resource-bar button plus a rebindable toggle key.
- Input blocking while the window is open (the map underneath ignores clicks,
  scroll-zoom, and hotkeys).
- Artifact and skill effects formatted from the game's own bonus data,
  including artifact set combination bonuses.
- Skills grouped one collapsible entry per logical skill, with tiers nested
  inside; duplicates across game modes are merged into a single entry.

## Requirements

- .NET SDK 6.0+
- The BepInEx 6 IL2CPP pack for Olden Era (with its deobfuscation config)
  installed into the game
- The game launched once after installing BepInEx, to generate
  `BepInEx/interop/`

## Build

1. Set `<GameDir>` in `OldenPedia.csproj` to your install path.
2. `dotnet build -c Release`
3. Copy `bin/Release/net6.0/OldenPedia.dll` into `<GameDir>/BepInEx/plugins/`.
4. Launch the game.

### Linux / Proton
Add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the game's Steam launch
options, launch once to generate `BepInEx/interop/`, then build — either
`dotnet build -c Release -p:GameDir="<GameDir>"` or `./build.sh "<GameDir>"`.

## Config

`<GameDir>/BepInEx/config/oldenpedia.civilopedia.cfg`, under `[General]`:

| Key | Default | Meaning |
|-----|---------|---------|
| `ToggleKey` | `Period` | Key that opens/closes the window |
| `Language` | `auto` | A folder name (`german`, `polish`, …) to force a language, or `auto` |
| `BlockMapInput` | `true` | Whether the map is blocked from input while the window is open |
| `Show3DUnitPreview` | `false` | Opt-in 3D unit portrait rendering |

## Dev probes

Discovery tools; each writes to `<GameDir>/BepInEx/OldenPedia/*.txt`.

| Key | Probe |
|-----|-------|
| `Y` | Visual style (colors/sprites/fonts) of whatever screen is open |
| `L` | Language-setting detection |
| F2 | Ability data |
| F3 | Data extractor dump |
| F4 | Type detail (targets set in `[General] InspectTypes`) |
| F5–F9 | Container / type / data / game-data / UI probes |
| F10 | Localization probe |
| F11 | Language loader probe |

## Known limitations

- Unit passives have no discoverable display name.
- Unit portrait facing direction can't be corrected if a prefab was authored
  facing a different way than others.
- Artifact set bonuses don't show a specific piece-count per tier (labeled
  "Partial" / "Full" instead).

## Files

| File | Purpose |
|------|---------|
| `src/Plugin.cs` | BepInEx entry, config, Harmony init |
| `src/DataExtractor.cs` | Live data → category model, effects, skill grouping |
| `src/PediaWindow.cs` | Window UI: master/detail, tabs, portraits, search |
| `src/PediaBehaviour.cs` | Input loop, toggle key, probe keys |
| `src/PediaButton.cs` | Resource-bar button |
| `src/InputBlocker.cs` | Harmony patches blocking map input while open |
| `src/LangLoader.cs` | Localization loader and language detection |
| `src/Localizer.cs` | Loc key resolution |
| `src/IconLoader.cs` | Resolves icon strings to loaded sprites |
| `src/FontLoader.cs` | Resolves font names to loaded font assets |
| `src/UnitModel.cs` | Unit prefab access |
| `src/UnitPreviewRenderer.cs` | 3D unit portrait rendering |
| `src/NullablePolyfill.cs` | Compiler compatibility shim |
| `src/Il2CppUtil.cs`, `src/FileLogListener.cs` | Interop helpers, file logging |
| `src/*Probe.cs` | Dev discovery probes (see Dev probes table) |
| `OldenPedia.csproj`, `build.sh`, `nuget.config` | Build config |

## License

This repository's own code is MIT-licensed (see `LICENSE`). That does not
extend to `Hex.dll` or any other game assets, which remain the property of
their original developers and are not redistributed here.
