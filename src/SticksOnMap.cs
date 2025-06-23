using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UIElements;

namespace ToasterSticksOnMap;

public static class SticksOnMap
{
    private static Dictionary<Stick, VisualElement> stickVisualElementMap = new Dictionary<Stick, VisualElement>();
    
    static readonly FieldInfo _bladeHandleField = typeof(Stick)
        .GetField("bladeHandle",
            BindingFlags.Instance | BindingFlags.NonPublic);
    
    static readonly FieldInfo _bladeAngleStepField = typeof(Stick)
        .GetField("bladeAngleStep",
            BindingFlags.Instance | BindingFlags.NonPublic);
    
    [HarmonyPatch(typeof(UIMinimap), "Update")]
    public static class UIMinimapUpdatePatch
    {
        // Define the height range for interpolation
        static float minHeight = 0f;
        static float maxHeight = 3f;

        // Define the scale range
        static float maxScale = 1.0f; // 1x scale at height 0
        static float minScale = 0.5f; // 0.5x scale at height 3 or more
        
        [HarmonyPostfix]
        public static void Postfix(UIMinimap __instance)
        {
            foreach (KeyValuePair<Stick, VisualElement> keyValuePair in stickVisualElementMap)
            {
                Stick stick = keyValuePair.Key;
                VisualElement stickVisualElement = keyValuePair.Value;
                if (stick)
                {
                    MethodInfo worldPositionToMinimapPositionMethod = __instance.GetType().GetMethod("WorldPositionToMinimapPosition", 
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    
                    GameObject bladeHandle = (GameObject)_bladeHandleField.GetValue(stick);
                    
                    Vector3 stickPosition = ((__instance.Team == PlayerTeam.Blue) ? bladeHandle.transform.position : (-bladeHandle.transform.position));
                    float stickWorldAngle = ((__instance.Team == PlayerTeam.Blue) ? bladeHandle.transform.rotation.eulerAngles.y : (bladeHandle.transform.rotation.eulerAngles.y + 180f));
                    float bladeAngleStep = (float) _bladeAngleStepField.GetValue(stick);
                    float stickRotationAngle = (float)stick.Player.PlayerInput.BladeAngleInput.ServerValue * bladeAngleStep;
                    Vector2 positionOnMinimap = (Vector2) worldPositionToMinimapPositionMethod.Invoke(__instance, new object[] { stickPosition, LevelManager.Instance.IceBounds });
                    stickVisualElement.style.translate = new Translate(-positionOnMinimap.x, positionOnMinimap.y);
                    stickVisualElement.style.rotate = new Rotate(stickWorldAngle - stickRotationAngle);
                    
                    // Calculate the scale based on stick height
                    float stickHeight = -stickPosition.y; // Assuming Y-axis represents height
                    
                    // Clamp the stickHeight to the defined range
                    float clampedHeight = Mathf.Clamp(stickHeight, minHeight, maxHeight);

                    // Calculate the interpolation factor (0 to 1)
                    float t = 0f;
                    if (maxHeight > minHeight) {
                        t = (clampedHeight - minHeight) / (maxHeight - minHeight);
                    }

                    // Linearly interpolate the scale
                    float currentScale = Mathf.Lerp(maxScale, minScale, t);
                    stickVisualElement.style.scale = new StyleScale(new Scale(new Vector2(currentScale, 1)));
                    stickVisualElement.style.opacity = currentScale;
                }
            }
        }
    }
    
    static readonly FieldInfo _minimapMarkingsVisualElementField = typeof(UIMinimap)
        .GetField("minimapMarkingsVisualElement",
            BindingFlags.Instance | BindingFlags.NonPublic);

    [HarmonyPatch(typeof(Stick), "OnNetworkPostSpawn")]
    public static class StickOnNetworkPostSpawnPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Stick __instance)
        {
            VisualElement stickVisualElement = CreateStickVisualElement(__instance.Player);
            VisualElement minimapMarkingsVisualElement = (VisualElement) _minimapMarkingsVisualElementField.GetValue(UIMinimap.Instance);
            minimapMarkingsVisualElement.Add(stickVisualElement);
            stickVisualElement.SendToBack();
            stickVisualElementMap.Add(__instance, stickVisualElement);
        }
    }

    private static VisualElement CreateStickVisualElement(Player player)
    {
        VisualElement stickVisualElement = new VisualElement();
        stickVisualElement.name = $"{player.Username.Value.ToString()} Stick Visual";
        stickVisualElement.style.backgroundColor = new StyleColor(player.Team.Value == PlayerTeam.Blue ? new Color(23/255f, 92/255f, 230/255f) : new Color(230/255f, 39/255f, 39/255f));
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
        ((VisualElement) _minimapMarkingsVisualElementField.GetValue(UIMinimap.Instance)).Remove(stickVisualElementMap[stick]);
        stickVisualElementMap.Remove(stick);
    }
}