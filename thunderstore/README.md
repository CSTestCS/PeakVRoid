# PeakVRoid

Climb PEAK as **your own VRM avatar** — and actually see them, with a **third-person camera**.

* Works with **VRM 0.x and VRM 1.0** files: VRoid Studio exports, Booth / VRoid Hub downloads, anything that exports to `.vrm`.
* Your avatar copies the scout's every move: walking, climbing, ragdolling, passing out, emotes.
  Two-bone IK puts its hands on the scout's grips and held items and its feet on the ground.
* **Hair & clothes physics** (VRM spring bones), **automatic blinking**, head hidden in first person.
* **Third person** (F5) with wall collision, zoom and over-the-shoulder swap.
* No Unity, no asset bundles, no conversion — just drop the `.vrm` in a folder.

## Install

**Mod manager (recommended):** install *PeakVRoid* with r2modman / Thunderstore Mod Manager / Gale. BepInEx is installed automatically.

**Manual / one-click:** download `PeakVRoid-manual.zip` from the GitHub releases, extract it anywhere and double-click
**`Install PeakVRoid.bat`**. It finds PEAK through Steam, installs BepInEx if needed, installs the mod and asks you for your `.vrm`.

## Add your avatar

1. Start the game once with the mod (this creates the folder).
2. Put your `.vrm` file into **`BepInEx/config/PeakVRoid/`**
   * r2modman / Gale: *Settings → Browse profile folder*, then `BepInEx/config/PeakVRoid`.
   * Manual: `<Steam>/steamapps/common/PEAK/BepInEx/config/PeakVRoid`.
3. Press **F6** in game (or restart). Several avatars? **F8** cycles through them.

## Controls

| Key | Action |
| --- | --- |
| F5 | First / third person |
| PageUp / PageDown | Zoom third-person camera |
| F9 | Swap shoulder |
| F6 | Reload settings (.cfg) and avatar files |
| F7 | Avatar on / off |
| F8 | Next avatar in the folder |

All keys and options are in `BepInEx/config/com.peakvroid.peakvroid.cfg` (or your mod manager's config editor).

## Multiplayer

Avatars are **client-side**: everyone else sees your normal scout unless they also run PeakVRoid and have your file.
To see a friend as their avatar, put their file in `BepInEx/config/PeakVRoid/players/<their Steam name>.vrm`.

## Good to know

* Materials use PEAK's own lit shader to approximate VRM's toon (MToon) look. Try `[Materials] Unlit = true` for a flat anime look,
  or tune `ShadeLift`.
* If your avatar floats or sinks, adjust `[Avatar] HeightOffset`. `ScaleMode` chooses between scout height, scout leg length or the VRM's real size.
* Respect your avatar's license (VRM files carry their own usage terms).
