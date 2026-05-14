using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToasterSticksOnMap;

public static class SticksOnMap
{
    private static Dictionary<Stick, VisualElement> stickVisualElementMap = new Dictionary<Stick, VisualElement>();

    // Scale ranges remain hardcoded - they're shape constants, not user knobs.
    private const float StickMaxScale = 1.0f;
    private const float PuckMaxScale = 1.0f;

    // Tunables — read from config. See ModSettings.cs.
    private static float StickMaxHeight => Plugin.modSettings.stickMaxHeight;
    private static float StickMinScale => Plugin.modSettings.stickMinScale;
    private static float PuckMaxHeight => Plugin.modSettings.puckMaxHeight;
    private static float PuckMinScale => Plugin.modSettings.puckMinScale;

    private static bool EnableSideViews => Plugin.modSettings.enableSideViews;
    private static bool EnablePuckHeightOpacity => Plugin.modSettings.enablePuckHeightOpacity;
    private static bool EnableFogOfWar => Plugin.modSettings.enableFogOfWar;

    private static float FogFovDegrees => Plugin.modSettings.fogFovDegrees;
    private static float FogHiddenOpacity => Plugin.modSettings.fogHiddenOpacity;

    // Side view panel references
    private static VisualElement sideViewLeft;   // long-ways: shares vertical axis (Z) with minimap, horizontal = height
    private static VisualElement sideViewBottom;  // short-ways: shares horizontal axis (X) with minimap, vertical = height
    private static bool sideViewsInitialized = false;

    // Side view dot tracking - keyed by PlayerBody (like the vanilla minimap) and Puck
    private static Dictionary<PlayerBody, VisualElement> playerSideLeftMap = new Dictionary<PlayerBody, VisualElement>();
    private static Dictionary<PlayerBody, VisualElement> playerSideBottomMap = new Dictionary<PlayerBody, VisualElement>();
    private static Dictionary<Puck, VisualElement> puckSideLeftMap = new Dictionary<Puck, VisualElement>();
    private static Dictionary<Puck, VisualElement> puckSideBottomMap = new Dictionary<Puck, VisualElement>();

    // Side view dimensions
    private const float SideViewThickness = 30f;
    private const float SideViewGap = 4f;

    // ─── Reflection fields ────────────────────────────────────────────

