using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ExitGames.Client.Photon;
using HarmonyLib;
using Photon.Pun;
using Photon.Realtime;
using ScalerCore;
using UnityEngine;

namespace MiniEepo
{
    [BepInPlugin("darkharasho.MiniEepo", "MiniEepo", "1.1.0")]
    [BepInDependency("Vippy.ScalerCore", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<float> PlayerScale = null!;
        internal static ConfigEntry<float> ItemScale = null!;
        internal static ConfigEntry<float> ValuableScale = null!;
        internal static ConfigEntry<float> CartScale = null!;
        internal static ConfigEntry<bool> VoiceMod = null!;

        // Active values — start from local config, overridden by host sync
        internal static float ActivePlayerScale;
        internal static float ActiveItemScale;
        internal static float ActiveValuableScale;
        internal static float ActiveCartScale;
        internal static bool ActiveVoiceMod;

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
            CartScale = Config.Bind("Scaling", "CartScale", 1.0f,
                new ConfigDescription("Extra scale multiplier applied when a valuable is in the cart (1.0 = no change, 0.5 = half size)", range));

            VoiceMod = Config.Bind("Audio", "VoiceMod", true,
                new ConfigDescription("Enable voice pitch modulation when players are shrunk"));

            ResetToLocalConfig();

            var go = new GameObject("MiniEepo_Syncer");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<SettingsSyncer>();

            var harmony = new Harmony("darkharasho.MiniEepo");
            harmony.PatchAll();

            // SetVoicePitchRPC may have overloads — find it manually so a missing
            // match can't abort PatchAll for the rest of the mod.
            var voiceMethod = AccessTools.Method(typeof(PlayerAvatar), "SetVoicePitchRPC",
                new[] { typeof(float) });
            voiceMethod ??= AccessTools.Method(typeof(PlayerAvatar), "SetVoicePitchRPC");
            if (voiceMethod != null)
                harmony.Patch(voiceMethod,
                    prefix: new HarmonyMethod(typeof(VoicePitchPatch), nameof(VoicePitchPatch.Prefix)));
            else
                Log.LogWarning("SetVoicePitchRPC not found — voice mod toggle disabled");

            Log.LogInfo("MiniEepo v1.1.0 loaded — everything is tiny now.");
        }

        internal static void ResetToLocalConfig()
        {
            ActivePlayerScale   = PlayerScale.Value;
            ActiveItemScale     = ItemScale.Value;
            ActiveValuableScale = ValuableScale.Value;
            ActiveCartScale     = CartScale.Value;
            ActiveVoiceMod      = VoiceMod.Value;
        }

        internal static readonly HashSet<int> ManagedObjects = new HashSet<int>();

        internal static void Shrink(GameObject go, float factor)
        {
            ManagedObjects.Add(go.GetInstanceID());
            var opts = ScaleOptions.Default; // ScaleOptions is a struct; this is a safe value copy
            opts.Factor = factor;
            ScaleManager.ApplyIfNotScaled(go, opts);
        }
    }

    internal class SettingsSyncer : MonoBehaviourPunCallbacks
    {
        private const string K_PLAYER   = "ME_PS";
        private const string K_ITEM     = "ME_IS";
        private const string K_VALUABLE = "ME_VS";
        private const string K_CART     = "ME_CS";
        private const string K_VOICE    = "ME_VM";

        // MonoBehaviourPunCallbacks.OnEnable() calls PhotonNetwork.AddCallbackTarget
        // immediately, which faults if Photon isn't initialized yet (e.g. during Plugin.Awake).
        // Suppress auto-registration and do it ourselves in Start instead.
        public override void OnEnable() { }
        public override void OnDisable() { }

        private void Start() => PhotonNetwork.AddCallbackTarget(this);
        private void OnDestroy() => PhotonNetwork.RemoveCallbackTarget(this);

        // Called on all clients when room properties change (including after PushHostSettings)
        public override void OnRoomPropertiesUpdate(ExitGames.Client.Photon.Hashtable changed)
        {
            if (changed.ContainsKey(K_PLAYER))   Plugin.ActivePlayerScale   = (float)changed[K_PLAYER];
            if (changed.ContainsKey(K_ITEM))      Plugin.ActiveItemScale     = (float)changed[K_ITEM];
            if (changed.ContainsKey(K_VALUABLE))  Plugin.ActiveValuableScale = (float)changed[K_VALUABLE];
            if (changed.ContainsKey(K_CART))      Plugin.ActiveCartScale     = (float)changed[K_CART];
            if (changed.ContainsKey(K_VOICE))     Plugin.ActiveVoiceMod      = (bool)changed[K_VOICE];
            Plugin.Log.LogInfo($"[Sync] Settings received from host — player={Plugin.ActivePlayerScale} item={Plugin.ActiveItemScale} valuable={Plugin.ActiveValuableScale} cart={Plugin.ActiveCartScale} voiceMod={Plugin.ActiveVoiceMod}");
        }

        // Non-host clients: read existing room properties on join
        public override void OnJoinedRoom()
        {
            if (PhotonNetwork.IsMasterClient)
            {
                PushHostSettings();
            }
            else
            {
                var props = PhotonNetwork.CurrentRoom?.CustomProperties;
                if (props != null) OnRoomPropertiesUpdate(props);
            }
        }

