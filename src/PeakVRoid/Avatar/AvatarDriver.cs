using System.Collections.Generic;
using PeakVRoid.Game;
using PeakVRoid.Vrm;
using UnityEngine;

namespace PeakVRoid.Avatar
{
    /// <summary>
    /// Makes one VRM instance mimic one PEAK character every frame. Rotations are copied
    /// from the scout's animated bodyparts relative to both rigs' rest poses, limbs are
    /// then re-aimed along the scout's limb directions, followed by spring bones, blinking
    /// and first-person head hiding.
    /// </summary>
    public sealed class AvatarDriver : MonoBehaviour
    {
        // VRM humanoid bone ← PEAK BodypartType, in parent-before-child order.
        private static readonly (string vrm, string game)[] BoneMap =
        {
            ("hips", "Hip"), ("spine", "Mid"), ("chest", "Torso"), ("neck", "Neck"), ("head", "Head"),
            ("leftShoulder", "Shoulder_L"), ("leftUpperArm", "Arm_L"), ("leftLowerArm", "Elbow_L"), ("leftHand", "Hand_L"),
            ("rightShoulder", "Shoulder_R"), ("rightUpperArm", "Arm_R"), ("rightLowerArm", "Elbow_R"), ("rightHand", "Hand_R"),
            ("leftUpperLeg", "Leg_L"), ("leftLowerLeg", "Knee_L"), ("leftFoot", "Foot_L"), ("leftToes", "Toe_L"),
            ("rightUpperLeg", "Leg_R"), ("rightLowerLeg", "Knee_R"), ("rightFoot", "Foot_R"), ("rightToes", "Toe_R"),
        };

        // Two-bone limbs solved with IK so the avatar's hands and feet land on the scout's:
        // (vrm upper, vrm lower, vrm end, game upper, game lower, game end).
        private static readonly (string, string, string, string, string, string)[] Limbs =
        {
            ("leftUpperArm", "leftLowerArm", "leftHand", "Arm_L", "Elbow_L", "Hand_L"),
            ("rightUpperArm", "rightLowerArm", "rightHand", "Arm_R", "Elbow_R", "Hand_R"),
            ("leftUpperLeg", "leftLowerLeg", "leftFoot", "Leg_L", "Knee_L", "Foot_L"),
            ("rightUpperLeg", "rightLowerLeg", "rightFoot", "Leg_R", "Knee_R", "Foot_R"),
        };

        private static readonly string[] FingerNames = { "Thumb", "Index", "Middle", "Ring", "Little" };
        private static readonly string[] ThumbSegments = { "Metacarpal", "Proximal", "Distal" };
        private static readonly string[] FingerSegments = { "Proximal", "Intermediate", "Distal" };

        private sealed class Map
        {
            public Transform Vrm;
            public Transform Game;
            public Quaternion Offset;
            // IK (set on the upper bone of a limb): the lower bone, the end bone and the scout's end/pole.
            public Map Lower;
            public Transform VrmEnd;
            public Transform GameMid;
            public Transform GameEnd;
            public bool IsLower;
            public Vector3 Target;
        }

        public Component Character { get; private set; }
        public VrmModel Model { get; private set; }
        public bool IsLocal => Character != null && Character == Peak.LocalCharacter;

        private Transform[] t;
        private readonly List<Map> maps = new List<Map>();
        private Map hipsMap;
        private Transform head;
        private Vector3 headScale = Vector3.one;
        private float scale = 1f;
        private float hipLift; // metres to add along the body's up axis
        private Vector3 hipsRestInRoot;
        private Quaternion rootOffset; // smr rotation → VRM root rotation
        private Vector3 gameUpInHip;
        private SkinnedMeshRenderer scoutMain;
        private List<Renderer> scoutRenderers = new List<Renderer>();
        private readonly List<Renderer> thirdPersonOnly = new List<Renderer>();
        private readonly List<Renderer> firstPersonOnly = new List<Renderer>();
        private Renderer[] ownRenderers;
        private SpringBones springs;
        private Blinker blinker;
        private Vector3 lastHips;
        private bool resetSprings = true;
        private float nextScoutRefresh;
        private bool visible = true;

        public bool Ready { get; private set; }

        public static AvatarDriver Create(VrmModel model, Component character, Transform parent)
        {
            var go = Instantiate(model.Template, parent, false);
            go.name = "PeakVRoid " + model.Title + " (" + Peak.CharacterName(character) + ")";
            var driver = go.AddComponent<AvatarDriver>();
            driver.Character = character;
            driver.Model = model;
            driver.Setup();
            return driver;
        }

