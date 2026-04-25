# MiniEepo

A R.E.P.O. mod that shrinks all players, items, and valuables to 40% of their original size.

## Features

- All players, items, and valuables start tiny
- Scale factor is configurable (default 0.4 = 40%)
- Compatible with [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) — adjust the scale in-game via the config menu

## Configuration

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `ScaleFactor` | `0.4` | `0.1–1.0` | Size multiplier for all shrinkable objects |

Config file: `BepInEx/config/darkharasho.MiniEepo.cfg`

## Building

Requires the .NET SDK and a R.E.P.O. install with BepInEx and ScalerCore.

**Windows:**
```bash
dotnet build MiniEepo.csproj --configuration Release
# Override game path if not the default Steam location:
dotnet build MiniEepo.csproj --configuration Release /p:GameDir="D:\Games\REPO"
```

**Linux (Steam/Proton):**
```bash
./package.sh
# Override game path:
GAME_DIR="$HOME/.steam/steam/steamapps/common/REPO" ./package.sh
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