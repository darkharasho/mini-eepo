using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using ScalerCore;
using UnityEngine;

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

            // Keep ActiveVoiceMod in sync when the user changes the toggle at runtime via
            // REPOConfig. Without this, the patches keep using the startup value forever.
            VoiceMod.SettingChanged += (_, _) => ActiveVoiceMod = VoiceMod.Value;

            // Attach SettingsSyncer to our own plugin GameObject (BepInEx keeps it alive across
            // scenes). A fresh GameObject didn't reliably get its Start callback fired.
            gameObject.AddComponent<SettingsSyncer>();

            var harmony = new Harmony("darkharasho.MiniEepo");
            harmony.PatchAll();

            // Voice mod has two separate pitch mechanisms in ScalerCore + REPO:
            //   (1) AudioPitchHelper.ApplyPitch — pitches Sound objects (grunts, voice lines)
            //   (2) PlayerVoiceChat.OverridePitch — Photon Voice / actual microphone pitch
            // Both must be suppressed when VoiceMod is off. The same prefix works for both.
            System.Type? pitchHelper = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "ScalerCore") continue;
                foreach (var t in asm.GetTypes())
                    if (t.Name == "AudioPitchHelper") { pitchHelper = t; break; }
                if (pitchHelper != null) break;
            }
            var applyPitch = pitchHelper != null ? AccessTools.Method(pitchHelper, "ApplyPitch") : null;
            if (applyPitch != null)
            {
                harmony.Patch(applyPitch, prefix: new HarmonyMethod(typeof(VoicePitchPatch), nameof(VoicePitchPatch.Prefix)));
                Log.LogInfo($"[Voice] Patched {pitchHelper!.FullName}.ApplyPitch (Sound pitch)");
            }
            else
                Log.LogWarning("[Voice] AudioPitchHelper.ApplyPitch not found");

            var voiceChatType = AccessTools.TypeByName("PlayerVoiceChat");
            var overridePitch = voiceChatType != null ? AccessTools.Method(voiceChatType, "OverridePitch") : null;
            if (overridePitch != null)
            {
                harmony.Patch(overridePitch, prefix: new HarmonyMethod(typeof(VoicePitchPatch), nameof(VoicePitchPatch.Prefix)));
                Log.LogInfo("[Voice] Patched PlayerVoiceChat.OverridePitch (Photon Voice pitch)");
            }
            else
                Log.LogWarning("[Voice] PlayerVoiceChat.OverridePitch not found");

            // ScalerCore's PlayerHandler.OnUpdate writes voiceChat.overridePitchMultiplierTarget /
            // overridePitchTimer / overridePitchIsActive AS DIRECT FIELD ASSIGNMENTS every frame —
            // bypasses OverridePitch entirely. Postfix-reset those fields when VoiceMod is off.
            System.Type? playerHandler = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "ScalerCore") continue;
                foreach (var t in asm.GetTypes())
                    if (t.Name == "PlayerHandler") { playerHandler = t; break; }
                if (playerHandler != null) break;
            }
            var phOnUpdate = playerHandler != null ? AccessTools.Method(playerHandler, "OnUpdate") : null;
            if (phOnUpdate != null)
            {
                harmony.Patch(phOnUpdate, postfix: new HarmonyMethod(typeof(VoicePitchPatch), nameof(VoicePitchPatch.PlayerHandlerOnUpdatePostfix)));
                Log.LogInfo($"[Voice] Patched {playerHandler!.FullName}.OnUpdate (per-frame voice pitch fields)");
            }
            else
                Log.LogWarning("[Voice] PlayerHandler.OnUpdate not found");

            // Stop ScalerCore from un-shrinking players when they take damage (PlayerBonkPatch
            // postfix calls controller.RequestBonkExpand on hp drop). Patching the patch's Postfix
            // to return false-equivalent skips the bonk-expand entirely, without touching
            // ScaleOptions (which affected grab feel).
            System.Type? bonkPatch = null;
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.GetName().Name != "ScalerCore") continue;
                foreach (var t in asm.GetTypes())
                    if (t.Name == "PlayerBonkPatch") { bonkPatch = t; break; }
                if (bonkPatch != null) break;
            }
            var bonkPostfix = bonkPatch != null ? AccessTools.Method(bonkPatch, "Postfix") : null;
            if (bonkPostfix != null)
            {
                harmony.Patch(bonkPostfix, prefix: new HarmonyMethod(typeof(BonkBlocker), nameof(BonkBlocker.Prefix)));
                Log.LogInfo($"[BonkImmune] Disabled ScalerCore's damage-bonk-expand (damage no longer un-shrinks)");
            }
            else
                Log.LogWarning("[BonkImmune] PlayerBonkPatch.Postfix not found — damage may un-shrink");

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
            var opts = ScaleOptions.Default;
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
            if (!Plugin.IsInGameplay()) return;

            // Player + their visible body sibling.
            float pTarget = Plugin.ActivePlayerScale;
            if (pTarget < 0.99f)
            {
                foreach (var pa in Object.FindObjectsOfType<PlayerAvatar>())
                {
                    if (pa == null) continue;
                    var paT = pa.transform;
                    if (paT.localScale.x > 0.9f && paT.localScale.x > pTarget + 0.4f)
                        paT.localScale = new Vector3(pTarget, pTarget, pTarget);
                    var visuals = paT.parent != null ? paT.parent.Find("Player Visuals") : null;
                    if (visuals != null && visuals.localScale.x > 0.9f && visuals.localScale.x > pTarget + 0.4f)
                        visuals.localScale = new Vector3(pTarget, pTarget, pTarget);
                }
            }

            // Items — un-pocketing from inventory resets scale back to 1.0 but ScalerCore's
            // controller still thinks IsScaled=true. Direct-set the scale instead of going
            // through Restore+Shrink (which animates 0.4→1.0→0.4 and produces a visible flash
            // when guns are pulled from inventory). ScalerCore's per-frame OnLateUpdate also
            // forces _t.localScale = _target while IsScaled, so this is consistent with that.
            float iTarget = Plugin.ActiveItemScale;
            if (iTarget < 0.99f)
            {
                foreach (var equip in Object.FindObjectsOfType<ItemEquippable>())
                {
                    if (equip == null) continue;
                    var attrs = equip.GetComponentInParent<ItemAttributes>(includeInactive: true);
                    GameObject target = attrs != null ? attrs.gameObject : equip.gameObject;
                    var s = target.transform.localScale.x;
                    if (s > 0.9f && s > iTarget + 0.4f)
                        target.transform.localScale = new Vector3(iTarget, iTarget, iTarget);
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

            // Only the host calls Plugin.Shrink for player avatars — matches ShrinkerGun's pattern
            // (only the host calls ScaleManager.Apply). Non-hosts get scaled via the host's RPC_Shrink.
            //
            // If non-hosts ALSO call Plugin.Shrink locally for their own avatar, ScalerCore's
            // ApplyLocalPlayerShrinkEffects runs twice on them: once from local DispatchShrink, then
            // again when host's RPC_Shrink arrives. The second pass re-caches the already-scaled
            // VisionTarget / camera offsets as "original" and scales them again, collapsing held
            // item position to ~16% of true eye height — the droop bug.
            if (PhotonNetwork.IsMasterClient || !PhotonNetwork.InRoom)
            {
                // For the LOCAL player avatar (host's own, or singleplayer), wait until the
                // singletons ApplyLocalPlayerShrinkEffects depends on are bound. Otherwise the
                // VisionTarget / camera / grab-distance scaling silently no-ops, leaving held
                // items dangling at full-size eye height.
                var pv = instance.GetComponent<PhotonView>();
                bool isLocal = pv == null || pv.IsMine;
                if (isLocal)
                {
                    float w = 0f;
                    while (w < 5f &&
                           (PhysGrabber.instance == null ||
                            instance.GetComponent<PlayerVisionTarget>() == null ||
                            CameraPosition.instance == null))
                    {
                        w += Time.deltaTime;
                        yield return null;
                    }
                    if (!Plugin.IsInGameplay()) yield break;
                }
                Plugin.Shrink(instance.gameObject, Plugin.ActivePlayerScale);
                yield break;
            }

            // Non-host: wait briefly for host's RPC_Shrink to land. If it doesn't (e.g. host is
            // running without MiniEepo), fall back to direct localScale so the player still appears
            // small visually. No ScalerCore controller in this branch — purely cosmetic.
            float waited = 0f;
            while (waited < 5f && instance.transform.localScale.x > 0.99f)
            {
                waited += Time.deltaTime;
                yield return null;
            }
            if (instance.transform.localScale.x > 0.99f &&
                DirectScaledPlayers.Add(instance.gameObject.GetInstanceID()))
            {
                instance.transform.localScale *= Plugin.ActivePlayerScale;
                var parent = instance.transform.parent;
                if (parent != null)
                {
                    var visuals = parent.Find("Player Visuals");
                    if (visuals != null && visuals.localScale.x > 0.99f)
                        visuals.localScale *= Plugin.ActivePlayerScale;
                }
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

            // Always attach the tracker — the gate on ActiveCartScale is in OnTriggerEnter so it
            // works even when valuables spawn before the host's settings sync arrives.
            var pgo = __instance.GetComponentInChildren<PhysGrabObject>(includeInactive: true)
                   ?? __instance.GetComponentInParent<PhysGrabObject>(includeInactive: true);
            var target = pgo != null ? pgo.gameObject : __instance.gameObject;
            var tracker = target.AddComponent<ValuableCartTracker>();
            tracker.SetValuable(__instance);
        }
    }


    // Applied as Prefix on ScalerCore.Patches.PlayerBonkPatch.Postfix — returning false skips
    // RequestBonkExpand entirely, so taking damage no longer un-shrinks the player.
    internal static class BonkBlocker
    {
        public static bool Prefix() => false;
    }

    internal static class VoicePitchPatch
    {
        public static bool Prefix() => Plugin.ActiveVoiceMod;

        // Reflection cache. HandlerState is INTERNAL on ScaleController (a field, not a property),
        // so we have to use AccessTools / non-public flags to find it.
        private const System.Reflection.BindingFlags BF =
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Instance;
        private static System.Reflection.FieldInfo? _handlerStateField;
        private static System.Reflection.FieldInfo? _avatarField;
        private static System.Reflection.FieldInfo? _voiceChatField;
        private static System.Reflection.FieldInfo? _activeField;
        private static System.Reflection.FieldInfo? _multField;

        public static void PlayerHandlerOnUpdatePostfix(object ctrl)
        {
            if (Plugin.ActiveVoiceMod) return;
            if (ctrl == null) return;
            try
            {
                _handlerStateField ??= ctrl.GetType().GetField("HandlerState", BF);
                var state = _handlerStateField?.GetValue(ctrl);
                if (state == null) return;
                _avatarField ??= state.GetType().GetField("PlayerAvatar", BF);
                var avatar = _avatarField?.GetValue(state);
                // Unity null-check — UnityEngine.Object overrides == to detect destroyed objects.
                if (avatar is UnityEngine.Object uAvatar && uAvatar == null) return;
                if (avatar == null) return;
                _voiceChatField ??= avatar.GetType().GetField("voiceChat", BF);
                var voiceChat = _voiceChatField?.GetValue(avatar);
                if (voiceChat is UnityEngine.Object uVc && uVc == null) return;
                if (voiceChat == null) return;
                var vt = voiceChat.GetType();
                _activeField ??= vt.GetField("overridePitchIsActive", BF);
                _multField ??= vt.GetField("overridePitchMultiplierTarget", BF);
                _activeField?.SetValue(voiceChat, false);
                _multField?.SetValue(voiceChat, 1.0f);
            }
            catch (System.Exception e)
            {
                // Avoid crashing the game during host migration / avatar destruction.
                Plugin.Log.LogDebug($"[Voice] OnUpdate postfix swallowed: {e.GetType().Name}");
            }
        }
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

    // SolidAim-style stabilization for held guns when the local player is shrunk. Without this,
    // shotguns/heavy guns droop because the grab spring isn't strong enough vs gravity torque
    // for a 40%-scale player whose grab strength is reduced. Applies override-mass + override-
    // grab-strength + rotation slerp toward camera every FixedUpdate (overrides are timed at
    // 0.1s so they need re-application). Mirrors jangnana/SolidAim's ApplyAimStabilization.
    [HarmonyPatch(typeof(PhysGrabObject), "FixedUpdate")]
    internal static class HeldGunStabilizationPatch
    {
        static void Postfix(PhysGrabObject __instance)
        {
            var local = SemiFunc.PlayerAvatarLocal();
            if (local == null) return;
            float scale = local.transform.localScale.x;
            if (scale > 0.99f) return; // not shrunk — let vanilla physics run
            if (!__instance.playerGrabbing.Contains(local.physGrabber)) return;

            var gun = __instance.GetComponent<ItemGun>();
            if (gun == null) return; // only stabilize guns; other items keep vanilla feel

            __instance.OverrideMass(0.25f, 0.1f);
            __instance.OverrideGrabStrength(2f, 0.1f);
            __instance.OverrideTorqueStrength(5f, 0.1f);
            __instance.OverrideDrag(2f, 0.1f);
            if (__instance.rb != null) __instance.rb.angularDrag = 29f;

            var cam = Camera.main;
            if (cam != null && __instance.rb != null)
                __instance.rb.rotation = Quaternion.Slerp(
                    __instance.rb.rotation, cam.transform.rotation, Time.fixedDeltaTime * 10f);
        }
    }

    // PhysGrabber.StartGrabbingPhysObject hardcodes a `- camera.up * 0.3` offset on the puller
    // position when grabbing forceGrabPoint items (guns, melee). That 0.3 is in world units and
    // doesn't scale — for a shrunk player (~0.6 eye height) it puts the puller near the floor,
    // so guns drop out of view. Add back most of that offset proportionally to player scale so
    // the puller stays in front of the shrunken eye line.
    [HarmonyPatch(typeof(PhysGrabber), "StartGrabbingPhysObject")]
    internal static class GrabPullerOffsetFix
    {
        private static System.Reflection.FieldInfo? _camField;

        static void Postfix(PhysGrabber __instance)
        {
            var pa = __instance.playerAvatar;
            if (pa == null) return;
            float scale = pa.transform.localScale.x;
            if (scale > 0.99f) return; // not shrunk
            var puller = __instance.physGrabPointPuller;
            if (puller == null) return;
            _camField ??= AccessTools.Field(typeof(PhysGrabber), "playerCamera");
            var cam = _camField?.GetValue(__instance) as Camera;
            if (cam == null) return;
            // Cancel the baked-in -0.3 world-unit down-offset entirely. For shrunk players the
            // camera is still at near-full-size eye height (capsule unchanged), so any constant
            // down-offset puts the gun out of FOV. Lift by the full 0.3.
            Vector3 lift = cam.transform.up * 0.3f;
            puller.position += lift;
            __instance.physGrabPointPlane.position += lift;
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
            if (Plugin.ActiveCartScale >= 1.0f) return; // no-op when host's CartScale is 1.0
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
