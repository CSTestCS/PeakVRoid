# Changelog

## 1.0.5
- Height matching measures the avatar from the vertices attached to its head and legs (stray meshes no longer inflate it) and the scout from its on-screen size; the hip lift uses the scout's real leg length.
- Log lines with all measurements and the avatar's final on-screen height.
- Backpack patch finds the handler's fields by type, for newer game versions.

## 1.0.4
- Fixed avatars coming out smaller than the scout: the scout is now measured through its bones, so MatchHeight really matches.
- F6 now also reloads the .cfg file, so settings edited while the game runs apply immediately.

## 1.0.3
- New `[Avatar] ArmMode`: `ReachScoutHands` (default), `MatchShoulders` (arms closer to the body) or `CopyRotation` (no arm IK). Can be changed while playing.
- The backpack you wear is now shown on the avatar's back, and your own backpack is visible in third person.
  Tune with `ShowBackpack`, `BackpackDistance`, `BackpackHeight`.

## 1.0.2
- Avatars are now scaled (uniformly) to exactly the default scout's full height, measured feet to top of head.
- Knees bend forward instead of bowing outward; feet are placed under the avatar's own hips instead of the scout's wide stance.

## 1.0.1
- Installer: no longer crashes when Steam lists a library on a drive that doesn't exist anymore.

## 1.0.0
- First release.
- Load any VRM 0.x or VRM 1.0 avatar (VRoid Studio exports, Booth models, etc.) at runtime, no Unity needed.
- Avatar follows the scout's animation, ragdoll and climbing; two-bone IK keeps hands on grips/items and feet on the ground.
- VRM spring bones (hair/clothes physics) with colliders, automatic blinking, first-person head hiding.
- Third-person camera (F5) with wall collision, zoom and shoulder swap; rendering-only offset so game logic is untouched.
- Optional local avatars for friends (`players/<SteamName>.vrm`).
