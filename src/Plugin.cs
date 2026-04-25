using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ScalerCore;
using UnityEngine;

namespace MiniEepo
{
    [BepInPlugin("darkharasho.MiniEepo", "MiniEepo", "1.0.0")]
    [BepInDependency("Vippy.ScalerCore", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<float> ScaleFactor = null!;

        private void Awake()
        {
            Log = Logger;

            ScaleFactor = Config.Bind(
                "General",
                "ScaleFactor",
                0.4f,
                new ConfigDescription(
                    "Scale multiplier applied to all players, items, and valuables (0.4 = 40% of original size)",
                    new AcceptableValueRange<float>(0.1f, 1.0f)
                )
            );

            new Harmony("darkharasho.MiniEepo").PatchAll();
            Log.LogInfo("MiniEepo v1.0.0 loaded — everything is tiny now.");
        }

        internal static void Shrink(GameObject go)
        {
            var opts = ScaleOptions.Default;
            opts.Factor = ScaleFactor.Value;
            ScaleManager.ApplyIfNotScaled(go, opts);
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), "Start")]
    internal static class PlayerAvatarPatch
    {
        private static void Postfix(PlayerAvatar __instance) => Plugin.Shrink(__instance.gameObject);
    }

    [HarmonyPatch(typeof(ItemAttributes), "Start")]
    internal static class ItemAttributesPatch
    {
        private static void Postfix(ItemAttributes __instance) => Plugin.Shrink(__instance.gameObject);
    }

    [HarmonyPatch(typeof(ValuableObject), "Start")]
    internal static class ValuableObjectPatch
    {
        private static void Postfix(ValuableObject __instance) => Plugin.Shrink(__instance.gameObject);
    }
}
