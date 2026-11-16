using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakVRoid.Game
{
    /// <summary>
    /// PEAK hides your OWN worn backpack (CharacterBackpackHandler.LateUpdate deactivates it for the local
    /// player unless spectating) because you normally play in first person. In third person we want to see
    /// it, so after the game's LateUpdate we re-enable it whenever the game would show it to other players.
    /// </summary>
    public static class BackpackPatch
    {
        private static FieldInfo backpackField;
        private static FieldInfo characterField;
        private static FieldInfo lastShowField;
        public static Type HandlerType { get; private set; }

        public static void Apply(Harmony harmony)
        {
            HandlerType = Peak.FindType("CharacterBackpackHandler");
            var lateUpdate = HandlerType != null ? AccessTools.Method(HandlerType, "LateUpdate") : null;
            backpackField = HandlerType != null ? AccessTools.Field(HandlerType, "backpack") : null;
            characterField = HandlerType != null ? AccessTools.Field(HandlerType, "character") : null;
            lastShowField = HandlerType != null ? AccessTools.Field(HandlerType, "lastShow") : null;
            if (lateUpdate == null || backpackField == null || lastShowField == null)
            {
                Plugin.Log.LogWarning("CharacterBackpackHandler not found; your own backpack stays hidden in third person.");
                return;
            }
            harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(BackpackPatch), nameof(Postfix)));
            Plugin.Log.LogInfo("Backpack patch applied.");
        }

        /// <summary>The worn-backpack object of a character (active or not), or null.</summary>
        public static GameObject BackpackOf(Component character)
        {
            if (HandlerType == null || backpackField == null || character == null) return null;
            var handler = character.GetComponent(HandlerType);
            return handler != null ? backpackField.GetValue(handler) as GameObject : null;
        }

        private static void Postfix(object __instance)
        {
            try
            {
                if (!ModConfig.ShowBackpack.Value || !ThirdPersonCamera.Active) return;
                // lastShow is the game's own "is wearing a backpack and not holding it" result for this frame.
                if (!(lastShowField.GetValue(__instance) is bool show) || !show) return;
                var character = (characterField?.GetValue(__instance) as Component) ?? (__instance as Component)?.GetComponent(Peak.CharacterType);
                if (character == null || character != Peak.LocalCharacter) return;
                if (backpackField.GetValue(__instance) is GameObject backpack && backpack != null && !backpack.activeSelf)
                    backpack.SetActive(true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Backpack patch error: " + e.Message);
                ModConfig.ShowBackpack.Value = false;
            }
        }
    }
}
