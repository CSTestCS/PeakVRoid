using BepInEx.Configuration;
using UnityEngine;

namespace PeakVRoid
{
    public enum ScaleMode
    {
        /// <summary>Same height as the scout; hips are raised so the feet still touch the ground.</summary>
        MatchHeight,
        /// <summary>Legs as long as the scout's (the avatar ends up smaller).</summary>
        MatchLegs,
        /// <summary>Use the VRM's real size (times ScaleMultiplier).</summary>
        Original,
    }

    public static class ModConfig
    {
        // Avatar
        public static ConfigEntry<bool> AvatarEnabled;
        public static ConfigEntry<string> AvatarFile;
        public static ConfigEntry<ScaleMode> Scale;
        public static ConfigEntry<float> AvatarScale;
        public static ConfigEntry<float> HeightOffset;
        public static ConfigEntry<bool> RemotePlayerAvatars;
        public static ConfigEntry<bool> ShowMyAvatarOnEveryone;
        public static ConfigEntry<bool> LimbIK;
        public static ConfigEntry<bool> Fingers;
        public static ConfigEntry<bool> SpringBones;
        public static ConfigEntry<bool> AutoBlink;
        public static ConfigEntry<bool> HideHeadInFirstPerson;

        // Materials
        public static ConfigEntry<bool> Unlit;
        public static ConfigEntry<float> ShadeLift;
        public static ConfigEntry<string> ShaderOverride;

        // Camera
        public static ConfigEntry<bool> ThirdPersonOnStart;
        public static ConfigEntry<float> CameraDistance;
        public static ConfigEntry<float> CameraHeight;
        public static ConfigEntry<float> ShoulderOffset;
        public static ConfigEntry<float> MinDistance;
        public static ConfigEntry<float> MaxDistance;
        public static ConfigEntry<bool> CameraCollision;

        // Keys
        public static ConfigEntry<KeyCode> ToggleViewKey;
        public static ConfigEntry<KeyCode> ToggleAvatarKey;
        public static ConfigEntry<KeyCode> NextAvatarKey;
        public static ConfigEntry<KeyCode> ReloadKey;
        public static ConfigEntry<KeyCode> ZoomInKey;
        public static ConfigEntry<KeyCode> ZoomOutKey;
        public static ConfigEntry<KeyCode> SwapShoulderKey;

        public static void Bind(ConfigFile c)
        {
            AvatarEnabled = c.Bind("Avatar", "Enabled", true, "Replace your scout with your VRM avatar.");
            AvatarFile = c.Bind("Avatar", "File", "",
                "VRM to use. A file name inside the avatar folder (BepInEx/config/PeakVRoid), or a full path. " +
                "Leave empty to use the first .vrm in the avatar folder. The Next Avatar key cycles through the folder.");
            Scale = c.Bind("Avatar", "ScaleMode", ScaleMode.MatchHeight,
                "How the avatar is sized against the scout. MatchHeight = as tall as the scout (recommended), " +
                "MatchLegs = legs as long as the scout's (smaller avatar), Original = the VRM's real size.");
            AvatarScale = c.Bind("Avatar", "ScaleMultiplier", 1f,
                new ConfigDescription("Extra size multiplier applied after ScaleMode.", new AcceptableValueRange<float>(0.1f, 5f)));
            HeightOffset = c.Bind("Avatar", "HeightOffset", 0f,
                new ConfigDescription("Raise (+) or lower (-) the avatar in metres if its feet float or sink.", new AcceptableValueRange<float>(-1f, 1f)));
            RemotePlayerAvatars = c.Bind("Avatar", "OtherPlayersAvatars", true,
                "Show other players as VRMs too, on YOUR screen, if you have a file named after them in BepInEx/config/PeakVRoid/players " +
                "(e.g. players/SteamName.vrm). Avatars are not sent over the network.");
            ShowMyAvatarOnEveryone = c.Bind("Avatar", "UseMyAvatarForEveryone", false,
                "Silly mode: every scout without their own players/ file uses your avatar.");
            LimbIK = c.Bind("Avatar", "LimbIK", true,
                "Bend the avatar's arms and legs so its hands and feet land exactly on the scout's (held items, climbing grips, " +
                "feet on the ground). Also fixes A-pose/T-pose differences.");
            Fingers = c.Bind("Avatar", "Fingers", true, "Copy finger curls from the scout to the avatar.");
            SpringBones = c.Bind("Avatar", "SpringBones", true, "Simulate VRM hair/clothes physics.");
            AutoBlink = c.Bind("Avatar", "AutoBlink", true, "Blink automatically using the VRM's blink expression.");
            HideHeadInFirstPerson = c.Bind("Avatar", "HideHeadInFirstPerson", true,
                "Hide your avatar's head (and hair) in first person so it doesn't block the view.");

            Unlit = c.Bind("Materials", "Unlit", false, "Render the avatar unlit (flat colours, ignores PEAK's lighting).");
            ShadeLift = c.Bind("Materials", "ShadeLift", 0.15f,
                new ConfigDescription("Brightens shadowed areas to mimic VRM toon shading (lit mode only). 0 = off.", new AcceptableValueRange<float>(0f, 1f)));
            ShaderOverride = c.Bind("Materials", "ShaderOverride", "", "Advanced: name of a shader to use instead of URP Lit/Unlit.");

            ThirdPersonOnStart = c.Bind("Camera", "ThirdPersonByDefault", true, "Start in third person.");
            CameraDistance = c.Bind("Camera", "Distance", 2.6f,
                new ConfigDescription("Third-person camera distance behind the head (metres).", new AcceptableValueRange<float>(0.5f, 12f)));
            CameraHeight = c.Bind("Camera", "Height", 0.35f,
                new ConfigDescription("Third-person camera height above the head.", new AcceptableValueRange<float>(-1f, 3f)));
            ShoulderOffset = c.Bind("Camera", "ShoulderOffset", 0.45f,
                new ConfigDescription("Over-the-shoulder sideways offset (0 = centred).", new AcceptableValueRange<float>(-2f, 2f)));
            MinDistance = c.Bind("Camera", "MinDistance", 0.8f, "Closest zoom.");
            MaxDistance = c.Bind("Camera", "MaxDistance", 8f, "Farthest zoom.");
            CameraCollision = c.Bind("Camera", "Collision", true, "Pull the camera in front of walls and rocks.");

            ToggleViewKey = c.Bind("Keys", "ToggleView", KeyCode.F5, "Switch between first and third person.");
            ToggleAvatarKey = c.Bind("Keys", "ToggleAvatar", KeyCode.F7, "Show/hide your VRM (shows the normal scout).");
            NextAvatarKey = c.Bind("Keys", "NextAvatar", KeyCode.F8, "Cycle to the next .vrm in the avatar folder.");
            ReloadKey = c.Bind("Keys", "ReloadAvatars", KeyCode.F6, "Reload VRM files from disk (after editing them).");
            ZoomInKey = c.Bind("Keys", "ZoomIn", KeyCode.PageUp, "Move the third-person camera closer.");
            ZoomOutKey = c.Bind("Keys", "ZoomOut", KeyCode.PageDown, "Move the third-person camera farther.");
            SwapShoulderKey = c.Bind("Keys", "SwapShoulder", KeyCode.F9, "Swap the over-the-shoulder side.");
        }
    }
}
