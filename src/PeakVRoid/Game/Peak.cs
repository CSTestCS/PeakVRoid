using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PeakVRoid.Game
{
    /// <summary>
    /// Everything PeakVRoid needs from PEAK's Assembly-CSharp, looked up by name with
    /// reflection. That keeps the mod buildable without game files and makes it degrade
    /// gracefully (with a log message) if a game update renames something.
    /// </summary>
    public static class Peak
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static Type CharacterType { get; private set; }
        private static FieldInfo localCharacterField;
        private static FieldInfo allCharactersField;
        private static FieldInfo refsField;
        private static PropertyInfo characterNameProp;
        private static FieldInfo mainCameraInstanceField;
        private static FieldInfo mainCameraCamField;
        private static FieldInfo mainCameraOverrideField;
        private static PropertyInfo isSpectatingProp;

        // Per-type member caches for the nested refs objects.
        private static readonly Dictionary<(Type, string), MemberInfo> members = new Dictionary<(Type, string), MemberInfo>();

        public static bool Ready => CharacterType != null;

        public static void Init()
        {
            CharacterType = FindType("Character");
            if (CharacterType == null)
            {
                Plugin.Log.LogError("PEAK's Character class was not found; avatars are disabled.");
                return;
            }
            localCharacterField = CharacterType.GetField("localCharacter", Any);
            allCharactersField = CharacterType.GetField("AllCharacters", Any);
            refsField = CharacterType.GetField("refs", Any);
            characterNameProp = CharacterType.GetProperty("characterName", Any);

            var mainCamera = FindType("MainCamera");
            if (mainCamera != null)
            {
                mainCameraInstanceField = mainCamera.GetField("instance", Any);
                mainCameraCamField = mainCamera.GetField("cam", Any);
                mainCameraOverrideField = mainCamera.GetField("camOverride", Any);
            }
            var movement = FindType("MainCameraMovement");
            if (movement != null) isSpectatingProp = movement.GetProperty("IsSpectating", Any);

            Plugin.Log.LogInfo($"PEAK hooks: character={localCharacterField != null}, all={allCharactersField != null}, " +
                               $"refs={refsField != null}, camera={mainCameraInstanceField != null}, spectate={isSpectatingProp != null}");
        }

        public static Type FindType(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an != "Assembly-CSharp" && !an.StartsWith("Zorro", StringComparison.Ordinal)) continue;
                var t = asm.GetType(name, false);
                if (t != null) return t;
            }
            return null;
        }

        // ------------------------------------------------------------------ characters

        public static Component LocalCharacter => localCharacterField?.GetValue(null) as Component;

        public static IEnumerable<Component> AllCharacters()
        {
            if (allCharactersField?.GetValue(null) is IEnumerable list)
            {
                foreach (var o in list)
                    if (o is Component c && c != null) yield return c;
            }
            else if (CharacterType != null)
            {
                foreach (var o in UnityEngine.Object.FindObjectsByType(CharacterType, FindObjectsSortMode.None))
                    if (o is Component c) yield return c;
            }
        }

        public static string CharacterName(Component character)
        {
            try { return characterNameProp?.GetValue(character) as string ?? character.name; }
            catch { return character.name; }
        }

        public static object Refs(Component character) => refsField?.GetValue(character);

        public static object Get(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            if (!members.TryGetValue((t, name), out var m))
            {
                m = (MemberInfo)t.GetField(name, Any) ?? t.GetProperty(name, Any);
                members[(t, name)] = m;
            }
            try
            {
                if (m is FieldInfo f) return f.GetValue(obj);
                if (m is PropertyInfo p) return p.GetValue(obj);
            }
            catch { }
            return null;
        }

        public static SkinnedMeshRenderer MainRenderer(Component character)
        {
            if (Get(Refs(character), "mainRenderer") is SkinnedMeshRenderer smr && smr != null) return smr;
            // Fallback: the skinned renderer with the most bones.
            SkinnedMeshRenderer best = null;
            foreach (var r in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (best == null || r.bones.Length > best.bones.Length) best = r;
            return best;
        }

        /// <summary>BodypartType name (e.g. "Arm_L") → animated bodypart transform.</summary>
        public static Dictionary<string, Transform> Bodyparts(Component character)
        {
            var result = new Dictionary<string, Transform>();
            var ragdoll = Get(Refs(character), "ragdoll");
            if (Get(ragdoll, "partDict") is IDictionary dict)
            {
                foreach (DictionaryEntry e in dict)
                    if (e.Value is Component c && c != null) result[e.Key.ToString()] = c.transform;
            }
            if (result.Count == 0)
            {
                // Fallback: match bone names of the visible mesh.
                var smr = MainRenderer(character);
                if (smr != null)
                    foreach (var b in smr.bones)
                        if (b != null && !result.ContainsKey(b.name)) result[b.name] = b;
            }
            return result;
        }

        /// <summary>The renderers that draw the scout (body, face, hats, accessories), so they can be hidden.</summary>
        public static List<Renderer> ScoutRenderers(Component character)
        {
            var list = new List<Renderer>();
            var main = MainRenderer(character);
            if (main != null) list.Add(main);

            var custom = Get(Refs(character), "customization") as Component;
            var customRefs = Get(custom, "refs");
            if (customRefs != null)
            {
                foreach (var f in customRefs.GetType().GetFields(Any))
                {
                    object v;
                    try { v = f.GetValue(customRefs); } catch { continue; }
                    if (v is Renderer r && r != null) list.Add(r);
                    else if (v is IEnumerable e && !(v is string) && !(v is Transform))
                        foreach (var o in e)
                            if (o is Renderer rr && rr != null) list.Add(rr);
                }
            }
            foreach (var r in character.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                if (!list.Contains(r)) list.Add(r);
            return list;
        }

        // ------------------------------------------------------------------ camera

        public static Camera MainCamera
        {
            get
            {
                var inst = mainCameraInstanceField?.GetValue(null) as Component;
                if (inst != null)
                {
                    if (mainCameraCamField?.GetValue(inst) is Camera c && c != null) return c;
                    var cc = inst.GetComponent<Camera>();
                    if (cc != null) return cc;
                }
                return Camera.main;
            }
        }

        /// <summary>True while PEAK drives the camera itself (spectating, cutscenes, camera overrides).</summary>
        public static bool CameraBusy
        {
            get
            {
                try
                {
                    if (isSpectatingProp != null && (bool)isSpectatingProp.GetValue(null)) return true;
                    var inst = mainCameraInstanceField?.GetValue(null) as Component;
                    if (inst != null && mainCameraOverrideField?.GetValue(inst) is UnityEngine.Object o && o != null) return true;
                }
                catch { }
                return false;
            }
        }
    }
}
