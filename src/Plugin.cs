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
    // Soft-depend on SolidAim so it loads first when present — our Awake checks for it to skip
    // our redundant held-gun stabilization. Without this, BepInEx may load us first and the
    // detection misses, leaving both mods stabilizing every PGO every FixedUpdate.
    [BepInDependency("Jangnana.SolidAim", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log = null!;
        internal static ConfigEntry<float> PlayerScale = null!;
        internal static ConfigEntry<float> ItemScale = null!;
        internal static ConfigEntry<float> ValuableScale = null!;
        internal static ConfigEntry<float> CartScale = null!;
        internal static ConfigEntry<bool> VoiceMod = null!;
        internal static ConfigEntry<bool> ShrinkInShop = null!;

        // Active values — start from local config, overridden by host sync
        internal static float ActivePlayerScale;
        internal static float ActiveItemScale;
        internal static float ActiveValuableScale;
        internal static float ActiveCartScale;
        internal static bool ActiveVoiceMod;
        internal static bool ActiveShrinkInShop;

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

            ShrinkInShop = Config.Bind("Scaling", "ShrinkInShop", false,
                new ConfigDescription("If true, shrinking applies in the shop level too. Default false — items stay normal size while shopping."));

            ResetToLocalConfig();

            // Keep Active* values in sync when the user changes config at runtime (e.g. via
            // REPOConfig). Without these, the values captured at startup are used forever
            // — the host would also keep pushing the original room-entry values to clients.
            // Scale configs additionally re-push from the host so non-host clients update too.
            VoiceMod.SettingChanged += (_, _) => ActiveVoiceMod = VoiceMod.Value;
            PlayerScale.SettingChanged   += (_, _) => OnHostScaleConfigChanged();
            ItemScale.SettingChanged     += (_, _) => OnHostScaleConfigChanged();
            ValuableScale.SettingChanged += (_, _) => OnHostScaleConfigChanged();
            CartScale.SettingChanged     += (_, _) => OnHostScaleConfigChanged();
            ShrinkInShop.SettingChanged  += (_, _) => OnHostScaleConfigChanged();

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

            // If SolidAim is loaded it already does our held-gun stabilization (we mirror their
            // approach). Skip registering ours so PhysGrabObject.FixedUpdate doesn't pay Harmony
            // invocation overhead per PGO at 50Hz.
            if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("Jangnana.SolidAim"))
                Log.LogInfo("[Stabilize] SolidAim detected — skipping built-in held-gun stabilization");
            else
            {
                var pgoFixed = AccessTools.Method(typeof(PhysGrabObject), "FixedUpdate");
                if (pgoFixed != null)
                    harmony.Patch(pgoFixed, postfix: new HarmonyMethod(typeof(HeldGunStabilizationPatch), nameof(HeldGunStabilizationPatch.Postfix)));
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
            ActiveShrinkInShop  = ShrinkInShop.Value;
        }

        // Re-apply local config values, then re-push to room properties if we're host so non-host
        // clients pick up the change. Non-host changes only affect singleplayer; in a room the
        // host's values win and the next pull will overwrite ours.
        private static void OnHostScaleConfigChanged()
        {
            if (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient) return;
            ResetToLocalConfig();
            SettingsSyncer.Instance?.PushHostSettingsExternal();
        }

        // True when REPO is in an actual gameplay level — not the main menu, not the truck/lobby,
        // and (unless ShrinkInShop is on) not the shop. Uses RunManager's level tracking — REPO
        // loads levels additively so SceneManager.activeScene stays "Level - Lobby Menu" the
        // whole time and isn't reliable here.
        internal static bool IsInGameplay()
        {
            // Also require being in a Photon room — at game startup RunManager.levelCurrent has a
            // non-lobby default value which would otherwise wrongly read as "in gameplay".
            if (!PhotonNetwork.InRoom) return false;
            var rm = RunManager.instance;
            if (rm == null) return false;
            var current = rm.levelCurrent;
            if (current == null) return false;
            if (current == rm.levelLobby || current == rm.levelLobbyMenu) return false;
            if (current == rm.levelShop && !ActiveShrinkInShop) return false;
            return true;
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
        private const string K_SHOP     = "ME_SS"; // ShrinkInShop toggle

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

        // Watcher runs at ~4Hz (every 0.25s) instead of every frame. Items only reset scale on
        // un-pocket, players on damage/revive — none need sub-frame correction. FindObjectsOfType
        // is O(all scene objects) and was the main FPS hit on heavy modpacks.
        private float _watcherTimer;

        // Cache the "Player Visuals" sibling per avatar so the watcher doesn't call
        // Transform.Find every tick — it's the same Transform for the avatar's lifetime.
        private readonly Dictionary<int, Transform?> _visualsCache = new Dictionary<int, Transform?>();

        private void LateUpdate()
        {
            if (!Plugin.IsInGameplay()) return;
            _watcherTimer -= Time.deltaTime;
            if (_watcherTimer > 0f) return;
            _watcherTimer = 0.25f;

            // Player + their visible body sibling. Use GameDirector.PlayerList instead of
            // FindObjectsOfType — it's a maintained roster, not a scene scan.
            float pTarget = Plugin.ActivePlayerScale;
            if (pTarget < 0.99f && GameDirector.instance?.PlayerList != null)
            {
                foreach (var pa in GameDirector.instance.PlayerList)
                {
                    if (pa == null) continue;
                    var paT = pa.transform;
                    if (paT.localScale.x > 0.9f && paT.localScale.x > pTarget + 0.4f)
                        paT.localScale = new Vector3(pTarget, pTarget, pTarget);
                    int id = pa.GetInstanceID();
                    if (!_visualsCache.TryGetValue(id, out var visuals) || visuals == null)
                    {
                        visuals = paT.parent != null ? paT.parent.Find("Player Visuals") : null;
                        _visualsCache[id] = visuals;
                    }
                    if (visuals != null && visuals.localScale.x > 0.9f && visuals.localScale.x > pTarget + 0.4f)
                        visuals.localScale = new Vector3(pTarget, pTarget, pTarget);

                    // Apply CCD to tumble rigidbodies once we observe the avatar at shrunk scale.
                    // Catches the case where PlayerTumble.Start ran before the shrink landed
                    // (e.g. ShrinkerGun mid-game shrink, or the shrink coroutine racing tumble Start).
                    // Internally idempotent + cached, so repeated 4Hz calls are cheap.
                    if (paT.localScale.x < 0.99f)
                        TumbleCcdPatch.ApplyToAvatarTumble(pa);
                }
            }

            // Items removed from this watcher — un-pocket events are now caught directly via
            // a Harmony postfix on ItemEquippable.RPC_CompleteUnequip (see UnequipReshrinkPatch).
            // That re-shrinks only the specific item that just came out, eliminating the
            // periodic GetComponentInParent iteration that was contributing to frame stutter.
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
            if (props.ContainsKey(K_SHOP))     { var v = (bool)props[K_SHOP];      if (Plugin.ActiveShrinkInShop  != v) { Plugin.ActiveShrinkInShop  = v; changed = true; } }
            if (changed)
                Plugin.Log.LogInfo($"[Sync] Pulled host settings — player={Plugin.ActivePlayerScale} item={Plugin.ActiveItemScale} valuable={Plugin.ActiveValuableScale} cart={Plugin.ActiveCartScale} shrinkInShop={Plugin.ActiveShrinkInShop}");
        }

        // Public entry point for runtime config changes (REPOConfig sliders, etc.) — only valid
        // when the host actually has a room to push into.
        internal void PushHostSettingsExternal()
        {
            if (!PhotonNetwork.InRoom || !PhotonNetwork.IsMasterClient) return;
            PushHostSettings();
        }

        // Cache last-pushed values so we don't broadcast a Photon CustomProperties update when
        // SettingChanged fires but values are identical. Some config systems (REPOConfig, etc.)
        // re-emit SettingChanged on autosave even without a real change — without this guard
        // we'd spam the room with broadcasts every few seconds.
        private float _lastPushedPlayer = float.NaN, _lastPushedItem = float.NaN;
        private float _lastPushedValuable = float.NaN, _lastPushedCart = float.NaN;
        private bool? _lastPushedShop;

        private void PushHostSettings()
        {
            if (PhotonNetwork.CurrentRoom == null) return;
            float p = Plugin.PlayerScale.Value, i = Plugin.ItemScale.Value;
            float v = Plugin.ValuableScale.Value, c = Plugin.CartScale.Value;
            bool s = Plugin.ShrinkInShop.Value;
            if (p == _lastPushedPlayer && i == _lastPushedItem &&
                v == _lastPushedValuable && c == _lastPushedCart && s == _lastPushedShop)
            {
                Plugin.ResetToLocalConfig(); // still refresh Active mirrors, but skip broadcast
                return;
            }
            _lastPushedPlayer = p; _lastPushedItem = i;
            _lastPushedValuable = v; _lastPushedCart = c; _lastPushedShop = s;
            var props = new ExitGames.Client.Photon.Hashtable
            {
                [K_PLAYER] = p, [K_ITEM] = i, [K_VALUABLE] = v, [K_CART] = c, [K_SHOP] = s,
            };
            PhotonNetwork.CurrentRoom.SetCustomProperties(props);
            Plugin.ResetToLocalConfig();
            Plugin.Log.LogInfo($"[Sync] Host pushed settings — player={p} item={i} valuable={v} cart={c} shrinkInShop={s}");
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
            // Skip shop level (and lobby) — items stay normal size unless ShrinkInShop is on.
            // Items the player carries from shop into gameplay get caught by RescaleAfterJoin
            // when the level transitions, so we don't lose them.
            if (!Plugin.IsInGameplay()) return;

            GameObject? target = null;
            // ItemAttributes above the PhysGrabObject — original hierarchy; scale from that root.
            var attrsAbove = __instance.GetComponentInParent<ItemAttributes>(includeInactive: true);
            if (attrsAbove != null) target = attrsAbove.gameObject;
            else if (__instance.GetComponentInChildren<ItemAttributes>(includeInactive: true) != null)
                target = __instance.gameObject;
            if (target == null) return;

            // Equippables that aren't guns (melee, grenades, batons, etc.) get direct localScale
            // instead of the full ScalerCore path. ScalerCore registers a controller and tracks
            // these per-frame, which spiked frame time the moment a melee weapon spawned —
            // baguette / sledgehammer / etc. carry extra hitbox colliders and rigidbodies that
            // are expensive to re-bake. Direct scale keeps them visually shrunk without the
            // tracking overhead. Guns stay on the ScalerCore path because the gun-specific
            // stabilization + puller-lift patches assume that controller is registered.
            // ItemEquippable may sit on a child of the ItemAttributes GO, so search the whole
            // subtree. Same for ItemGun — if it's anywhere under target, treat as gun and keep
            // the full ScalerCore path.
            bool hasEquippable = target.GetComponentInChildren<ItemEquippable>(includeInactive: true) != null;
            bool hasGun = target.GetComponentInChildren<ItemGun>(includeInactive: true) != null;
            bool isNonGunEquippable = hasEquippable && !hasGun;

            // ScalerCore's ItemHandler doesn't sync via RPC (only PlayerHandler/CartHandler do) and
            // refuses to scale objects the local client doesn't own. On non-host, fall back to direct
            // localScale multiplication so items still appear small.
            bool useDirectScale = isNonGunEquippable ||
                (PhotonNetwork.InRoom && !PhotonNetwork.IsMasterClient);

            if (useDirectScale)
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
            // Skip shop level — valuables shouldn't shrink while shopping.
            if (!Plugin.IsInGameplay()) return;

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
        // Pre-boxed values so per-frame SetValue calls don't allocate. Reflection's
        // SetValue(object, value-type) boxes the value each call — at multiple players × 60fps
        // that's hundreds of allocs/sec and a GC stutter every few seconds.
        private static readonly object _boxedFalse = false;
        private static readonly object _boxedOne = 1.0f;

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
                _activeField?.SetValue(voiceChat, _boxedFalse);
                _multField?.SetValue(voiceChat, _boxedOne);
            }
            catch (System.Exception e)
            {
                // Avoid crashing the game during host migration / avatar destruction.
                Plugin.Log.LogDebug($"[Voice] OnUpdate postfix swallowed: {e.GetType().Name}");
            }
        }
    }

    // Tumble bodies tunnel through thin geometry when the player is shrunk — small collider +
    // ragdoll spin + fixed-timestep gaps = collider passes through the floor between ticks. Modded
    // maps with thin meshes hit this constantly. Bump every tumble rigidbody's collision detection
    // to ContinuousDynamic so Unity's solver does swept-volume checks instead of point-in-polygon
    // per tick. Only applied to shrunk players' tumbles — full-size vanilla physics is unchanged.
    [HarmonyPatch(typeof(PlayerTumble), "Start")]
    internal static class TumbleCcdPatch
    {
        static void Postfix(PlayerTumble __instance)
        {
            var pa = __instance.playerAvatar;
            if (pa == null) return;
            if (pa.transform.localScale.x > 0.99f) return; // not shrunk — leave vanilla CCD alone

            ApplyCcd(__instance);
        }

        // PlayerTumble instance ids we've already applied CCD to. PlayerTumble lives for the
        // lifetime of the avatar, so once we've walked its rigidbody tree once we don't need to
        // again. Without this guard the SettingsSyncer 4Hz watcher would re-walk every tick.
        private static readonly HashSet<int> _ccdApplied = new HashSet<int>();

        // Re-apply if the player gets shrunk AFTER tumble Start has already run (e.g. ShrinkerGun
        // hits them mid-run, or the shrink coroutine raced PlayerTumble.Start).
        internal static void ApplyToAvatarTumble(PlayerAvatar pa)
        {
            if (pa == null) return;
            var tumble = pa.GetComponentInChildren<PlayerTumble>(includeInactive: true);
            if (tumble != null) ApplyCcd(tumble);
        }

        private static void ApplyCcd(PlayerTumble tumble)
        {
            if (!_ccdApplied.Add(tumble.GetInstanceID())) return;
            foreach (var rb in tumble.GetComponentsInChildren<Rigidbody>(includeInactive: true))
            {
                // ContinuousDynamic catches collisions vs both static and dynamic CCD-enabled
                // colliders. Continuous (without Dynamic) only sweeps vs static — fine for floors,
                // misses moving platforms.
                if (rb.collisionDetectionMode != CollisionDetectionMode.ContinuousDynamic)
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
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

    // Re-shrink an item the moment it's un-pocketed from inventory. This is the only place an
    // already-shrunk item resets its localScale back to 1.0, so hooking the un-pocket RPC lets
    // us catch it without polling every item every frame. Wait one frame for the un-equip
    // animation to settle before snapping the scale.
    [HarmonyPatch(typeof(ItemEquippable), "RPC_CompleteUnequip")]
    internal static class UnequipReshrinkPatch
    {
        static void Postfix(ItemEquippable __instance)
        {
            if (Plugin.ActiveItemScale >= 0.99f) return;
            __instance.StartCoroutine(Reshrink(__instance));
        }

        private static IEnumerator Reshrink(ItemEquippable equip)
        {
            yield return null;
            if (equip == null) yield break;
            var attrs = equip.GetComponentInParent<ItemAttributes>(includeInactive: true);
            GameObject target = attrs != null ? attrs.gameObject : equip.gameObject;
            float t = Plugin.ActiveItemScale;
            if (target.transform.localScale.x > 0.9f && target.transform.localScale.x > t + 0.4f)
                target.transform.localScale = new Vector3(t, t, t);
        }
    }

    // SolidAim-style stabilization for held guns when the local player is shrunk. Without this,
    // shotguns/heavy guns droop because the grab spring isn't strong enough vs gravity torque
    // for a 40%-scale player whose grab strength is reduced. Applies override-mass + override-
    // grab-strength + rotation slerp toward camera every FixedUpdate (overrides are timed at
    // 0.1s so they need re-application). Mirrors jangnana/SolidAim's ApplyAimStabilization.
    //
    // No [HarmonyPatch] attribute — registered manually in Plugin.Awake only when SolidAim is
    // absent. Otherwise PhysGrabObject.FixedUpdate fires per PGO at 50Hz and even an early-return
    // postfix has Harmony invocation overhead worth avoiding on heavy scenes.
    internal static class HeldGunStabilizationPatch
    {
        // Cache ItemGun presence per PGO. GetComponent allocates and is the most expensive check
        // here; ItemGun never appears/disappears on a live PGO, so look it up once and reuse.
        // Without this cache, every melee weapon / valuable / prop pays a GetComponent on every
        // FixedUpdate (50Hz) — heavy levels and segmented melee items tanked frame time.
        private static readonly Dictionary<int, bool> _isGun = new Dictionary<int, bool>();

        internal static void Postfix(PhysGrabObject __instance)
        {
            // Cheapest/most-discriminating check first: most PGOs aren't guns, so reject them
            // before touching SemiFunc or any list scans.
            int id = __instance.GetInstanceID();
            if (!_isGun.TryGetValue(id, out bool isGun))
            {
                isGun = __instance.GetComponent<ItemGun>() != null;
                _isGun[id] = isGun;
            }
            if (!isGun) return;

            var local = SemiFunc.PlayerAvatarLocal();
            if (local == null) return;
            float scale = local.transform.localScale.x;
            if (scale > 0.99f) return; // not shrunk — let vanilla physics run
            if (!__instance.playerGrabbing.Contains(local.physGrabber)) return;

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

    // ScalerCore's GrabVerticalPositionScalePatch handles puller height correctly for valuables,
    // but guns still droop because the baked-in `- camera.up * 0.3` offset on forceGrabPoint items
    // pulls them below the shrunken eye line. Re-lift only for guns so valuables keep ScalerCore's
    // natural feel.
    [HarmonyPatch(typeof(PhysGrabber), "StartGrabbingPhysObject")]
    internal static class GrabPullerOffsetFixForGuns
    {
        static void Postfix(PhysGrabber __instance, PhysGrabObject ___grabbedPhysGrabObject)
        {
            var pa = __instance.playerAvatar;
            if (pa == null) return;
            if (pa.transform.localScale.x > 0.99f) return; // not shrunk
            var grabbed = ___grabbedPhysGrabObject;
            if (grabbed == null || grabbed.GetComponent<ItemGun>() == null) return;
            var puller = __instance.physGrabPointPuller;
            if (puller == null) return;
            var cam = Camera.main;
            if (cam == null) return;
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