        // New master client takes over and pushes their settings
        public override void OnMasterClientSwitched(Player newMasterClient)
        {
            if (newMasterClient.IsLocal)
                PushHostSettings();
        }

        private void PushHostSettings()
        {
            if (PhotonNetwork.CurrentRoom == null) return;
            var props = new ExitGames.Client.Photon.Hashtable
            {
                [K_PLAYER]   = Plugin.PlayerScale.Value,
                [K_ITEM]     = Plugin.ItemScale.Value,
                [K_VALUABLE] = Plugin.ValuableScale.Value,
                [K_CART]     = Plugin.CartScale.Value,
                [K_VOICE]    = Plugin.VoiceMod.Value,
            };
            PhotonNetwork.CurrentRoom.SetCustomProperties(props);
            Plugin.Log.LogInfo($"[Sync] Host settings pushed — player={Plugin.PlayerScale.Value} item={Plugin.ItemScale.Value} valuable={Plugin.ValuableScale.Value} cart={Plugin.CartScale.Value}");
        }

        // Singleplayer / lobby: reset to local config
        public override void OnLeftRoom() => Plugin.ResetToLocalConfig();
    }

    [HarmonyPatch(typeof(PlayerAvatar), "Start")]
    internal static class PlayerAvatarPatch
    {
        private static IEnumerator ShrinkNextFrame(PlayerAvatar instance)
        {
            yield return null;
            Plugin.Shrink(instance.gameObject, Plugin.ActivePlayerScale);
        }

        private static void Postfix(PlayerAvatar __instance) =>
            __instance.StartCoroutine(ShrinkNextFrame(__instance));
    }

    [HarmonyPatch(typeof(PhysGrabObject), "Start")]
    internal static class PhysGrabObjectPatch
    {
        private static void Postfix(PhysGrabObject __instance)
        {
            // Only scale grabbable items, not level geometry (doors, chest lids, etc.)
            var attrs = __instance.GetComponentInParent<ItemAttributes>(includeInactive: true);
            if (attrs != null)
                Plugin.Shrink(attrs.gameObject, Plugin.ActiveItemScale);
        }
    }

    [HarmonyPatch(typeof(ValuableObject), "Start")]
    internal static class ValuableObjectPatch
    {
        private static readonly HashSet<int> _scaled = new HashSet<int>();

        private static void Postfix(ValuableObject __instance)
        {
            int id = __instance.gameObject.GetInstanceID();
            if (!_scaled.Add(id)) return;
            __instance.transform.localScale *= Plugin.ActiveValuableScale;

            if (Plugin.ActiveCartScale >= 1.0f) return;

            // Attach tracker to the PhysGrabObject's GameObject (where the Rigidbody lives)
            // so OnTriggerEnter/Exit fires when the valuable enters the cart's trigger zone.
            var pgo = __instance.GetComponentInChildren<PhysGrabObject>(includeInactive: true)
                   ?? __instance.GetComponentInParent<PhysGrabObject>(includeInactive: true);
            var target = pgo != null ? pgo.gameObject : __instance.gameObject;
            var tracker = target.AddComponent<ValuableCartTracker>();
            tracker.SetValuable(__instance);
        }
    }

    // Applied manually in Plugin.Awake to avoid PatchAll aborting on overload ambiguity
    internal static class VoicePitchPatch
    {
        internal static void Prefix(ref float __0)
        {
            if (!Plugin.ActiveVoiceMod)
                __0 = 1.0f;
        }
    }

    // Block external mods (e.g. ShrinkerGun) from toggling/restoring MiniEepo-managed objects.
    // ScalerCore's "same factor → toggle" would otherwise unshrink players and items when shot.
    [HarmonyPatch(typeof(ScaleManager), "Apply", typeof(GameObject), typeof(ScaleOptions))]
    internal static class ScaleManagerApplyPatch
    {
        static bool Prefix(GameObject target)
        {
            var ctrl = ScaleManager.GetController(target);
            int id = ctrl != null ? ctrl.gameObject.GetInstanceID() : target.GetInstanceID();
            return !Plugin.ManagedObjects.Contains(id);
        }
    }

    internal class ValuableCartTracker : MonoBehaviour
    {
        private ValuableObject? _vo;
        private bool _inCart;
        private Vector3 _preCartScale;

        internal void SetValuable(ValuableObject vo)
        {
            _vo = vo;
            _preCartScale = vo.transform.localScale; // capture once — never overwrite mid-lerp
        }

        private void OnTriggerEnter(Collider other)
        {
            if (_inCart || _vo == null) return;
            if (other.GetComponentInParent<PhysGrabInCart>() == null) return;
            _inCart = true;
            StopAllCoroutines();
            StartCoroutine(ScaleTo(_vo.transform.localScale * Plugin.ActiveCartScale, 0.4f));
        }

        private void OnTriggerExit(Collider other)
        {
            if (!_inCart || _vo == null) return;
            if (other.GetComponentInParent<PhysGrabInCart>() == null) return;
            _inCart = false;
            StopAllCoroutines();
            StartCoroutine(ScaleTo(_preCartScale, 0.3f));
        }

        private IEnumerator ScaleTo(Vector3 target, float duration)
        {
            var start = _vo!.transform.localScale;
            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                _vo.transform.localScale = Vector3.Lerp(start, target, t / duration);
                yield return null;
            }
            _vo.transform.localScale = target;
        }
    }
}
