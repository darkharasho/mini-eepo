# MiniEepo

A R.E.P.O. mod that shrinks all players, items, and valuables to 40% of their original size.

## Features

- All players, items, and valuables start tiny
- Scale factor is configurable (default 0.4 = 40%)
- Compatible with [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) — adjust the scale in-game via the config menu

## Configuration

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `PlayerScale` | `0.4` | `0.1–1.0` | Size multiplier for players |
| `ItemScale` | `0.4` | `0.1–1.0` | Size multiplier for items |
| `ValuableScale` | `0.4` | `0.1–1.0` | Size multiplier for valuables |

Config file: `BepInEx/config/darkharasho.MiniEepo.cfg`

## Building

Requires the [.NET SDK](https://dotnet.microsoft.com/download) and a R.E.P.O. install (for game DLLs). BepInEx and ScalerCore are pulled automatically via NuGet/libs — no separate install needed to compile.

**Windows:**
```bash
dotnet build MiniEepo.csproj --configuration Release
# Override game path if not the default Steam location:
dotnet build MiniEepo.csproj --configuration Release /p:GameDir="D:\Games\REPO"
```

**Linux (Steam):**
```bash
./package.sh
# Override game path:
GAME_DIR="/path/to/steamapps/common/REPO" ./package.sh
```

Output DLL: `bin/Release/netstandard2.1/MiniEepo.dll`

## Packaging for Thunderstore

A 256×256 `icon.png` must exist at the repo root before packaging (not committed — create your own).

**Linux:**
```bash
./package.sh          # builds and zips in one step
```

The script outputs `MiniEepo-1.0.0.zip` ready for Thunderstore upload.

**Manual:**
Zip these four files (no subdirectory):
- `manifest.json`
- `icon.png`
- `README.md`
- `bin/Release/netstandard2.1/MiniEepo.dll`

## Dependencies

- [BepInExPack](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/)
- [ScalerCore](https://thunderstore.io/c/repo/p/Vippy/ScalerCore/)

## Installation

Install via [Thunderstore Mod Manager](https://www.overwolf.com/app/Thunderstore-Thunderstore_Mod_Manager) or manually place `MiniEepo.dll` in `BepInEx/plugins/MiniEepo/`.