        private void Setup()
        {
            t = GetComponentsInChildren<Transform>(true);
            ownRenderers = GetComponentsInChildren<Renderer>(true);

            scoutMain = Peak.MainRenderer(Character);
            if (scoutMain == null)
            {
                Plugin.Log.LogWarning($"{Character.name}: no scout mesh found; cannot attach avatar.");
                return;
            }
            var parts = Peak.Bodyparts(Character);
            // The visible mesh's own bones are the ground truth for what the scout looks like;
            // prefer them (matched by name) and use them for extras such as fingers.
            foreach (var b in scoutMain.bones)
                if (b != null) parts[b.name] = b;
            if (!parts.ContainsKey("Hip"))
            {
                Plugin.Log.LogWarning($"{Character.name}: scout has no Hip bodypart; cannot attach avatar.");
                return;
            }

            // Rest pose of the scout (in its mesh space) from the skin bind poses.
            var bindIndex = new Dictionary<Transform, int>();
            var smrBones = scoutMain.bones;
            for (int i = 0; i < smrBones.Length; i++)
                if (smrBones[i] != null && !bindIndex.ContainsKey(smrBones[i])) bindIndex[smrBones[i]] = i;
            var byName = new Dictionary<string, int>();
            for (int i = 0; i < smrBones.Length; i++)
                if (smrBones[i] != null && !byName.ContainsKey(smrBones[i].name)) byName[smrBones[i].name] = i;
            var bindposes = scoutMain.sharedMesh != null ? scoutMain.sharedMesh.bindposes : new Matrix4x4[0];
            Quaternion smrInv = Quaternion.Inverse(scoutMain.transform.rotation);

            bool GameRest(Transform g, out Quaternion rot, out Vector3 pos)
            {
                int i;
                if (!bindIndex.TryGetValue(g, out i) && !byName.TryGetValue(g.name, out i)) i = -1;
                if (i >= 0 && i < bindposes.Length)
                {
                    var m = bindposes[i].inverse;
                    rot = m.rotation;
                    pos = m.GetColumn(3);
                    return true;
                }
                // Not skinned: use the current pose instead.
                rot = smrInv * g.rotation;
                pos = scoutMain.transform.InverseTransformPoint(g.position);
                return false;
            }

            // VRM rest pose (template is at the origin, unrotated, unscaled).
            Transform V(string bone) => Model.HumanBones.TryGetValue(bone, out int ix) ? t[ix] : null;
            Quaternion vInv = Quaternion.Inverse(transform.rotation);
            Vector3 VPos(Transform b) => transform.InverseTransformPoint(b.position);

            // Facing frames of both rigs so differently-oriented exports line up.
            Vector3 GPos(string part) { GameRest(parts[part], out _, out var p); return p; }
            Vector3 gUp = parts.ContainsKey("Head") ? GPos("Head") - GPos("Hip") : Vector3.up;
            Vector3 gRight = parts.ContainsKey("Arm_R") && parts.ContainsKey("Arm_L") ? GPos("Arm_R") - GPos("Arm_L")
                : parts.ContainsKey("Leg_R") && parts.ContainsKey("Leg_L") ? GPos("Leg_R") - GPos("Leg_L") : Vector3.right;
            Quaternion fg = Frame(gRight, gUp);

            var vHips = V("hips");
            var vHead = V("head") ?? V("neck") ?? V("chest") ?? V("spine");
            var vUp = vHead != null ? VPos(vHead) - VPos(vHips) : Vector3.up;
            var vl = V("leftUpperArm"); var vr = V("rightUpperArm");
            var vRight = vl != null && vr != null ? VPos(vr) - VPos(vl) : Vector3.right;
            Quaternion fv = Frame(vRight, vUp);
            Quaternion frame = fg * Quaternion.Inverse(fv);
            rootOffset = frame;

            // Scale.
            var gHipRestPos = GPos("Hip");
            float gameScale = scoutMain.transform.lossyScale.y;
            Vector3 gUpN = gUp.normalized;
            float gameLeg = FootDrop(parts, gHipRestPos, gUpN, GPos) * gameScale;
            float gameHeight = parts.ContainsKey("Head") ? (Vector3.Dot(GPos("Head") - gHipRestPos, gUpN)) * gameScale + gameLeg : gameLeg * 2f;
            Vector3 vUpN = vUp.normalized;
            float vrmLeg = VrmFootDrop(V, VPos, VPos(vHips), vUpN);
            float vrmHeight = vHead != null ? Vector3.Dot(VPos(vHead) - VPos(vHips), vUpN) + vrmLeg : vrmLeg * 2f;
            switch (ModConfig.Scale.Value)
            {
                case ScaleMode.MatchLegs: scale = vrmLeg > 0.01f && gameLeg > 0.01f ? gameLeg / vrmLeg : 1f; break;
                case ScaleMode.MatchHeight: scale = vrmHeight > 0.01f && gameHeight > 0.01f ? gameHeight / vrmHeight : 1f; break;
                default: scale = 1f; break;
            }
            scale *= ModConfig.AvatarScale.Value;
            hipLift = vrmLeg * scale - gameLeg + ModConfig.HeightOffset.Value;
            hipsRestInRoot = VPos(vHips);

            // Bone mappings.
            void Add(string vrmBone, string gameBone)
            {
                var vb = V(vrmBone);
                if (vb == null || !parts.TryGetValue(gameBone, out var gb) || gb == null) return;
                GameRest(gb, out var gr, out _);
                Quaternion vrest = vInv * vb.rotation;
                var map = new Map { Vrm = vb, Game = gb, Offset = Quaternion.Inverse(gr) * frame * vrest };
                maps.Add(map);
                if (vrmBone == "hips") hipsMap = map;
            }
            foreach (var (vrm, game) in BoneMap) Add(vrm, game);
            if (ModConfig.Fingers.Value)
            {
                foreach (var side in new[] { "L", "R" })
                    for (int f = 0; f < 5; f++)
                        for (int s = 0; s < 3; s++)
                        {
                            string vb = (side == "L" ? "left" : "right") + FingerNames[f] + (f == 0 ? ThumbSegments[s] : FingerSegments[s]);
                            Add(vb, $"Finger_{f + 1}_{s + 1}_{side}");
                        }
            }
            if (hipsMap == null)
            {
                Plugin.Log.LogWarning($"{Character.name}: could not map hips; avatar disabled.");
                return;
            }
            GameRest(hipsMap.Game, out var hipRest, out _);
            gameUpInHip = Quaternion.Inverse(hipRest) * gUpN;

            if (ModConfig.LimbIK.Value)
            {
                foreach (var (vu, vlow, ve, gu, gl, ge) in Limbs)
                {
                    var up = maps.Find(x => x.Vrm == V(vu));
                    var low = maps.Find(x => x.Vrm == V(vlow));
                    if (up == null || low == null || V(ve) == null) continue;
                    if (!parts.TryGetValue(gl, out var gmid) || !parts.TryGetValue(ge, out var gend)) continue;
                    up.Lower = low;
                    up.VrmEnd = V(ve);
                    up.GameMid = gmid;
                    up.GameEnd = gend;
                    low.IsLower = true;
                    low.VrmEnd = V(ve);
                }
            }

            head = V("head");
            if (head != null) headScale = head.localScale;

            foreach (int ix in Model.ThirdPersonOnly) if (t[ix].TryGetComponent<Renderer>(out var r)) thirdPersonOnly.Add(r);
            foreach (int ix in Model.FirstPersonOnly) if (t[ix].TryGetComponent<Renderer>(out var r)) firstPersonOnly.Add(r);

            int layer = scoutMain.gameObject.layer;
            foreach (var tr in t) tr.gameObject.layer = layer;

            springs = new SpringBones(Model, t);
            blinker = new Blinker(Model, t);

            transform.localScale = Vector3.one * scale;
            gameObject.SetActive(true);
            Ready = true;
            Plugin.Log.LogInfo($"Avatar '{Model.Title}' attached to {Peak.CharacterName(Character)}: {maps.Count} bones, " +
                               $"{springs.Count} spring joints, scale {scale:0.00} (scout legs {gameLeg:0.00} m, VRM legs {vrmLeg:0.00} m)");
            Apply();
        }

