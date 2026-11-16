# Changelog

## 1.0.0
- First release.
- Load any VRM 0.x or VRM 1.0 avatar (VRoid Studio exports, Booth models, etc.) at runtime, no Unity needed.
- Avatar follows the scout's animation, ragdoll and climbing; two-bone IK keeps hands on grips/items and feet on the ground.
- VRM spring bones (hair/clothes physics) with colliders, automatic blinking, first-person head hiding.
- Third-person camera (F5) with wall collision, zoom and shoulder swap; rendering-only offset so game logic is untouched.
- Optional local avatars for friends (`players/<SteamName>.vrm`).
