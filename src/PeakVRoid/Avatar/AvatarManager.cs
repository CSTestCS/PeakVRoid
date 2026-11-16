using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using PeakVRoid.Game;
using PeakVRoid.Vrm;
using UnityEngine;

namespace PeakVRoid.Avatar
{
    /// <summary>Loads VRM files and keeps one <see cref="AvatarDriver"/> on every character that should have one.</summary>
    public sealed class AvatarManager
    {
        public static string AvatarFolder => Path.Combine(Paths.ConfigPath, "PeakVRoid");
        public static string PlayersFolder => Path.Combine(AvatarFolder, "players");

        private readonly Transform templates;
        private readonly Transform instances;
        private readonly Dictionary<string, VrmModel> cache = new Dictionary<string, VrmModel>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Component, AvatarDriver> drivers = new Dictionary<Component, AvatarDriver>();
        private readonly HashSet<Component> skipped = new HashSet<Component>();
        private readonly MaterialFactory materials = new MaterialFactory();
        private float nextScan;

        public AvatarManager(Transform root)
        {
            var t = new GameObject("Templates");
            t.SetActive(false);
            t.transform.SetParent(root, false);
            templates = t.transform;
            var i = new GameObject("Avatars");
            i.transform.SetParent(root, false);
            instances = i.transform;

            Directory.CreateDirectory(AvatarFolder);
            Directory.CreateDirectory(PlayersFolder);
            string readme = Path.Combine(AvatarFolder, "PUT YOUR .VRM FILES HERE.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme,
                    "Drop your VRM avatar (.vrm, VRM 0.x or 1.0) into this folder and start PEAK.\r\n" +
                    "Several files? Press F8 in game to cycle, or set [Avatar] File in BepInEx/config/com.peakvroid.peakvroid.cfg.\r\n\r\n" +
                    "players/ : put <SteamName>.vrm here to see a friend as that avatar on your screen.\r\n" +
                    "Hotkeys: F5 first/third person, F6 reload files, F7 avatar on/off, F8 next avatar, F9 swap shoulder, PageUp/PageDown zoom.\r\n");
        }

        public static string[] AvatarFiles()
        {
            try
            {
                return Directory.GetFiles(AvatarFolder, "*.vrm", SearchOption.TopDirectoryOnly)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            }
            catch { return new string[0]; }
        }

        public string SelectedFile()
        {
            string f = ModConfig.AvatarFile.Value?.Trim().Trim('"');
            if (!string.IsNullOrEmpty(f))
            {
                string p = Path.IsPathRooted(f) ? f : Path.Combine(AvatarFolder, f);
                if (File.Exists(p)) return p;
                if (File.Exists(p + ".vrm")) return p + ".vrm";
            }
            return AvatarFiles().FirstOrDefault();
        }

        private string PlayerFile(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return null;
            foreach (char c in Path.GetInvalidFileNameChars()) playerName = playerName.Replace(c, '_');
            string p = Path.Combine(PlayersFolder, playerName + ".vrm");
            return File.Exists(p) ? p : null;
        }

        // ------------------------------------------------------------------ commands

        public void NextAvatar()
        {
            var files = AvatarFiles();
            if (files.Length == 0)
            {
                Plugin.Toast("No .vrm files in " + AvatarFolder);
                return;
            }
            string current = SelectedFile();
            int i = Array.FindIndex(files, f => string.Equals(f, current, StringComparison.OrdinalIgnoreCase));
            string next = files[(i + 1) % files.Length];
            ModConfig.AvatarFile.Value = Path.GetFileName(next);
            if (!ModConfig.AvatarEnabled.Value) ModConfig.AvatarEnabled.Value = true;
            RemoveAll();
        }

        public void ToggleAvatar()
        {
            ModConfig.AvatarEnabled.Value = !ModConfig.AvatarEnabled.Value;
            Plugin.Toast(ModConfig.AvatarEnabled.Value ? "VRM avatar on" : "VRM avatar off");
            RemoveAll();
        }

        public void Reload()
        {
            RemoveAll();
            foreach (var m in cache.Values) DestroyModel(m);
            cache.Clear();
            failed.Clear();
            Plugin.Toast("Reloading avatars…");
        }

        public void RemoveAll()
        {
            foreach (var d in drivers.Values)
                if (d != null) UnityEngine.Object.Destroy(d.gameObject);
            drivers.Clear();
            skipped.Clear();
            nextScan = 0f;
        }

        // ------------------------------------------------------------------ per frame

        public void Update()
        {
            if (!Peak.Ready || Time.unscaledTime < nextScan) return;
            nextScan = Time.unscaledTime + 0.5f;

            // Forget characters that are gone.
            foreach (var dead in drivers.Where(kv => kv.Key == null || kv.Value == null).Select(kv => kv.Key).ToList())
                drivers.Remove(dead);
            skipped.RemoveWhere(c => c == null);

            var local = Peak.LocalCharacter;
            foreach (var character in Peak.AllCharacters())
            {
                if (drivers.ContainsKey(character) || skipped.Contains(character)) continue;
                string file = ChooseFile(character, character == local);
                if (file == null) { skipped.Add(character); continue; }
                // Wait until the character's rig exists.
                if (Peak.MainRenderer(character) == null || Peak.Bodyparts(character).Count == 0) continue;

                var model = GetModel(file, character);
                if (model == null) { skipped.Add(character); continue; }
                try
                {
                    var driver = AvatarDriver.Create(model, character, instances);
                    if (!driver.Ready)
                    {
                        UnityEngine.Object.Destroy(driver.gameObject);
                        skipped.Add(character);
                        continue;
                    }
                    drivers[character] = driver;
                    if (character == local) Plugin.Toast($"Avatar: {model.Title}");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Could not attach avatar to {character.name}: {e}");
                    skipped.Add(character);
                }
            }
        }

        private string ChooseFile(Component character, bool isLocal)
        {
            if (!ModConfig.AvatarEnabled.Value) return null;
            if (isLocal) return SelectedFile();
            string own = ModConfig.RemotePlayerAvatars.Value ? PlayerFile(Peak.CharacterName(character)) : null;
            if (own != null) return own;
            return ModConfig.ShowMyAvatarOnEveryone.Value ? SelectedFile() : null;
        }

        private VrmModel GetModel(string path, Component character)
        {
            if (cache.TryGetValue(path, out var m) && m.Template != null) return m;
            if (failed.Contains(path)) return null;
            try
            {
                materials.Unlit = ModConfig.Unlit.Value;
                materials.ShadeLift = ModConfig.ShadeLift.Value;
                materials.ShaderOverride = ModConfig.ShaderOverride.Value;
                var scout = Peak.MainRenderer(character);
                if (scout != null) materials.FallbackMaterial = scout.sharedMaterial;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                m = VrmLoader.Load(path, materials, templates);
                Plugin.Log.LogInfo($"Loaded VRM {m.SpecVersion}.x '{m.Title}' by {m.Author} from {Path.GetFileName(path)} in {sw.ElapsedMilliseconds} ms");
                cache[path] = m;
                return m;
            }
            catch (Exception e)
            {
                failed.Add(path);
                Plugin.Log.LogError($"Failed to load {path}: {e}");
                Plugin.Toast($"Could not load {Path.GetFileName(path)}: {e.Message}");
                return null;
            }
        }

        private static void DestroyModel(VrmModel m)
        {
            if (m == null) return;
            if (m.Template != null) UnityEngine.Object.Destroy(m.Template);
            foreach (var a in m.Assets)
                if (a != null) UnityEngine.Object.Destroy(a);
            m.Assets.Clear();
        }
    }
}
