# Building & Contributing

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download)
- A R.E.P.O. install (for game DLLs — BepInEx and ScalerCore are pulled via NuGet/libs automatically)

## Building

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

`package.sh` also auto-deploys to the r2modman profile set by `R2_PROFILE` (default: `10-30-update`) for local testing.

## Packaging for Thunderstore

A 256×256 `icon.png` must exist at the repo root before packaging (not committed — create your own).

```bash
./package.sh    # builds, deploys to r2modman, and zips in one step
```

Outputs `MiniEepo-<version>.zip` ready for Thunderstore upload.

**Manual zip** (no subdirectory):
- `manifest.json`
- `icon.png`
- `README.md`
- `bin/Release/netstandard2.1/MiniEepo.dll`

## Releasing

1. Bump `version_number` in `manifest.json` and `VERSION` in `package.sh`
2. Run `./package.sh` to build and package
3. Upload the zip to [Thunderstore](https://thunderstore.io/c/repo/) — each release requires a new version number
