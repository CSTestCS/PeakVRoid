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
            public bool IsLeg;
            public bool IsArm;
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
        private Vector3 vrmForwardInHips = Vector3.forward;
        private Vector3 bodyUp = Vector3.up;
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

        // Backpack: the scout's worn-backpack visuals, moved onto the avatar's back while rendering.
        private readonly List<Transform> backpackRoots = new List<Transform>();
        private readonly List<Vector3> backpackSaved = new List<Vector3>();
        private Transform scoutTorso, scoutArmL, scoutArmR;
        private Transform vrmBack, vrmArmL, vrmArmR;
        private bool backpackMoved;
        private float nextBackpackScan;
        private float attachTime;
        private float targetHeight;
        private bool sizeLogged;

        private static readonly HashSet<AvatarDriver> Live = new HashSet<AvatarDriver>();
        private static bool renderHooked;

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
            // Mesh units → metres. A skinned mesh is drawn through its bones, so the real scale is
            // bone.localToWorld * bindpose (the renderer object's own scale doesn't matter and can be wrong,
            // e.g. when the armature is scaled).
            float gameScale = scoutMain.transform.lossyScale.y;
            {
                int hi;
                var hipT = parts["Hip"];
                if ((bindIndex.TryGetValue(hipT, out hi) || byName.TryGetValue(hipT.name, out hi)) && hi < bindposes.Length && smrBones[hi] != null)
                {
                    var m = smrBones[hi].localToWorldMatrix * bindposes[hi];
                    float s = (((Vector3)m.GetColumn(0)).magnitude + ((Vector3)m.GetColumn(1)).magnitude + ((Vector3)m.GetColumn(2)).magnitude) / 3f;
                    if (s > 1e-5f) gameScale = s;
                }
            }
            Vector3 gUpN = gUp.normalized;
            float gameLeg = FootDrop(parts, gHipRestPos, gUpN, GPos) * gameScale;
            // Full visible height (soles to top of head) of both characters, measured from their meshes in the rest pose.
            float gameBindHeight = ScoutHeight(gUpN) * gameScale;
            // What the scout really measures on screen right now (world bounds of its skinned mesh), used when
            // it is standing upright; this is what "same height as the scout" means to a player.
            float gameLiveHeight = 0f;
            GameRest(parts["Hip"], out var hipRestRot, out _);
            Vector3 liveUp = parts["Hip"].rotation * (Quaternion.Inverse(hipRestRot) * gUpN);
            if (scoutMain.enabled && Vector3.Dot(liveUp, Vector3.up) > 0.9f)
            {
                gameLiveHeight = scoutMain.bounds.size.y;
                // Leg length in the world too, so the hips are lifted by the right amount.
                float sum = 0; int n = 0;
                foreach (var f in new[] { "Foot_L", "Foot_R" })
                    if (parts.TryGetValue(f, out var ft)) { sum += parts["Hip"].position.y - ft.position.y; n++; }
                if (n > 0 && sum / n > 0.05f) gameLeg = sum / n;
            }
            float gameHeight = gameLiveHeight > 0.3f ? gameLiveHeight : gameBindHeight;
            if (gameHeight < 0.05f)
                gameHeight = parts.ContainsKey("Head") ? Vector3.Dot(GPos("Head") - gHipRestPos, gUpN) * gameScale + gameLeg : gameLeg * 2f;
            Vector3 vUpN = vUp.normalized;
            float vrmLeg = VrmFootDrop(V, VPos, VPos(vHips), vUpN);
            float vrmBoundsHeight = VrmBoundsHeight(vUpN);
            float vrmHeight = VrmHeight(vUpN, V("head"), V("leftUpperLeg"), V("rightUpperLeg"));
            Plugin.Log.LogInfo($"Size: scout bind {gameBindHeight:0.00} m, on screen {gameLiveHeight:0.00} m (scale {gameScale:0.###}); " +
                               $"VRM body {vrmHeight:0.00}, all meshes {vrmBoundsHeight:0.00}; legs scout {gameLeg:0.00} m, VRM {vrmLeg:0.00}");
            if (vrmHeight < 0.05f)
                vrmHeight = vHead != null ? Vector3.Dot(VPos(vHead) - VPos(vHips), vUpN) + vrmLeg : vrmLeg * 2f;
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
            // The avatar's forward direction expressed in its hips bone, used to bend knees forward.
            vrmForwardInHips = Quaternion.Inverse(vInv * vHips.rotation) * (fv * Vector3.forward);

            if (ModConfig.LimbIK.Value)
            {
                foreach (var (vu, vlow, ve, gu, gl, ge) in Limbs)
                {
                    var up = maps.Find(x => x.Vrm == V(vu));
                    var low = maps.Find(x => x.Vrm == V(vlow));
                    if (up == null || low == null || V(ve) == null) continue;
                    if (!parts.TryGetValue(gl, out var gmid) || !parts.TryGetValue(ge, out var gend)) continue;
                    up.Lower = low;
                    up.IsLeg = vu.EndsWith("UpperLeg");
                    up.IsArm = low.IsArm = vu.EndsWith("UpperArm");
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

            parts.TryGetValue("Torso", out scoutTorso);
            parts.TryGetValue("Arm_L", out scoutArmL);
            parts.TryGetValue("Arm_R", out scoutArmR);
            vrmBack = V("upperChest") ?? V("chest") ?? V("spine");
            vrmArmL = V("leftUpperArm");
            vrmArmR = V("rightUpperArm");
            FindBackpack(true);
            Live.Add(this);
            HookRendering();

            transform.localScale = Vector3.one * scale;
            gameObject.SetActive(true);
            attachTime = Time.unscaledTime;
            targetHeight = gameHeight * ModConfig.AvatarScale.Value;
            Ready = true;
            Plugin.Log.LogInfo($"Avatar '{Model.Title}' attached to {Peak.CharacterName(Character)}: {maps.Count} bones, " +
                               $"{springs.Count} spring joints, scale {scale:0.00} (scout {gameHeight:0.00} m tall, VRM {vrmHeight:0.00} m tall)");
            Apply();
        }

        /// <summary>Height of the scout's body mesh along its up axis, in mesh units (bind pose).</summary>
        private float ScoutHeight(Vector3 up)
        {
            var mesh = scoutMain.sharedMesh;
            if (mesh == null) return 0f;
            return Extent(mesh.bounds, up, Matrix4x4.identity, out _, out _);
        }

        /// <summary>
        /// Height of the avatar along its up axis (model units, rest pose): from the lowest vertex driven by a
        /// leg to the highest vertex driven by the head. Vertices are placed through their own bones, so the
        /// result doesn't depend on how the file stores meshes, and stray objects (props, ground planes,
        /// hidden helpers) can't inflate it.
        /// </summary>
        private float VrmHeight(Vector3 up, Transform headBone, Transform legL, Transform legR)
        {
            float top = float.MinValue, bottom = float.MaxValue;
            var toRoot = transform.worldToLocalMatrix;
            foreach (var r in ownRenderers)
            {
                if (!(r is SkinnedMeshRenderer smr) || smr.sharedMesh == null) continue;
                var mesh = smr.sharedMesh;
                var bones = smr.bones;
                if (bones == null || bones.Length == 0 || !mesh.isReadable) continue;
                var bind = mesh.bindposes;
                var verts = mesh.vertices;
                var weights = mesh.boneWeights;
                if (weights.Length != verts.Length) continue;

                // Per bone: where it is (head / leg / other) and its rest skinning matrix.
                var kind = new int[bones.Length];
                var mats = new Matrix4x4[bones.Length];
                for (int b = 0; b < bones.Length; b++)
                {
                    var bt = bones[b];
                    if (bt == null || b >= bind.Length) { kind[b] = -1; continue; }
                    mats[b] = toRoot * bt.localToWorldMatrix * bind[b];
                    if (headBone != null && bt.IsChildOf(headBone)) kind[b] = 1;
                    else if ((legL != null && bt.IsChildOf(legL)) || (legR != null && bt.IsChildOf(legR))) kind[b] = 2;
                }
                for (int v = 0; v < verts.Length; v++)
                {
                    int b = weights[v].boneIndex0;
                    if (b < 0 || b >= bones.Length || kind[b] <= 0) continue;
                    float h = Vector3.Dot(mats[b].MultiplyPoint3x4(verts[v]), up);
                    if (kind[b] == 1) top = Mathf.Max(top, h);
                    else bottom = Mathf.Min(bottom, h);
                }
            }
            if (top > bottom && top - bottom > 0.05f) return top - bottom;
            return VrmBoundsHeight(up);
        }

        /// <summary>Fallback: height of all of the VRM's mesh bounds.</summary>
        private float VrmBoundsHeight(Vector3 up)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            var toRoot = transform.worldToLocalMatrix;
            foreach (var r in ownRenderers)
            {
                Mesh mesh = null;
                Matrix4x4 m;
                if (r is SkinnedMeshRenderer smr)
                {
                    mesh = smr.sharedMesh;
                    // Skinned glTF vertices are already in model space; unskinned ones follow their node.
                    m = smr.bones != null && smr.bones.Length > 0 ? Matrix4x4.identity : toRoot * r.transform.localToWorldMatrix;
                }
                else
                {
                    if (r.TryGetComponent<MeshFilter>(out var mf)) mesh = mf.sharedMesh;
                    m = toRoot * r.transform.localToWorldMatrix;
                }
                if (mesh == null || mesh.vertexCount == 0) continue;
                Extent(mesh.bounds, up, m, out float a, out float b);
                lo = Mathf.Min(lo, a);
                hi = Mathf.Max(hi, b);
            }
            return hi > lo ? hi - lo : 0f;
        }

        private static float Extent(Bounds b, Vector3 up, Matrix4x4 m, out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                float d = Vector3.Dot(m.MultiplyPoint3x4(c), up);
                lo = Mathf.Min(lo, d);
                hi = Mathf.Max(hi, d);
            }
            return hi - lo;
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

            if (Time.unscaledTime >= nextBackpackScan) FindBackpack(false);
            if (!sizeLogged && Time.unscaledTime > attachTime + 1.5f && visible && ThirdPersonCamera.Active)
            {
                sizeLogged = true;
                bool any = false;
                Bounds b = default;
                foreach (var r in ownRenderers)
                {
                    if (r == null || !r.enabled || r.forceRenderingOff) continue;
                    if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds);
                }
                if (any) Plugin.Log.LogInfo($"On screen: avatar is {b.size.y:0.00} m tall (target {targetHeight:0.00} m)");
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
            bodyUp = up;
            Vector3 hipsPos = hipsMap.Game.position + up * hipLift;
            transform.position = hipsPos - transform.rotation * (hipsRestInRoot * scale);

            hipsMap.Vrm.position = hipsPos;
            bool armIK = ModConfig.Arms.Value != ArmMode.CopyRotation;
            for (int i = 0; i < maps.Count; i++)
            {
                var m = maps[i];
                if (m.Game == null) continue;
                // Copy the scout's rotation (this also carries the twist of limbs).
                m.Vrm.rotation = m.Game.rotation * m.Offset;
                if (m.IsArm && !armIK) continue;
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
            if (up.IsLeg)
            {
                // Put the foot under the avatar's own hip joint rather than the scout's (much wider) one,
                // keeping the scout's foot height so it still stands on the ground.
                target += Vector3.ProjectOnPlane(a - up.Game.position, bodyUp);
            }
            else if (up.IsArm && ModConfig.Arms.Value == ArmMode.MatchShoulders)
            {
                // Keep the hand where the scout's is relative to its shoulder, measured from the avatar's own
                // (narrower) shoulder: arms hang closer to the body, held items sit slightly off the hand.
                target += a - up.Game.position;
            }
            float l1 = (low.Vrm.position - a).magnitude;
            float l2 = (up.VrmEnd.position - low.Vrm.position).magnitude;
            Vector3 toTarget = target - a;
            float d = toTarget.magnitude;
            if (l1 < 1e-5f || l2 < 1e-5f || d < 1e-5f) return;
            Vector3 dir = toTarget / d;
            d = Mathf.Clamp(d, Mathf.Abs(l1 - l2) + 1e-4f, (l1 + l2) * 0.9999f);

            // Bend direction: towards the scout's elbow/knee, falling back to the avatar's current bend.
            Vector3 bend;
            if (up.IsLeg)
            {
                // Knees always bend the way the body faces (the scout's splayed knees made them bow outward).
                Vector3 fwd = hipsMap.Vrm.rotation * vrmForwardInHips;
                bend = Vector3.ProjectOnPlane(fwd, dir);
            }
            else bend = Vector3.ProjectOnPlane(up.GameMid.position - up.Game.position, dir);
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
            {
                if (r == null || IsOwn(r)) continue;
                bool h = hide && !IsBackpack(r.transform);
                if (r.forceRenderingOff != h) r.forceRenderingOff = h;
            }
        }

        private bool IsOwn(Renderer r) => r.transform.IsChildOf(transform);

        private bool IsBackpack(Transform tr)
        {
            if (!ModConfig.ShowBackpack.Value) return false;
            foreach (var b in backpackRoots)
                if (b != null && tr.IsChildOf(b)) return true;
            return false;
        }

        private void OnDestroy()
        {
            RestoreBackpack();
            Live.Remove(this);
            if (Character != null) HideScout(false);
        }

        // ------------------------------------------------------------------ backpack

        /// <summary>
        /// Finds the visuals of the backpack the scout wears: whatever hangs under the character's
        /// backpack anchor (refs.backpackTransform), plus renderer objects under the character whose
        /// name mentions a backpack. Rescanned every couple of seconds as backpacks are put on and taken off.
        /// </summary>
        private void FindBackpack(bool log)
        {
            nextBackpackScan = Time.unscaledTime + 2f;
            backpackRoots.Clear();
            // PEAK keeps the worn backpack in CharacterBackpackHandler.backpack (MeshRenderers under the character).
            var worn = BackpackPatch.BackpackOf(Character);
            if (worn != null && worn.transform != Character.transform && !scoutMain.transform.IsChildOf(worn.transform))
            {
                backpackRoots.Add(worn.transform);
                if (log) Plugin.Log.LogInfo($"Backpack: {worn.name} ({worn.GetComponentsInChildren<Renderer>(true).Length} renderers)");
                return;
            }
            var anchor = Peak.Get(Peak.Refs(Character), "backpackTransform") as Transform;
            bool anchorUsable = anchor != null && anchor != scoutTorso && anchor != Character.transform &&
                                !scoutMain.transform.IsChildOf(anchor);
            if (anchorUsable && anchor.GetComponentsInChildren<Renderer>(true).Length > 0) backpackRoots.Add(anchor);

            foreach (var r in Character.GetComponentsInChildren<Renderer>(true))
            {
                if (r == scoutMain || IsUnder(r.transform, backpackRoots)) continue;
                var named = FindNamedAncestor(r.transform, "backpack");
                if (named != null && named != Character.transform && !scoutMain.transform.IsChildOf(named) && !backpackRoots.Contains(named))
                    backpackRoots.Add(named);
            }
            if (log)
            {
                var names = backpackRoots.ConvertAll(b => b.name + " (" + b.GetComponentsInChildren<Renderer>(true).Length + " renderers)");
                Plugin.Log.LogInfo($"Backpack anchor: {(anchor != null ? anchor.name : "none")}; visuals: " +
                                   (names.Count > 0 ? string.Join(", ", names) : "none yet"));
            }
        }

        private Transform FindNamedAncestor(Transform tr, string word)
        {
            for (var x = tr; x != null && x != Character.transform; x = x.parent)
                if (x.name.IndexOf(word, System.StringComparison.OrdinalIgnoreCase) >= 0) return x;
            return null;
        }

        private static bool IsUnder(Transform tr, List<Transform> roots)
        {
            foreach (var r in roots) if (r != null && tr.IsChildOf(r)) return true;
            return false;
        }

        /// <summary>
        /// Moves the backpack from the scout's (much wider) back onto the avatar's back for the duration of a
        /// camera render. The game's own transforms are put back right after, so physics and game logic
        /// never see the change.
        /// </summary>
        private void MoveBackpack()
        {
            if (backpackMoved || !Ready || !visible || !ModConfig.ShowBackpack.Value) return;
            if (backpackRoots.Count == 0 || scoutTorso == null || vrmBack == null) return;

            // How much slimmer the avatar's torso is than the scout's.
            float k = 0.6f;
            if (scoutArmL != null && scoutArmR != null && vrmArmL != null && vrmArmR != null)
            {
                float sw = (scoutArmL.position - scoutArmR.position).magnitude;
                float vw = (vrmArmL.position - vrmArmR.position).magnitude;
                if (sw > 1e-3f) k = Mathf.Clamp(vw / sw, 0.2f, 1.5f);
            }
            k *= ModConfig.BackpackDistance.Value;

            backpackSaved.Clear();
            foreach (var b in backpackRoots)
            {
                if (b == null) { backpackSaved.Add(Vector3.zero); continue; }
                backpackSaved.Add(b.position);
                Vector3 rel = b.position - scoutTorso.position;
                Vector3 vertical = Vector3.Project(rel, bodyUp);
                Vector3 horizontal = rel - vertical;
                b.position = vrmBack.position + horizontal * k + vertical + bodyUp * ModConfig.BackpackHeight.Value;
            }
            backpackMoved = true;
        }

        private void RestoreBackpack()
        {
            if (!backpackMoved) return;
            for (int i = 0; i < backpackRoots.Count && i < backpackSaved.Count; i++)
                if (backpackRoots[i] != null) backpackRoots[i].position = backpackSaved[i];
            backpackMoved = false;
        }

        private static void HookRendering()
        {
            if (renderHooked) return;
            renderHooked = true;
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += (ctx, cam) => BeginRender(cam);
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering += (ctx, cam) => EndRender();
            Camera.onPreCull += BeginRender;
            Camera.onPostRender += cam => EndRender();
        }

        private static void BeginRender(Camera cam)
        {
            if (cam == null || cam.cameraType != CameraType.Game) return;
            foreach (var d in Live) if (d != null) d.MoveBackpack();
        }

        private static void EndRender()
        {
            foreach (var d in Live) if (d != null) d.RestoreBackpack();
        }
    }
}
