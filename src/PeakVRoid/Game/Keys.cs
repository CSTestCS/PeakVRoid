using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PeakVRoid.Game
{
    /// <summary>
    /// Key polling that works whether the game uses the legacy Input Manager or the new
    /// Input System (PEAK uses the latter, where UnityEngine.Input throws). The Input
    /// System is reached through reflection so no extra reference is needed.
    /// </summary>
    public static class Keys
    {
        private static bool legacyBroken;
        private static PropertyInfo keyboardCurrent;
        private static PropertyInfo itemByKey;
        private static Type keyEnum;
        private static PropertyInfo wasPressed;
        private static PropertyInfo isPressed;
        private static bool inputSystemTried;
        private static readonly Dictionary<KeyCode, object> keyCache = new Dictionary<KeyCode, object>();

        public static bool Down(KeyCode key) => Query(key, true);
        public static bool Held(KeyCode key) => Query(key, false);

        private static bool Query(KeyCode key, bool pressedThisFrame)
        {
            if (key == KeyCode.None) return false;
            if (!legacyBroken)
            {
                try { return pressedThisFrame ? Input.GetKeyDown(key) : Input.GetKey(key); }
                catch (InvalidOperationException) { legacyBroken = true; }
                catch (Exception) { legacyBroken = true; }
            }
            return QueryInputSystem(key, pressedThisFrame);
        }

        private static bool QueryInputSystem(KeyCode key, bool pressedThisFrame)
        {
            if (!inputSystemTried)
            {
                inputSystemTried = true;
                var keyboard = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem");
                keyEnum = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem");
                var button = Type.GetType("UnityEngine.InputSystem.Controls.ButtonControl, Unity.InputSystem");
                if (keyboard != null && keyEnum != null && button != null)
                {
                    keyboardCurrent = keyboard.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                    itemByKey = keyboard.GetProperty("Item", new[] { keyEnum });
                    wasPressed = button.GetProperty("wasPressedThisFrame");
                    isPressed = button.GetProperty("isPressed");
                }
                if (itemByKey == null) Plugin.Log.LogWarning("No usable input API found; hotkeys are disabled.");
            }
            if (itemByKey == null) return false;
            var kb = keyboardCurrent.GetValue(null);
            if (kb == null) return false;
            if (!keyCache.TryGetValue(key, out var k))
            {
                k = MapKey(key);
                keyCache[key] = k;
            }
            if (k == null) return false;
            try
            {
                var control = itemByKey.GetValue(kb, new[] { k });
                if (control == null) return false;
                return (bool)(pressedThisFrame ? wasPressed : isPressed).GetValue(control);
            }
            catch { return false; }
        }

        /// <summary>Translate a legacy KeyCode into the Input System's Key enum by name.</summary>
        private static object MapKey(KeyCode key)
        {
            string n = key.ToString();
            if (n.StartsWith("Alpha", StringComparison.Ordinal)) n = "Digit" + n.Substring(5);
            else if (n.StartsWith("Keypad", StringComparison.Ordinal))
                n = "Numpad" + n.Substring(6);
            else
            {
                switch (key)
                {
                    case KeyCode.Return: n = "Enter"; break;
                    case KeyCode.LeftControl: n = "LeftCtrl"; break;
                    case KeyCode.RightControl: n = "RightCtrl"; break;
                    case KeyCode.LeftAlt: n = "LeftAlt"; break;
                    case KeyCode.RightAlt: n = "RightAlt"; break;
                    case KeyCode.BackQuote: n = "Backquote"; break;
                    case KeyCode.Equals: n = "Equals"; break;
                    case KeyCode.LeftBracket: n = "LeftBracket"; break;
                    case KeyCode.RightBracket: n = "RightBracket"; break;
                }
            }
            try { return Enum.Parse(keyEnum, n, true); }
            catch
            {
                Plugin.Log.LogWarning($"Key {key} is not supported by the Input System.");
                return null;
            }
        }
    }
}
