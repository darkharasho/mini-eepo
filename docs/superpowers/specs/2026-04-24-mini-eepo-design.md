# MiniEepo — Design Spec
_Date: 2026-04-24_

## Overview

A R.E.P.O. BepInEx mod that permanently shrinks all players, items, and valuables to 40% of their original size on spawn. Uses ScalerCore as a hard dependency for all scaling logic. Thunderstore-compatible and REPOConfig-compatible.

---

## Repository Structure

```
mini-eepo/
├── src/
│   └── Plugin.cs          # Plugin entry point + all Harmony patches
├── MiniEepo.csproj        # C# project targeting netstandard2.1
├── manifest.json          # Thunderstore package manifest
├── icon.png               # 256x256 PNG (user-supplied)
├── README.md
└── CHANGELOG.md
```

---

## Plugin Entry Point

**GUID:** `darkharasho.MiniEepo`  
**Name:** `MiniEepo`  
**Version:** `1.0.0`

### Attributes
```csharp
[BepInPlugin("darkharasho.MiniEepo", "MiniEepo", "1.0.0")]
[BepInDependency("Vippy.ScalerCore", BepInDependency.DependencyFlags.HardDependency)]
```

### Config
One entry, bound in `Awake()`:

| Key | Section | Default | Range | Description |
|-----|---------|---------|-------|-------------|
| `ScaleFactor` | `General` | `0.4` | `0.1–1.0` | Scale multiplier applied to all players, items, and valuables |

Uses `AcceptableValueRange<float>(0.1f, 1.0f)` so REPOConfig renders it as a slider automatically (no special registration needed).

### Awake
1. Bind `ScaleFactor` config entry.
2. Run `new Harmony("darkharasho.MiniEepo").PatchAll()`.
3. Log loaded message.

---

## Harmony Patches

Three Postfix patches, all in `Plugin.cs`. Each builds a `ScaleOptions` from the live config value at call time (so REPOConfig hot-changes apply on next spawn without restart).

### Helper (shared inline logic)
```csharp
static void Shrink(GameObject go)
{
    var opts = ScaleOptions.Default;
    opts.Factor = Plugin.ScaleFactor.Value;
    ScaleManager.ApplyIfNotScaled(go, opts);
}
```

### Patch 1 — Players
```csharp
[HarmonyPatch(typeof(PlayerAvatar), "Start")]
static class PlayerAvatarPatch
{
    static void Postfix(PlayerAvatar __instance) => Shrink(__instance.gameObject);
}
```

### Patch 2 — Items
```csharp
[HarmonyPatch(typeof(ItemAttributes), "Start")]
static class ItemAttributesPatch
{
    static void Postfix(ItemAttributes __instance) => Shrink(__instance.gameObject);
}
```

### Patch 3 — Valuables
```csharp
[HarmonyPatch(typeof(ValuableObject), "Start")]
static class ValuableObjectPatch
{
    static void Postfix(ValuableObject __instance) => Shrink(__instance.gameObject);
}
```

`ApplyIfNotScaled` is a no-op if an object is already scaled, so patches are safe to run multiple times on the same object.

---

## Project File (MiniEepo.csproj)

- Target: `netstandard2.1`
- References (Private=false, not copied to output):
  - `BepInEx.dll` — from `$(BepInExDir)/core/`
  - `0Harmony.dll` — from `$(BepInExDir)/core/`
  - `UnityEngine.CoreModule.dll` — from `$(REPO_REFS)`
  - `Assembly-CSharp.dll` — from `$(REPO_REFS)`
  - `ScalerCore.dll` — from `$(BepInExDir)/plugins/ScalerCore/`
- Default env var fallbacks point to the default Steam install path on Windows.

---

## Thunderstore Manifest

```json
{
    "name": "MiniEepo",
    "version_number": "1.0.0",
    "website_url": "https://github.com/darkharasho/mini-eepo",
    "description": "Shrinks all players, items, and valuables to 40% size. Everyone starts the round tiny.",
    "dependencies": [
        "BepInEx-BepInExPack-5.4.2100",
        "Vippy-ScalerCore-0.4.3"
    ]
}
```

---

## REPOConfig Compatibility

No special integration code required. REPOConfig auto-discovers all `ConfigEntry` objects from loaded plugins via reflection. The `ScaleFactor` float entry with `AcceptableValueRange<float>` will render as a slider in the in-game config menu automatically.

---

## Out of Scope

- Enemies are not shrunk (intentional — only players, items, valuables).
- No toggle on/off at runtime (always-on mod).
- No per-type scale factors (one shared value).
- No Thunderstore CLI (`thunderstore.toml`) setup — user publishes manually.
