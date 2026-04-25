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
        internal static ConfigEntry<float> PlayerScale = null!;
        internal static ConfigEntry<float> ItemScale = null!;
        internal static ConfigEntry<float> ValuableScale = null!;

        private void Awake()
        {
            Log = Logger;

            var range = new AcceptableValueRange<float>(0.1f, 1.0f);

            PlayerScale = Config.Bind("Scaling", "PlayerScale", 0.4f,
                new ConfigDescription("Scale multiplier for players (0.4 = 40%)", range));

            ItemScale = Config.Bind("Scaling", "ItemScale", 0.4f,
                new ConfigDescription("Scale multiplier for items (0.4 = 40%)", range));

            ValuableScale = Config.Bind("Scaling", "ValuableScale", 0.4f,
                new ConfigDescription("Scale multiplier for valuables (0.4 = 40%)", range));

            new Harmony("darkharasho.MiniEepo").PatchAll();
            Log.LogInfo("MiniEepo v1.0.0 loaded — everything is tiny now.");
        }

        internal static void Shrink(GameObject go, float factor)
        {
            var opts = ScaleOptions.Default; // ScaleOptions is a struct; this is a safe value copy
            opts.Factor = factor;
            ScaleManager.ApplyIfNotScaled(go, opts);
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), "Start")]
    internal static class PlayerAvatarPatch
    {
        private static void Postfix(PlayerAvatar __instance) =>
            Plugin.Shrink(__instance.gameObject, Plugin.PlayerScale.Value);
    }

    [HarmonyPatch(typeof(ItemAttributes), "Start")]
    internal static class ItemAttributesPatch
    {
        private static void Postfix(ItemAttributes __instance) =>
            Plugin.Shrink(__instance.gameObject, Plugin.ItemScale.Value);
    }

    [HarmonyPatch(typeof(ValuableObject), "Start")]
    internal static class ValuableObjectPatch
    {
        private static void Postfix(ValuableObject __instance) =>
            Plugin.Shrink(__instance.gameObject, Plugin.ValuableScale.Value);
    }
}