        private static Quaternion Frame(Vector3 right, Vector3 up)
        {
            var fwd = Vector3.Cross(right.normalized, up.normalized);
            if (fwd.sqrMagnitude < 1e-6f) return Quaternion.identity;
            return Quaternion.LookRotation(fwd, up);
        }

        private static float FootDrop(Dictionary<string, Transform> parts, Vector3 hip, Vector3 up, System.Func<string, Vector3> pos)
        {
            float sum = 0; int n = 0;
            foreach (var f in new[] { "Foot_L", "Foot_R" })
                if (parts.ContainsKey(f)) { sum += Vector3.Dot(hip - pos(f), up); n++; }
            return n > 0 ? sum / n : 0.5f;
        }

        private static float VrmFootDrop(System.Func<string, Transform> v, System.Func<Transform, Vector3> pos, Vector3 hips, Vector3 up)
        {
            float sum = 0; int n = 0;
            foreach (var f in new[] { "leftFoot", "rightFoot" })
            {
                var b = v(f);
                if (b != null) { sum += Vector3.Dot(hips - pos(b), up); n++; }
            }
            return n > 0 ? sum / n : 0.8f;
        }

        // ------------------------------------------------------------------ per frame

        private void LateUpdate()
        {
            if (!Ready) return;
            if (Character == null || scoutMain == null)
            {
                Destroy(gameObject);
                return;
            }

            // Mirror the scout's own visibility (it is hidden when passed out in some states, in menus, etc.).
            bool shouldShow = Character.gameObject.activeInHierarchy && scoutMain.enabled && scoutMain.gameObject.activeInHierarchy;
            if (shouldShow != visible)
            {
                visible = shouldShow;
                foreach (var r in ownRenderers) if (r != null) r.enabled = visible;
                resetSprings = true;
            }

            HideScout(true);
            Apply();
        }

