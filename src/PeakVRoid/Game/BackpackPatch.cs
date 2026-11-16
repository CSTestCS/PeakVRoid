using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakVRoid.Game
{
    /// <summary>
    /// PEAK hides your OWN worn backpack (CharacterBackpackHandler.LateUpdate deactivates it for the local
    /// player unless spectating) because you normally play in first person. In third person we want to see
    /// it, so after the game's LateUpdate we re-enable it whenever the game would show it to other players.
    /// Field names differ between game versions, so everything is located by type and name heuristics.
    /// </summary>
    public static class BackpackPatch
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo backpackField;
        private static FieldInfo characterField;
        private static FieldInfo lastShowField;
        public static Type HandlerType { get; private set; }

        public static void Apply(Harmony harmony)
        {
            HandlerType = Peak.FindType("CharacterBackpackHandler");
            if (HandlerType == null)
            {
                Plugin.Log.LogWarning("CharacterBackpackHandler not found; your own backpack stays hidden in third person.");
                return;
            }
            var fields = HandlerType.GetFields(All);
            var goFields = fields.Where(f => f.FieldType == typeof(GameObject)).ToArray();
            backpackField = goFields.FirstOrDefault(f => f.Name.IndexOf("backpack", StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? goFields.FirstOrDefault(f => f.Name.IndexOf("visual", StringComparison.OrdinalIgnoreCase) >= 0)
                            ?? (goFields.Length == 1 ? goFields[0] : null);
            characterField = fields.FirstOrDefault(f => Peak.CharacterType != null && f.FieldType == Peak.CharacterType);
            lastShowField = fields.FirstOrDefault(f => f.FieldType == typeof(bool) && f.Name.IndexOf("show", StringComparison.OrdinalIgnoreCase) >= 0);

            var lateUpdate = AccessTools.Method(HandlerType, "LateUpdate") ?? AccessTools.Method(HandlerType, "Update");
            if (lateUpdate == null || backpackField == null || lastShowField == null)
            {
                Plugin.Log.LogWarning("Couldn't recognise CharacterBackpackHandler in this game version; your own backpack stays hidden " +
                                      "in third person. Fields: " + string.Join(", ", fields.Select(f => f.FieldType.Name + " " + f.Name)) +
                                      "; methods: " + string.Join(", ", HandlerType.GetMethods(All | BindingFlags.DeclaredOnly).Select(m => m.Name)));
                return;
            }
            harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(BackpackPatch), nameof(Postfix)));
            Plugin.Log.LogInfo($"Backpack patch applied ({lateUpdate.Name}: {backpackField.Name}, {lastShowField.Name}).");
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
