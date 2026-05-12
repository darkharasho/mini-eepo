# MiniEepo

> ## 📦 This repo is archived.
> **MiniEepo has moved to the [REPOsitory monorepo](https://github.com/darkharasho/REPOsitory/tree/main/mods/mini-eepo).**
> All future development, issues, and releases live there.

A R.E.P.O. mod that shrinks all players, items, and valuables to 40% of their original size. Everyone starts the round tiny.

## Features

- All players, items, and valuables start at configurable tiny size (default 40%)
- Valuables smoothly shrink further when placed in the extraction cart, then restore when removed
- Held guns are stabilized so shotguns and heavy weapons don't droop out of view
- Taking damage no longer un-shrinks the player; revive also preserves the shrunk state
- Items pulled from inventory stay small (no full-size flash on un-pocket)
- Shop level is excluded from shrinking by default so items stay readable while you browse — toggleable
- Host scale settings sync to all clients automatically — only the host needs to configure the mod
- Voice pitch modulation when players are shrunk — each client controls what *they* hear (toggleable per-player)
- Compatible with [REPOConfig](https://thunderstore.io/c/repo/p/nickklmao/REPOConfig/) — adjust settings in-game via the config menu

## Configuration

| Key | Default | Range | Description |
|-----|---------|-------|-------------|
| `PlayerScale` | `0.4` | `0.1–1.0` | Size multiplier for players |
| `ItemScale` | `0.4` | `0.1–1.0` | Size multiplier for items |
| `ValuableScale` | `0.4` | `0.1–1.0` | Size multiplier for valuables |
| `CartScale` | `1.0` | `0.1–1.0` | Extra shrink applied when a valuable is in the cart (`1.0` = no change, `0.5` = half size) |
| `ShrinkInShop` | `false` | — | If true, shrinking applies in the shop level too. Default false leaves shop items at normal size. |
| `VoiceMod` | `true` | — | Enable voice pitch modulation when players are shrunk |

Config file: `BepInEx/config/darkharasho.MiniEepo.cfg`

In multiplayer, the host's scale settings (`PlayerScale`, `ItemScale`, `ValuableScale`, `CartScale`, `ShrinkInShop`) apply to all players — non-host values for these are ignored while in a room. `VoiceMod` is intentionally local: each client decides whether they hear pitched-up voices.

## Dependencies

- [BepInExPack](https://thunderstore.io/c/repo/p/BepInEx/BepInExPack/)
- [ScalerCore](https://thunderstore.io/c/repo/p/Vippy/ScalerCore/)

## Installation

Install via [Thunderstore Mod Manager](https://www.overwolf.com/app/Thunderstore-Thunderstore_Mod_Manager) or manually place `MiniEepo.dll` in `BepInEx/plugins/MiniEepo/`.

## Thanks

Big thanks to **Vette** and **Grimm** for testing.