        private void Apply()
        {
            if (head != null) head.localScale = headScale;

            Quaternion smrRot = scoutMain.transform.rotation;
            transform.rotation = smrRot * rootOffset;
            transform.localScale = Vector3.one * scale;

            // Hips position: scout hip, shifted along the body's up so feet line up.
            Vector3 up = hipsMap.Game.rotation * gameUpInHip;
            Vector3 hipsPos = hipsMap.Game.position + up * hipLift;
            transform.position = hipsPos - transform.rotation * (hipsRestInRoot * scale);

            hipsMap.Vrm.position = hipsPos;
            for (int i = 0; i < maps.Count; i++)
            {
                var m = maps[i];
                if (m.Game == null) continue;
                // Copy the scout's rotation (this also carries the twist of limbs).
                m.Vrm.rotation = m.Game.rotation * m.Offset;
                if (m.Lower != null && m.GameEnd != null) SolveLimb(m);
                else if (m.IsLower) Aim(m.Vrm, m.VrmEnd, m.Target);
            }

            if (springs != null && ModConfig.SpringBones.Value)
            {
                if ((hipsPos - lastHips).sqrMagnitude > 4f) resetSprings = true; // teleported
                if (resetSprings) { springs.Reset(); resetSprings = false; }
                springs.Update(Time.deltaTime, scale);
            }
            lastHips = hipsPos;

            if (ModConfig.AutoBlink.Value) blinker?.Update(Time.deltaTime);

            bool firstPerson = IsLocal && !ThirdPersonCamera.Active;
            if (firstPerson && ModConfig.HideHeadInFirstPerson.Value && head != null) head.localScale = Vector3.one * 1e-4f;
            foreach (var r in thirdPersonOnly) if (r != null) r.forceRenderingOff = firstPerson;
            foreach (var r in firstPersonOnly) if (r != null) r.forceRenderingOff = !firstPerson;
        }

        /// <summary>
        /// Two-bone IK: bend the avatar's limb so its end reaches the scout's hand/foot, with the
        /// elbow/knee pointing the way the scout's does. Unreachable targets get a straight limb.
        /// </summary>
        private void SolveLimb(Map up)
        {
            var low = up.Lower;
            Vector3 a = up.Vrm.position;
            Vector3 target = up.GameEnd.position;
            float l1 = (low.Vrm.position - a).magnitude;
            float l2 = (up.VrmEnd.position - low.Vrm.position).magnitude;
            Vector3 toTarget = target - a;
            float d = toTarget.magnitude;
            if (l1 < 1e-5f || l2 < 1e-5f || d < 1e-5f) return;
            Vector3 dir = toTarget / d;
            d = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 1e-4f, (l1 + l2) * 0.9999f);

            // Bend direction: towards the scout's elbow/knee, falling back to the avatar's current bend.
            Vector3 bend = Vector3.ProjectOnPlane(up.GameMid.position - up.Game.position, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(low.Vrm.position - a, dir);
            if (bend.sqrMagnitude < 1e-8f) bend = Vector3.ProjectOnPlane(up.Vrm.rotation * Vector3.forward, dir);
            bend.Normalize();

            float cos = Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2f * l1 * d), -1f, 1f);
            float sin = Mathf.Sqrt(1f - cos * cos);
            Vector3 mid = a + dir * (l1 * cos) + bend * (l1 * sin);

            Aim(up.Vrm, low.Vrm, mid);
            low.Target = a + dir * d; // the lower bone aims at the (clamped) target once it is processed
        }

        private static void Aim(Transform bone, Transform child, Vector3 point)
        {
            if (bone == null || child == null) return;
            Vector3 from = child.position - bone.position;
            Vector3 to = point - bone.position;
            if (from.sqrMagnitude > 1e-10f && to.sqrMagnitude > 1e-10f)
                bone.rotation = Quaternion.FromToRotation(from, to) * bone.rotation;
        }

        // ------------------------------------------------------------------ scout visibility

        private void HideScout(bool hide)
        {
            if (Time.unscaledTime >= nextScoutRefresh || !hide)
            {
                nextScoutRefresh = Time.unscaledTime + 1f;
                scoutRenderers = Peak.ScoutRenderers(Character);
            }
            foreach (var r in scoutRenderers)
                if (r != null && r.forceRenderingOff != hide && !IsOwn(r)) r.forceRenderingOff = hide;
        }

        private bool IsOwn(Renderer r) => r.transform.IsChildOf(transform);

        private void OnDestroy()
        {
            if (Character != null) HideScout(false);
        }
    }
}