    static readonly FieldInfo _bladeHandleField = typeof(Stick)
        .GetField("bladeHandle", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly FieldInfo _bladeAngleStepField = typeof(Stick)
        .GetField("bladeAngleStep", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly FieldInfo _contentField = typeof(UIMinimap)
        .GetField("content", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly FieldInfo _minimapField = typeof(UIMinimap)
        .GetField("minimap", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly FieldInfo _puckVisualElementMapField = typeof(UIMinimap)
        .GetField("puckVisualElementMap", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly FieldInfo _playerBodyVisualElementMapField = typeof(UIMinimap)
        .GetField("playerBodyVisualElementMap", BindingFlags.Instance | BindingFlags.NonPublic);

    static readonly MethodInfo _worldToMinimapMethod = typeof(UIMinimap)
        .GetMethod("WorldPositionToMinimapPosition", BindingFlags.NonPublic | BindingFlags.Instance);

    // ─── Helpers ──────────────────────────────────────────────────────

    private static float GetHeightFactor(float worldY, float maxHeight)
    {
        float height = Mathf.Abs(worldY);
        float clamped = Mathf.Clamp(height, 0f, maxHeight);
        return clamped / maxHeight;
    }

    private static float GetScaleFromHeight(float worldY, float maxHeight, float maxScale, float minScale)
    {
        return Mathf.Lerp(maxScale, minScale, GetHeightFactor(worldY, maxHeight));
    }

    // ─── TRL Integration (optional dependency) ───────────────────────
    private static Type _trlApiType;
    private static bool _trlChecked;

    private static void EnsureTRLIntegration()
    {
        if (_trlChecked) return;
        _trlChecked = true;

        try
        {
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "ToasterReskinLoader");
            if (asm == null) return;

            _trlApiType = asm.GetType("ToasterReskinLoader.ToasterReskinLoaderAPI");
            if (_trlApiType == null) return;

            // Subscribe to color change events
            var evt = _trlApiType.GetEvent("OnTeamColorsChanged");
            if (evt != null)
            {
                evt.AddEventHandler(null, new Action(OnTRLTeamColorsChanged));
                Plugin.Log("TRL integration: subscribed to OnTeamColorsChanged");
            }

            // Subscribe to minimap settings changes
            var minimapEvt = _trlApiType.GetEvent("OnMinimapSettingsChanged");
            if (minimapEvt != null)
            {
                minimapEvt.AddEventHandler(null, new Action(OnTRLMinimapSettingsChanged));
                Plugin.Log("TRL integration: subscribed to OnMinimapSettingsChanged");
            }
        }
        catch (Exception e)
        {
            Plugin.Log($"TRL integration: not available ({e.Message})");
        }
    }

    private static void OnTRLTeamColorsChanged()
    {
        try
        {
            // Recolor all existing stick elements
            foreach (var kvp in stickVisualElementMap)
            {
                if (!kvp.Key || kvp.Key.Player == null) continue;
                kvp.Value.style.backgroundColor = new StyleColor(GetTeamColor(kvp.Key.Player.Team));
            }

            // Recolor side view dots
            foreach (var kvp in playerSideLeftMap)
            {
                if (!kvp.Key || !kvp.Key.Player) continue;
                kvp.Value.style.backgroundColor = new StyleColor(GetTeamColor(kvp.Key.Player.Team));
            }
            foreach (var kvp in playerSideBottomMap)
            {
                if (!kvp.Key || !kvp.Key.Player) continue;
                kvp.Value.style.backgroundColor = new StyleColor(GetTeamColor(kvp.Key.Player.Team));
            }

            Plugin.Log("TRL integration: recolored all elements");
        }
        catch (Exception e)
        {
            Plugin.Log($"TRL integration: recolor error ({e.Message})");
        }
    }

    private static Color GetTeamColor(PlayerTeam team)
    {
        // Check TRL for custom team colors
        if (_trlApiType != null)
        {
            try
            {
                bool enabled = (bool)_trlApiType.GetProperty("TeamColorsEnabled").GetValue(null);
                if (enabled)
                {
                    string prop = team == PlayerTeam.Blue ? "BlueTeamColor" : "RedTeamColor";
                    return (Color)_trlApiType.GetProperty(prop).GetValue(null);
                }
            }
            catch { }
        }

        return team == PlayerTeam.Blue
            ? new Color(23 / 255f, 92 / 255f, 230 / 255f)
            : new Color(230 / 255f, 39 / 255f, 39 / 255f);
    }

    private static void OnTRLMinimapSettingsChanged()
    {
        // Puck scale/color are applied per-frame in Update, so nothing to eagerly refresh.
        // This is here for future use or if we add non-per-frame properties.
    }

    /// <summary>
    /// Returns TRL's minimap refresh rate (default 60 if TRL not loaded).
    /// </summary>
    private static int GetTRLRefreshRate()
    {
        if (_trlApiType == null) return 60;
        try
        {
            return (int)_trlApiType.GetProperty("MinimapRefreshRate").GetValue(null);
        }
        catch { return 60; }
    }

    /// <summary>
    /// Returns TRL's minimap puck scale setting (default 1.0 if TRL not loaded).
    /// </summary>
    private static float GetTRLPuckScale()
    {
        if (_trlApiType == null) return 1f;
        try
        {
            return (float)_trlApiType.GetProperty("MinimapPuckScale").GetValue(null);
        }
        catch { return 1f; }
    }

    /// <summary>
    /// Returns TRL's minimap player scale setting (default 1.0 if TRL not loaded).
    /// </summary>
    private static float GetTRLPlayerScale()
    {
        if (_trlApiType == null) return 1f;
        try
        {
            return (float)_trlApiType.GetProperty("MinimapPlayerScale").GetValue(null);
        }
        catch { return 1f; }
    }

    /// <summary>
    /// Returns the signed angle (in degrees) between the look direction and the direction
    /// from the local player to a target, projected onto the XZ plane.
    /// Result is in [-180, 180]. If |result| <= halfFov, the target is visible.
    /// </summary>
    private static float AngleToTarget(Vector3 fromPos, Vector3 toPos, float lookYawDegrees)
    {
        float dx = toPos.x - fromPos.x;
        float dz = toPos.z - fromPos.z;
        // Atan2(x, z) gives angle from +Z axis, clockwise = positive, matching Unity's Y euler convention
        float angleToTarget = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
        float diff = Mathf.DeltaAngle(lookYawDegrees, angleToTarget);
        return diff;
    }

    // ─── Side View Setup ──────────────────────────────────────────────

    private static void EnsureSideViewsInitialized(UIMinimap minimap)
    {
        if (sideViewsInitialized) return;

        VisualElement minimapElement = (VisualElement)_minimapField.GetValue(minimap);
        if (minimapElement == null) return;

        // Left side panel (long-ways: shares vertical axis with minimap, horizontal = height)
        sideViewLeft = new VisualElement();
        sideViewLeft.name = "SideViewLeft";
        sideViewLeft.style.position = Position.Absolute;
        sideViewLeft.style.width = SideViewThickness;
        sideViewLeft.style.height = new Length(100, LengthUnit.Percent);
        sideViewLeft.style.right = new Length(100, LengthUnit.Percent);
        sideViewLeft.style.top = 0;
        sideViewLeft.style.marginRight = SideViewGap;
        sideViewLeft.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0.4f));
        sideViewLeft.style.overflow = Overflow.Hidden;
        sideViewLeft.style.display = DisplayStyle.Flex;
        minimapElement.Add(sideViewLeft);

        // Bottom panel (short-ways: shares horizontal axis with minimap, vertical = height)
        sideViewBottom = new VisualElement();
        sideViewBottom.name = "SideViewBottom";
        sideViewBottom.style.position = Position.Absolute;
        sideViewBottom.style.height = SideViewThickness;
        sideViewBottom.style.width = new Length(100, LengthUnit.Percent);
        sideViewBottom.style.top = new Length(100, LengthUnit.Percent);
        sideViewBottom.style.left = 0;
        sideViewBottom.style.marginTop = SideViewGap;
        sideViewBottom.style.backgroundColor = new StyleColor(new Color(0, 0, 0, 0.4f));
        sideViewBottom.style.overflow = Overflow.Hidden;
        sideViewBottom.style.display = DisplayStyle.Flex;
        minimapElement.Add(sideViewBottom);

        sideViewsInitialized = true;
    }

    private static VisualElement CreateSideDot(Color color, float size = 4f)
    {
        var dot = new VisualElement();
        dot.style.position = Position.Absolute;
        dot.style.width = size;
        dot.style.height = size;
        dot.style.backgroundColor = new StyleColor(color);
        dot.style.borderTopLeftRadius = size / 2;
        dot.style.borderTopRightRadius = size / 2;
        dot.style.borderBottomLeftRadius = size / 2;
        dot.style.borderBottomRightRadius = size / 2;
        return dot;
    }

    /// <summary>
    /// Positions a side-view dot using the exact minimap pixel coordinates for the shared axis.
    /// minimapPos comes from WorldPositionToMinimapPosition (same values the vanilla minimap uses).
    /// The vanilla minimap's content element is centered, so translate(0,0) = center of rink.
    /// Our side panels start from top-left, so we offset by half the panel dimension to match.
    /// </summary>
    private static void PositionSideDot(VisualElement dot, Vector2 minimapPos, float worldY,
        bool isLeftPanel, float contentWidth, float contentHeight)
    {
        float heightNorm = Mathf.Clamp01(Mathf.Abs(worldY) / PuckMaxHeight);

        if (isLeftPanel)
        {
            // Vertical axis = minimap's Y, offset by half panel height to center
            // Horizontal axis = height: ground on RIGHT (near minimap), elevated on LEFT (away)
            float x = (1f - heightNorm) * (SideViewThickness - 4f);
            float y = minimapPos.y + contentHeight / 2f;
            dot.style.translate = new Translate(x, y);
        }
        else
        {
            // Horizontal axis = minimap's X, offset by half panel width to center
            // The minimap uses translate(-posX, _), so we negate and offset
            float x = -minimapPos.x + contentWidth / 2f;
            // Vertical axis = height: ice at TOP (near minimap), elevated going DOWN (away)
            // Reflected vertically: elevated at TOP, ice at BOTTOM
            float y = (1f - heightNorm) * (SideViewThickness - 4f);
            dot.style.translate = new Translate(x, y);
        }
    }

    // ─── Minimap Update Patch ─────────────────────────────────────────

    // Accumulator to match our update rate to the configured minimap refresh rate
    private static float updateAccumulator;

    [HarmonyPatch(typeof(UIMinimap), "Update")]
    public static class UIMinimapUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(UIMinimap __instance)
        {
            EnsureTRLIntegration();

            // Rate-limit to match TRL's minimap refresh rate setting
            updateAccumulator += Time.deltaTime;
            int refreshRate = GetTRLRefreshRate();
            if (refreshRate > 0 && updateAccumulator < 1f / refreshRate)
                return;
            updateAccumulator = 0f;

            if (EnableSideViews)
                EnsureSideViewsInitialized(__instance);

            VisualElement content = (VisualElement)_contentField.GetValue(__instance);
            float contentWidth = content.resolvedStyle.width;
            float contentHeight = content.resolvedStyle.height;
            Bounds bounds = __instance.Bounds;

            // ── Update sticks on minimap ──
            foreach (var kvp in stickVisualElementMap)
            {
                Stick stick = kvp.Key;
                VisualElement stickVE = kvp.Value;
                if (!stick) continue;

                GameObject bladeHandle = (GameObject)_bladeHandleField.GetValue(stick);
                Vector3 worldPos = bladeHandle.transform.position;
                Vector3 teamPos = __instance.Team == PlayerTeam.Blue ? worldPos : -worldPos;

                float stickWorldAngle = __instance.Team == PlayerTeam.Blue
                    ? bladeHandle.transform.rotation.eulerAngles.y
                    : bladeHandle.transform.rotation.eulerAngles.y + 180f;

                float bladeAngleStep = (float)_bladeAngleStepField.GetValue(stick);
                float stickRotationAngle = (float)stick.Player.PlayerInput.BladeAngleInput.ServerValue * bladeAngleStep;

                Vector2 posOnMinimap = (Vector2)_worldToMinimapMethod.Invoke(__instance,
                    new object[] { teamPos, bounds });
                stickVE.style.translate = new Translate(-posOnMinimap.x, posOnMinimap.y);
                stickVE.style.rotate = new Rotate(stickWorldAngle - stickRotationAngle);

                float scale = GetScaleFromHeight(worldPos.y, StickMaxHeight, StickMaxScale, StickMinScale);
                stickVE.style.scale = new StyleScale(new Scale(new Vector2(scale, 1)));
                stickVE.style.opacity = scale;
            }

            // ── Shared lookups ──
            var puckMap = (Dictionary<Puck, VisualElement>)_puckVisualElementMapField.GetValue(__instance);
            var playerMap = (Dictionary<PlayerBody, VisualElement>)_playerBodyVisualElementMapField.GetValue(__instance);

            // ── Update puck height-based opacity + shrink ──
            if (EnablePuckHeightOpacity)
            {
                float trlPuckBase = GetTRLPuckScale();
                foreach (var kvp in puckMap)
                {
                    Puck puck = kvp.Key;
                    VisualElement puckVE = kvp.Value;
                    if (!puck) continue;

                    float worldY = puck.transform.position.y;
                    float heightFactor = GetScaleFromHeight(worldY, PuckMaxHeight, PuckMaxScale, PuckMinScale);
                    puckVE.style.opacity = heightFactor;
                    float scale = heightFactor * trlPuckBase;
                    puckVE.style.scale = new StyleScale(new Scale(new Vector2(scale, scale)));
                }
            }

            // ── Fog of War: hide elements outside view cone ──
            if (EnableFogOfWar)
            {
                Player localPlayer = MonoBehaviourSingleton<PlayerManager>.Instance.GetLocalPlayer();
                    if (localPlayer != null && localPlayer.PlayerBody != null && localPlayer.PlayerInput != null)
                    {
                        PlayerBody localBody = localPlayer.PlayerBody;
                        Vector3 localWorldPos = localBody.transform.position;

                        // Look direction: body yaw + camera local yaw, flipped 180 to match minimap orientation
                        float bodyYaw = localBody.transform.rotation.eulerAngles.y;
                        float cameraYaw = localPlayer.PlayerInput.LookAngleInput.ClientValue.y;
                        float lookYaw = bodyYaw + cameraYaw + 180f;

                        // For Red team the minimap is flipped
                        if (__instance.Team == PlayerTeam.Red)
                            lookYaw += 180f;

                        float halfFov = FogFovDegrees / 2f;

                        // Hide player bodies outside the cone (skip self)
                        foreach (var kvp in playerMap)
                        {
                            PlayerBody body = kvp.Key;
                            VisualElement container = kvp.Value;
                            if (!body) continue;

                            Player player = body.Player;
                            if (player && player.IsLocalPlayer)
                                continue;

                            float angle = AngleToTarget(localWorldPos, body.transform.position, lookYaw);
                            bool inCone = Mathf.Abs(angle) <= halfFov;
                            container.style.display = inCone ? DisplayStyle.Flex : DisplayStyle.None;
                        }

                        // Hide pucks outside the cone
                        foreach (var kvp in puckMap)
                        {
                            Puck puck = kvp.Key;
                            VisualElement ve = kvp.Value;
                            if (!puck) continue;

                            float angle = AngleToTarget(localWorldPos, puck.transform.position, lookYaw);
                            float fogOpacity = Mathf.Abs(angle) <= halfFov ? 1f : FogHiddenOpacity;

                            // If puck height opacity is also active, use the lower of the two
                            if (EnablePuckHeightOpacity)
                            {
                                float heightScale = GetScaleFromHeight(puck.transform.position.y, PuckMaxHeight, PuckMaxScale, PuckMinScale);
                                ve.style.opacity = Mathf.Min(fogOpacity, heightScale);
                            }
                            else
                            {
                                ve.style.opacity = fogOpacity;
                            }
                        }

                        // Hide enemy sticks outside the cone
                        foreach (var kvp in stickVisualElementMap)
                        {
                            Stick stick = kvp.Key;
                            VisualElement ve = kvp.Value;
                            if (!stick) continue;

                            // Always show own stick
                            if (stick.Player && stick.Player.IsLocalPlayer)
                                continue;

                            GameObject bladeHandle = (GameObject)_bladeHandleField.GetValue(stick);
                            float angle = AngleToTarget(localWorldPos, bladeHandle.transform.position, lookYaw);
                            float fogOpacity = Mathf.Abs(angle) <= halfFov ? 1f : FogHiddenOpacity;

                            // Combine with existing height-based opacity
                            float heightScale = GetScaleFromHeight(bladeHandle.transform.position.y, StickMaxHeight, StickMaxScale, StickMinScale);
                            ve.style.opacity = Mathf.Min(fogOpacity, heightScale);
                    }
                }
            }

            // ── Update side view dots (players + pucks) ──
            if (!EnableSideViews || sideViewLeft == null) return;

            // Players (using playerBodyVisualElementMap like vanilla)
            foreach (var kvp in playerMap)
            {
                PlayerBody body = kvp.Key;
                if (!body) continue;

                Vector3 worldPos = body.transform.position;
                Vector3 teamPos = __instance.Team == PlayerTeam.Blue ? worldPos : -worldPos;
                Vector2 minimapPos = (Vector2)_worldToMinimapMethod.Invoke(__instance,
                    new object[] { teamPos, bounds });

                if (playerSideLeftMap.TryGetValue(body, out var leftDot))
                    PositionSideDot(leftDot, minimapPos, worldPos.y, true, contentWidth, contentHeight);

                if (playerSideBottomMap.TryGetValue(body, out var bottomDot))
                    PositionSideDot(bottomDot, minimapPos, worldPos.y, false, contentWidth, contentHeight);
            }

            // Pucks
            foreach (var kvp in puckMap)
            {
                Puck puck = kvp.Key;
                if (!puck) continue;

                Vector3 worldPos = puck.transform.position;
                Vector3 teamPos = __instance.Team == PlayerTeam.Blue ? worldPos : -worldPos;
                Vector2 minimapPos = (Vector2)_worldToMinimapMethod.Invoke(__instance,
                    new object[] { teamPos, bounds });

                if (puckSideLeftMap.TryGetValue(puck, out var leftDot))
                    PositionSideDot(leftDot, minimapPos, worldPos.y, true, contentWidth, contentHeight);

                if (puckSideBottomMap.TryGetValue(puck, out var bottomDot))
                    PositionSideDot(bottomDot, minimapPos, worldPos.y, false, contentWidth, contentHeight);
            }
        }
    }

    // ─── Stick Spawn / Despawn (minimap only, no side view) ───────────

    [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
    public static class StickOnNetworkPostSpawnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance)
        {
            VisualElement stickVisualElement = CreateStickVisualElement(__instance.Player);
            UIMinimap minimap = MonoBehaviourSingleton<UIManager>.Instance.Minimap;
            VisualElement contentElement = (VisualElement)_contentField.GetValue(minimap);
            contentElement.Add(stickVisualElement);
            stickVisualElement.SendToBack();
            stickVisualElementMap[__instance] = stickVisualElement;
        }
    }

