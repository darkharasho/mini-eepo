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
using UnityEngine.SceneManagement;

namespace MiniEepo
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
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

            // Attach SettingsSyncer to our own plugin GameObject (BepInEx keeps it alive across
            // scenes). A fresh GameObject didn't reliably get its Start callback fired.
            gameObject.AddComponent<SettingsSyncer>();

            var harmony = new Harmony("darkharasho.MiniEepo");
            harmony.PatchAll();

            // Voice mod: patch ScalerCore's AudioPitchHelper.ApplyPitch — return false from prefix
            // when VoiceMod is off, suppressing the pitch change at its source. More reliable than
            // racing to reset AudioSource.pitch after the fact (which only catches looping sounds,
            // not one-shots).
            System.Type? pitchHelper = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "ScalerCore") continue;
                foreach (var t in asm.GetTypes())
                    if (t.Name == "AudioPitchHelper") { pitchHelper = t; break; }
                if (pitchHelper != null) break;
            }
            if (pitchHelper != null)
            {
                var applyPitch = AccessTools.Method(pitchHelper, "ApplyPitch");
                if (applyPitch != null)
                {
                    harmony.Patch(applyPitch, prefix: new HarmonyMethod(typeof(VoicePitchPatch), nameof(VoicePitchPatch.Prefix)));
                    Log.LogInfo($"[Voice] Patched {pitchHelper.FullName}.ApplyPitch");
                }
                else
                    Log.LogWarning($"[Voice] {pitchHelper.FullName}.ApplyPitch not found");
            }
            else
                Log.LogWarning("[Voice] ScalerCore.AudioPitchHelper not found — voice mod toggle disabled");

            // Revive resets localScale — re-shrink after recovery. Tumble/Hurt patches were tried
            // but ForceShrink mid-tumble destroys ScalerCore's controller and breaks the tumble
            // state, leaving the player at full size. The continuous LateUpdate watcher handles
            // damage/tumble scale resets without that destructive side effect.
            foreach (var name in new[] { "ReviveRPC", "Revive" })
            {
                var m = AccessTools.Method(typeof(PlayerAvatar), name);
                if (m == null) continue;
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(PlayerRevivePatch), nameof(PlayerRevivePatch.Postfix)));
                Log.LogInfo($"[Revive] Patched {name}");
            }

            Log.LogInfo($"MiniEepo v{PluginInfo.PLUGIN_VERSION} loaded — everything is tiny now.");
        }

        internal static void ResetToLocalConfig()
        {
            ActivePlayerScale   = PlayerScale.Value;
            ActiveItemScale     = ItemScale.Value;
            ActiveValuableScale = ValuableScale.Value;
            ActiveCartScale     = CartScale.Value;
            ActiveVoiceMod      = VoiceMod.Value;
        }

        // True when REPO is in an actual gameplay level — not the main menu, not the truck/lobby.
        // Uses RunManager's level tracking (REPO loads levels additively, so SceneManager.activeScene
        // stays "Level - Lobby Menu" the whole time and isn't reliable here).
        internal static bool IsInGameplay()
        {
            // Also require being in a Photon room — at game startup RunManager.levelCurrent has a
            // non-lobby default value which would otherwise wrongly read as "in gameplay".
            if (!PhotonNetwork.InRoom) return false;
            var rm = RunManager.instance;
            if (rm == null) return false;
            var current = rm.levelCurrent;
            if (current == null) return false;
            return current != rm.levelLobby && current != rm.levelLobbyMenu;
        }

        internal static readonly HashSet<int> ManagedObjects = new HashSet<int>();
        // Re-entrancy guard: ScalerCore attaches its controller to the same GameObject as the target,
        // so ScaleManagerApplyPatch would otherwise block our own first-time scales. Set true while
        // we are inside ApplyIfNotScaled so the patch lets our calls through.
        internal static bool IsApplying;

        internal static void Shrink(GameObject go, float factor)
        {
            ManagedObjects.Add(go.GetInstanceID());
            var opts = ScaleOptions.Default; // ScaleOptions is a struct; this is a safe value copy
            opts.Factor = factor;
            IsApplying = true;
            try { ScaleManager.ApplyIfNotScaled(go, opts); }
            finally { IsApplying = false; }
        }

    }

    // Plain MonoBehaviour with polling — abandoned MonoBehaviourPunCallbacks because OnJoinedRoom /
    // OnRoomPropertiesUpdate weren't firing for us in any tested setup, despite ScalerCore's RPC
    // dispatch working. Polling sidesteps the entire callback-registration mystery.
    internal class SettingsSyncer : MonoBehaviour
    {
        // VoiceMod intentionally not synced — it controls what the local listener hears, so each
        // client should be able to opt out (or in) regardless of the host's setting.
        private const string K_PLAYER   = "ME_PS";
        private const string K_ITEM     = "ME_IS";
        private const string K_VALUABLE = "ME_VS";
        private const string K_CART     = "ME_CS";

        // Static singleton — FindObjectOfType doesn't reliably find us when attached to the BepInEx
        // plugin GameObject (which lives outside the normal scene hierarchy).
        internal static SettingsSyncer? Instance;

        private bool _wasInRoom;
        private bool _wasMaster;
        private float _pullPollDelay;

        private void Awake() => Instance = this;
        private void Start() => Plugin.Log.LogInfo("[Sync] SettingsSyncer ready (polling mode)");

        // Called from RunManagerUpdateLevelPatch when REPO transitions to a gameplay level.
        internal void TriggerRescale() => StartCoroutine(RescaleAfterJoin());

        private void Update()
        {
            // Detect room-state transitions and pull/push settings accordingly. Cheap — just two
            // static bool reads against cached state.
            bool inRoom = PhotonNetwork.InRoom;
            bool master = inRoom && PhotonNetwork.IsMasterClient;

            if (inRoom && !_wasInRoom)
            {
                if (master) PushHostSettings();
                else        PullHostSettings();
            }
            else if (!inRoom && _wasInRoom)
            {
                Plugin.ResetToLocalConfig();
                Plugin.Log.LogInfo("[Sync] Left room — reset to local config");
            }
            else if (inRoom && master && !_wasMaster)
            {
                // We just became master client (e.g. host migration) — push our settings.
                PushHostSettings();
            }
            else if (inRoom && !master)
            {
                // Cheap re-poll once a second in case host pushed after we joined.
                _pullPollDelay -= Time.unscaledDeltaTime;
                if (_pullPollDelay <= 0f)
                {
                    _pullPollDelay = 1f;
                    PullHostSettings();
                }
            }

            _wasInRoom = inRoom;
            _wasMaster = master;
        }

        private void LateUpdate()
        {
            float target = Plugin.ActivePlayerScale;
            if (target >= 0.99f || !Plugin.IsInGameplay()) return;
            foreach (var pa in Object.FindObjectsOfType<PlayerAvatar>())
            {
                var s = pa.transform.localScale.x;
                if (s > 0.9f && s > target + 0.4f)
                {
                    // Direct set only. ScaleManager.Apply with same factor would TOGGLE off
                    // (ScalerCore treats same-factor-on-scaled-object as ShrinkerGun toggle).
                    pa.transform.localScale = new Vector3(target, target, target);
                }
            }
        }

private void PullHostSettings()
        {
            var props = PhotonNetwork.CurrentRoom?.CustomProperties;
            if (props == null) return;
            bool changed = false;
            if (props.ContainsKey(K_PLAYER))   { var v = (float)props[K_PLAYER];   if (Plugin.ActivePlayerScale   != v) { Plugin.ActivePlayerScale   = v; changed = true; } }
            if (props.ContainsKey(K_ITEM))     { var v = (float)props[K_ITEM];     if (Plugin.ActiveItemScale     != v) { Plugin.ActiveItemScale     = v; changed = true; } }
            if (props.ContainsKey(K_VALUABLE)) { var v = (float)props[K_VALUABLE]; if (Plugin.ActiveValuableScale != v) { Plugin.ActiveValuableScale = v; changed = true; } }
            if (props.ContainsKey(K_CART))     { var v = (float)props[K_CART];     if (Plugin.ActiveCartScale     != v) { Plugin.ActiveCartScale     = v; changed = true; } }
            if (changed)
                Plugin.Log.LogInfo($"[Sync] Pulled host settings — player={Plugin.ActivePlayerScale} item={Plugin.ActiveItemScale} valuable={Plugin.ActiveValuableScale} cart={Plugin.ActiveCartScale}");
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
            };
            PhotonNetwork.CurrentRoom.SetCustomProperties(props);
            Plugin.ResetToLocalConfig();
            Plugin.Log.LogInfo($"[Sync] Host pushed settings — player={Plugin.PlayerScale.Value} item={Plugin.ItemScale.Value} valuable={Plugin.ValuableScale.Value} cart={Plugin.CartScale.Value}");
        }

        private IEnumerator RescaleAfterJoin()
        {
            yield return new WaitForSeconds(3f);
            Plugin.Log.LogInfo("[Sync] Running post-join rescale pass");
            foreach (var pgo in FindObjectsOfType<PhysGrabObject>())
            {
                var attrs = pgo.GetComponentInParent<ItemAttributes>(includeInactive: true)
                         ?? pgo.GetComponentInChildren<ItemAttributes>(includeInactive: true);
                if (attrs != null)
                    Plugin.Shrink(attrs.gameObject, Plugin.ActiveItemScale);
            }
            foreach (var pa in FindObjectsOfType<PlayerAvatar>())
                Plugin.Shrink(pa.gameObject, Plugin.ActivePlayerScale);
        }
    }

    [HarmonyPatch(typeof(PlayerAvatar), "Start")]
    internal static class PlayerAvatarPatch
    {
        // Track non-host direct-scale players by instance id so we don't double-apply.
        internal static readonly HashSet<int> DirectScaledPlayers = new HashSet<int>();

        private static IEnumerator ShrinkNextFrame(PlayerAvatar instance)
        {
            yield return null;
            if (!Plugin.IsInGameplay()) yield break;

            var pv = instance.GetComponent<Photon.Pun.PhotonView>();
            bool useShrink = pv == null || pv.IsMine || PhotonNetwork.IsMasterClient;
            if (useShrink)
            {
                // Owner or master client — use ScalerCore for full effect (animation, mass, RPC).
                Plugin.Shrink(instance.gameObject, Plugin.ActivePlayerScale);
            }
            else
            {
                // Non-owner on non-master: ScalerCore refuses to apply non-owned objects, and the
                // host's RPC may have raced past our handler-registration. Bypass ScalerCore and
                // direct-multiply the transform — same pattern as items/valuables (which work).
                if (DirectScaledPlayers.Add(instance.gameObject.GetInstanceID()))
                    instance.transform.localScale *= Plugin.ActivePlayerScale;
            }
        }

        private static void Postfix(PlayerAvatar __instance) =>
            __instance.StartCoroutine(ShrinkNextFrame(__instance));
    }

    // Fires whenever REPO transitions to a new level — including the lobby→gameplay jump where
    // SceneManager.sceneLoaded doesn't fire (REPO loads levels additively). This is our reliable
    // hook for "we just entered gameplay; shrink everything."
    [HarmonyPatch(typeof(RunManager), "UpdateLevel")]
    internal static class RunManagerUpdateLevelPatch
    {
        static void Postfix(RunManager __instance)
        {
            if (!Plugin.IsInGameplay()) return;
            SettingsSyncer.Instance?.TriggerRescale();
        }
    }

    [HarmonyPatch(typeof(PhysGrabObject), "Start")]
    internal static class PhysGrabObjectPatch
    {
        // Track non-host direct-scale items by instance id, so RescaleAfterJoin doesn't double-apply.
        internal static readonly HashSet<int> DirectScaledItems = new HashSet<int>();

        private static void Postfix(PhysGrabObject __instance)
        {
            GameObject? target = null;
            // ItemAttributes above the PhysGrabObject — original hierarchy; scale from that root.
            var attrsAbove = __instance.GetComponentInParent<ItemAttributes>(includeInactive: true);
            if (attrsAbove != null) target = attrsAbove.gameObject;
            else if (__instance.GetComponentInChildren<ItemAttributes>(includeInactive: true) != null)
                target = __instance.gameObject;
            if (target == null) return;

            // ScalerCore's ItemHandler doesn't sync via RPC (only PlayerHandler/CartHandler do) and
            // refuses to scale objects the local client doesn't own. On non-host, fall back to direct
            // localScale multiplication so items still appear small.
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient)
            {
                if (DirectScaledItems.Add(target.GetInstanceID()))
                    target.transform.localScale *= Plugin.ActiveItemScale;
                return;
            }
            Plugin.Shrink(target, Plugin.ActiveItemScale);
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


    // Applied manually in Plugin.Awake. Returning false skips ScalerCore's ApplyPitch entirely
    // when VoiceMod is off — pitch is never modified, so audio plays at normal pitch.
    internal static class VoicePitchPatch
    {
        public static bool Prefix() => Plugin.ActiveVoiceMod;
    }

    // Applied manually in Plugin.Awake for revive method names — re-shrinks after the
    // animation that resets localScale. Direct-sets transform.localScale (no ForceShrink, which
    // would DestroyImmediate ScalerCore's controller and break its tracking mid-animation).
    internal static class PlayerRevivePatch
    {
        internal static void Postfix(PlayerAvatar __instance)
        {
            float t = Plugin.ActivePlayerScale;
            if (t < 0.99f) __instance.transform.localScale = new Vector3(t, t, t);
        }
    }

    // Block external mods (e.g. ShrinkerGun) from toggling/restoring MiniEepo-managed objects.
    // Always allow our own calls (gated by Plugin.IsApplying) so ScalerCore can register and apply
    // the initial scale — ScalerCore attaches its controller to the same GameObject as the target,
    // which would otherwise make this patch self-block.
    [HarmonyPatch(typeof(ScaleManager), "Apply", typeof(GameObject), typeof(ScaleOptions))]
    internal static class ScaleManagerApplyPatch
    {
        static bool Prefix(GameObject target)
        {
            if (Plugin.IsApplying) return true;
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
