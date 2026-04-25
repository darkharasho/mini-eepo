# MiniEepo

A R.E.P.O. mod that shrinks all players, items, and valuables to 40% of their original size. Everyone starts the round tiny.

## Features

- All players, items, and valuables start at configurable tiny size (default 40%)
- Valuables smoothly shrink further when placed in the extraction cart, then restore when removed
- Host settings sync to all clients automatically — only the host needs to configure the mod
- Voice pitch modulation when players are shrunk (toggleable)
- Compatible with [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) — adjust settings in-game via the config menu

## Configuration

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `PlayerScale` | `0.4` | `0.1–1.0` | Size multiplier for players |
| `ItemScale` | `0.4` | `0.1–1.0` | Size multiplier for items |
| `ValuableScale` | `0.4` | `0.1–1.0` | Size multiplier for valuables |
| `CartScale` | `1.0` | `0.1–1.0` | Extra shrink applied when a valuable is in the cart (`1.0` = no change, `0.5` = half size) |
| `VoiceMod` | `true` | — | Enable voice pitch modulation when players are shrunk |

Config file: `BepInEx/config/darkharasho.MiniEepo.cfg`

In multiplayer, the host's settings apply to all players. Non-host config values are ignored while in a room.

## Dependencies

- [BepInExPack](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/)
- [ScalerCore](https://thunderstore.io/c/repo/p/Vippy/ScalerCore/)

## Installation

Install via [Thunderstore Mod Manager](https://www.overwolf.com/app/Thunderstore-Thunderstore_Mod_Manager) or manually place `MiniEepo.dll` in `BepInEx/plugins/MiniEepo/`.