    private static VisualElement CreateStickVisualElement(Player player)
    {
        VisualElement stickVisualElement = new VisualElement();
        stickVisualElement.name = $"{player.Username.Value.ToString()} Stick Visual";
        stickVisualElement.style.backgroundColor = new StyleColor(GetTeamColor(player.Team));
        stickVisualElement.style.minHeight = new StyleLength(new Length(8, LengthUnit.Pixel));
        stickVisualElement.style.minWidth = new StyleLength(new Length(3, LengthUnit.Pixel));
        stickVisualElement.style.position = Position.Absolute;
        stickVisualElement.style.display = DisplayStyle.Flex;
        return stickVisualElement;
    }

    [HarmonyPatch(typeof(Stick), "OnDestroy")]
    public static class StickOnDestroyPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Stick __instance)
        {
            RemoveStickFromMinimap(__instance);
        }
    }

    [HarmonyPatch(typeof(Stick), nameof(Stick.OnNetworkDespawn))]
    public static class StickOnNetworkDespawnPatch
    {
        [HarmonyPrefix]
        public static void Prefix(Stick __instance)
        {
            RemoveStickFromMinimap(__instance);
        }
    }

    private static void RemoveStickFromMinimap(Stick stick)
    {
        if (!stick) return;
        if (!stickVisualElementMap.ContainsKey(stick)) return;
        UIMinimap minimap = MonoBehaviourSingleton<UIManager>.Instance.Minimap;
        ((VisualElement)_contentField.GetValue(minimap)).Remove(stickVisualElementMap[stick]);
        stickVisualElementMap.Remove(stick);
    }

    // ─── Player Body Spawn / Despawn (side view dots) ─────────────────

    [HarmonyPatch(typeof(UIMinimap), nameof(UIMinimap.AddPlayerBody))]
    public static class MinimapAddPlayerBodyPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UIMinimap __instance, PlayerBody playerBody)
        {
            if (!playerBody) return;
            if (!EnableSideViews) return;
            EnsureSideViewsInitialized(__instance);

            Player player = playerBody.Player;
            Color color = player ? GetTeamColor(player.Team) : Color.gray;

            if (sideViewLeft != null && !playerSideLeftMap.ContainsKey(playerBody))
            {
                var dot = CreateSideDot(color, 5f);
                sideViewLeft.Add(dot);
                playerSideLeftMap[playerBody] = dot;
            }
            if (sideViewBottom != null && !playerSideBottomMap.ContainsKey(playerBody))
            {
                var dot = CreateSideDot(color, 5f);
                sideViewBottom.Add(dot);
                playerSideBottomMap[playerBody] = dot;
            }
        }
    }

    [HarmonyPatch(typeof(UIMinimap), nameof(UIMinimap.RemovePlayerBody))]
    public static class MinimapRemovePlayerBodyPatch
    {
        [HarmonyPrefix]
        public static void Prefix(PlayerBody playerBody)
        {
            if (!playerBody) return;
            if (playerSideLeftMap.TryGetValue(playerBody, out var leftDot))
            {
                leftDot.RemoveFromHierarchy();
                playerSideLeftMap.Remove(playerBody);
            }
            if (playerSideBottomMap.TryGetValue(playerBody, out var bottomDot))
            {
                bottomDot.RemoveFromHierarchy();
                playerSideBottomMap.Remove(playerBody);
            }
        }
    }

    // ─── Puck Spawn / Despawn (side view dots) ────────────────────────

    [HarmonyPatch(typeof(UIMinimap), nameof(UIMinimap.AddPuck))]
    public static class MinimapAddPuckPatch
    {
        [HarmonyPostfix]
        public static void Postfix(UIMinimap __instance, Puck puck)
        {
            if (!puck) return;
            if (!EnableSideViews) return;
            EnsureSideViewsInitialized(__instance);

            if (sideViewLeft != null && !puckSideLeftMap.ContainsKey(puck))
            {
                var dot = CreateSideDot(Color.black, 5f);
                sideViewLeft.Add(dot);
                puckSideLeftMap[puck] = dot;
            }
            if (sideViewBottom != null && !puckSideBottomMap.ContainsKey(puck))
            {
                var dot = CreateSideDot(Color.black, 5f);
                sideViewBottom.Add(dot);
                puckSideBottomMap[puck] = dot;
            }
        }
    }

    [HarmonyPatch(typeof(UIMinimap), nameof(UIMinimap.RemovePuck))]
    public static class MinimapRemovePuckPatch
    {
        [HarmonyPrefix]
        public static void Prefix(UIMinimap __instance, Puck puck)
        {
            if (!puck) return;
            if (puckSideLeftMap.TryGetValue(puck, out var leftDot))
            {
                leftDot.RemoveFromHierarchy();
                puckSideLeftMap.Remove(puck);
            }
            if (puckSideBottomMap.TryGetValue(puck, out var bottomDot))
            {
                bottomDot.RemoveFromHierarchy();
                puckSideBottomMap.Remove(puck);
            }
        }
    }
}
