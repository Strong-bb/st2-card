# STS2SelectCard

`STS2SelectCard` is a Slay the Spire 2 mod that adds two pause-menu tools:

- `Add Card`: opens a picker for the current character's unlocked card pool and adds the selected card directly to the deck.
- `Remove Card`: uses the game's native card-removal flow. In shops it uses the merchant removal path; elsewhere it uses the reward removal path.

## Quick Start

1. Copy the mod folder to your Slay the Spire 2 mods directory:
   `Slay the Spire 2/mods/STS2SelectCard`
2. Make sure the folder contains:
   - `STS2SelectCard.dll`
   - `STS2SelectCard.json`
3. Launch the game in modded mode.
4. Enter a run, open the pause menu, and use `Add Card` or `Remove Card`.

## Build From Source

1. Install .NET 9 SDK.
2. Open the project folder:
   `D:\project\card\sts-2-select-card`
3. Build the project:

```powershell
dotnet build .\STS2SelectCard.csproj -c Release
```

4. The compiled DLL is produced at:
   `.godot/mono/temp/bin/Release/STS2SelectCard.dll`

## Dev Console Commands

- `selectcard`
- `removecard`

## Notes

- `Remove Card` is intentionally routed through the game's native removal functions instead of manually editing the deck.
- Shop rooms use the merchant removal flow. Other rooms use the reward removal flow.
- Native removal is much safer than direct deck mutation, but it still depends on the current game state. If a specific room UI breaks, check the latest game log first.
- This mod is distributed as DLL-only. `has_pck` is set to `false` in the manifest.
- If the game is running while you rebuild, Windows may lock `STS2SelectCard.dll` and prevent overwrite.

## Repository Layout

- `Scripts/Entry.cs`: main Harmony patches and pause-menu integration
- `STS2SelectCard.csproj`: C# project file
- `STS2SelectCard.json`: mod manifest

## Troubleshooting

- Button shows up but does nothing:
  check that the latest DLL was copied into the game's `mods/STS2SelectCard` folder.
- Modded run shows a different save:
  Slay the Spire 2 uses a separate `modded` save directory.
- Build succeeds but the game still loads old behavior:
  fully exit the game before copying the rebuilt DLL.
