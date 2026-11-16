using BepInEx;
using BepInEx.Logging;
using PeakVRoid.Avatar;
using PeakVRoid.Game;
using UnityEngine;

namespace PeakVRoid
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private static string toast;
        private static float toastUntil;

        private AvatarManager avatars;
        private GUIStyle toastStyle;

        private void Awake()
        {
            Log = Logger;
            ModConfig.Bind(Config);

            // Keep our objects alive across scene loads, separate from the plugin manager object.
            var root = new GameObject("PeakVRoid");
            DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;

            Peak.Init();
            avatars = new AvatarManager(root.transform);
            ThirdPersonCamera.Init();

            Log.LogInfo($"PeakVRoid {MyPluginInfo.PLUGIN_VERSION} loaded. Avatar folder: {AvatarManager.AvatarFolder}");
            if (AvatarManager.AvatarFiles().Length == 0)
                Log.LogWarning("No .vrm files found yet. Put one in " + AvatarManager.AvatarFolder);
        }

        private void Update()
        {
            if (Keys.Down(ModConfig.ToggleAvatarKey.Value)) avatars.ToggleAvatar();
            if (Keys.Down(ModConfig.NextAvatarKey.Value)) avatars.NextAvatar();
            if (Keys.Down(ModConfig.ReloadKey.Value)) avatars.Reload();
            ThirdPersonCamera.Update();
            avatars.Update();
        }

        private void OnDestroy()
        {
            ThirdPersonCamera.Shutdown();
        }

        public static void Toast(string message)
        {
            toast = message;
            toastUntil = Time.unscaledTime + 4f;
            Log?.LogInfo(message);
        }

        private void OnGUI()
        {
            if (toast == null || Time.unscaledTime > toastUntil) return;
            if (toastStyle == null)
            {
                toastStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 18,
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = true,
                };
                toastStyle.normal.textColor = Color.white;
            }
            float w = Mathf.Min(720f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - w) / 2f, 24f, w, 44f), "PeakVRoid: " + toast, toastStyle);
        }
    }
}
