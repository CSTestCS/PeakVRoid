using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakVRoid.Vrm
{
    /// <summary>
    /// Builds a Unity hierarchy from a .vrm (VRM 0.x or 1.0) file at runtime: nodes,
    /// skinned meshes, blend shapes, textures, materials, humanoid map, spring bones,
    /// blink expression and first-person mesh annotations.
    /// </summary>
    public sealed class VrmLoader
    {
        private readonly GltfDocument doc;
        private readonly MaterialFactory materials;
        private readonly Transform parent;

        // Coordinate conversion. glTF is right-handed; Unity is left-handed. VRM 1.0 (and plain
        // glTF) is converted by mirroring X; VRM 0.x models face -Z and are mirrored on Z instead.
        private readonly bool flipZ;

        private Transform[] nodes;
        private readonly Dictionary<int, Texture2D> textures = new Dictionary<int, Texture2D>();
        private readonly Dictionary<int, Material> mats = new Dictionary<int, Material>();
        private readonly Dictionary<int, Mesh> meshes = new Dictionary<int, Mesh>();
        private Material defaultMaterial;
        private readonly List<Mesh> extraMeshes = new List<Mesh>();

        private VrmLoader(GltfDocument doc, MaterialFactory materials, Transform parent)
        {
            this.doc = doc;
            this.materials = materials;
            this.parent = parent;
            flipZ = doc.IsVrm0;
        }

        public static VrmModel Load(string path, MaterialFactory materials, Transform templateParent)
        {
            var doc = GltfDocument.Load(path);
            var loader = new VrmLoader(doc, materials, templateParent);
            var model = loader.Build();
            model.FilePath = path;
            return model;
        }

        // ------------------------------------------------------------------ conversion

        private Vector3 V(float x, float y, float z) => flipZ ? new Vector3(x, y, -z) : new Vector3(-x, y, z);
        private Vector3 V(float[] a, int i = 0) => V(a[i], a[i + 1], a[i + 2]);

        private Quaternion Q(float x, float y, float z, float w)
            => flipZ ? new Quaternion(-x, -y, z, w) : new Quaternion(x, -y, -z, w);

        private Matrix4x4 M(float[] a, int o)
        {
            var m = new Matrix4x4();
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    m[r, c] = a[o + c * 4 + r]; // glTF is column-major
            // S * m * S with S = mirror matrix.
            int k = flipZ ? 2 : 0;
            for (int i = 0; i < 4; i++)
            {
                if (i != k) { m[k, i] = -m[k, i]; m[i, k] = -m[i, k]; }
            }
            return m;
        }

        private Vector3 Vec(List<object> l)
        {
            if (l == null || l.Count < 3) return Vector3.zero;
            return V(Convert.ToSingle(l[0]), Convert.ToSingle(l[1]), Convert.ToSingle(l[2]));
        }

        /// <summary>VRM 0.x stores spring/collider vectors as {x,y,z} already in Unity's coordinate system.</summary>
        private static Vector3 Vec(Dictionary<string, object> o)
        {
            if (o == null) return Vector3.zero;
            return new Vector3((float)o.Num("x"), (float)o.Num("y"), (float)o.Num("z"));
        }

        // ------------------------------------------------------------------ build

        private VrmModel Build()
        {
            var model = new VrmModel { SpecVersion = doc.IsVrm1 ? 1 : 0 };
            ReadMeta(model);

            var root = new GameObject("VRM " + model.Title);
            root.transform.SetParent(parent, false);
            model.Template = root;

            BuildNodes(root.transform);
            BuildRenderers();
            foreach (var tex in textures.Values) if (tex != null) model.Assets.Add(tex);
            foreach (var mat in mats.Values) model.Assets.Add(mat);
            if (defaultMaterial != null) model.Assets.Add(defaultMaterial);
            foreach (var mesh in meshes.Values) model.Assets.Add(mesh);
            model.Assets.AddRange(extraMeshes);

            var all = root.GetComponentsInChildren<Transform>(true);
            var index = new Dictionary<Transform, int>();
            for (int i = 0; i < all.Length; i++) index[all[i]] = i;
            int NodeIx(int node) => node >= 0 && node < nodes.Length && nodes[node] != null ? index[nodes[node]] : -1;

            ReadHumanoid(model, NodeIx);
            if (!model.HumanBones.ContainsKey("hips"))
                throw new InvalidDataException("This file has no VRM humanoid definition (is it a plain .glb?).");

            ReadSpringBones(model, NodeIx, all);
            ReadBlink(model, NodeIx);
            ReadFirstPerson(model, NodeIx);

            // Measure rest proportions.
            var hips = all[model.HumanBones["hips"]];
            float floor = 0f;
            foreach (var foot in new[] { "leftFoot", "rightFoot", "leftToes", "rightToes" })
                if (model.HumanBones.TryGetValue(foot, out int fi)) floor = Mathf.Min(floor, all[fi].position.y);
            model.HipHeight = Mathf.Max(0.05f, hips.position.y - floor);
            model.HeadHeight = model.HumanBones.TryGetValue("head", out int hi)
                ? Mathf.Max(model.HipHeight, all[hi].position.y - floor)
                : model.HipHeight * 2f;

            return model;
        }

        private void ReadMeta(VrmModel model)
        {
            if (doc.IsVrm1)
            {
                var meta = doc.Vrm1.Obj("meta");
                model.Title = meta.Str("name", "Avatar");
                var authors = meta.Arr("authors");
                model.Author = authors != null && authors.Count > 0 ? authors[0] as string : "";
            }
            else
            {
                var meta = doc.Vrm0?.Obj("meta");
                model.Title = meta.Str("title", "Avatar");
                model.Author = meta.Str("author", "");
            }
            if (string.IsNullOrEmpty(model.Title)) model.Title = "Avatar";
        }

        private void BuildNodes(Transform root)
        {
            var list = doc.Nodes;
            nodes = new Transform[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i] as Dictionary<string, object>;
                var go = new GameObject(n.Str("name", "node" + i));
                nodes[i] = go.transform;
            }

            var hasParent = new bool[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                var children = (list[i] as Dictionary<string, object>).Arr("children");
                if (children == null) continue;
                foreach (var c in children)
                {
                    int ci = Convert.ToInt32(c);
                    if (ci < 0 || ci >= nodes.Length || hasParent[ci]) continue;
                    hasParent[ci] = true;
                    nodes[ci].SetParent(nodes[i], false);
                }
            }
            for (int i = 0; i < list.Count; i++)
                if (!hasParent[i]) nodes[i].SetParent(root, false);

            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i] as Dictionary<string, object>;
                var t = nodes[i];
                var matrix = n.Floats("matrix");
                if (matrix != null && matrix.Length == 16)
                {
                    var m = M(matrix, 0);
                    t.localPosition = m.GetColumn(3);
                    t.localRotation = m.rotation;
                    t.localScale = m.lossyScale;
                    continue;
                }
                var tr = n.Floats("translation");
                if (tr != null) t.localPosition = V(tr);
                var r = n.Floats("rotation");
                if (r != null) t.localRotation = Q(r[0], r[1], r[2], r[3]);
                var s = n.Floats("scale");
                if (s != null) t.localScale = new Vector3(s[0], s[1], s[2]);
            }
        }

        // ------------------------------------------------------------------ textures & materials

        private Texture2D GetTexture(int textureIndex)
        {
            if (textureIndex < 0 || textureIndex >= doc.Textures.Count) return null;
            if (textures.TryGetValue(textureIndex, out var cachedTex)) return cachedTex;
            Texture2D tex = null;
            try
            {
                var td = doc.Textures[textureIndex] as Dictionary<string, object>;
                int source = td.Int("source");
                if (source < 0) // KHR_texture_basisu / EXT_texture_webp are not supported; try any fallback source.
                    source = td.Obj("extensions")?.Obj("KHR_texture_basisu")?.Int("source") ?? -1;
                if (source >= 0)
                {
                    var bytes = doc.ImageBytes(source, out _);
                    tex = new Texture2D(2, 2, TextureFormat.RGBA32, true, false);
                    tex.name = (doc.Images[source] as Dictionary<string, object>).Str("name", "tex" + source);
                    if (!ImageConversion.LoadImage(tex, bytes, false))
                    {
                        UnityEngine.Object.Destroy(tex);
                        tex = null;
                    }
                    else
                    {
                        var sampler = td.Int("sampler") is int si && si >= 0 && si < doc.Samplers.Count
                            ? doc.Samplers[si] as Dictionary<string, object>
                            : null;
                        tex.wrapModeU = Wrap(sampler.Int("wrapS", 10497));
                        tex.wrapModeV = Wrap(sampler.Int("wrapT", 10497));
                        int mag = sampler.Int("magFilter", 9729);
                        tex.filterMode = mag == 9728 ? FilterMode.Point : FilterMode.Bilinear;
                        tex.anisoLevel = 2;
                        tex.Apply(true, true);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Texture {textureIndex} failed to load: {e.Message}");
                tex = null;
            }
            textures[textureIndex] = tex;
            return tex;
        }

        private static TextureWrapMode Wrap(int mode)
        {
            switch (mode)
            {
                case 33071: return TextureWrapMode.Clamp;
                case 33648: return TextureWrapMode.Mirror;
                default: return TextureWrapMode.Repeat;
            }
        }

        private Material GetMaterial(int index)
        {
            if (index < 0 || index >= doc.Materials.Count)
            {
                if (defaultMaterial == null)
                    defaultMaterial = materials.Create(new Dictionary<string, object>(), GetTexture);
                return defaultMaterial;
            }
            if (mats.TryGetValue(index, out var m)) return m;
            m = materials.Create(doc.Materials[index] as Dictionary<string, object>, GetTexture);
            mats[index] = m;
            return m;
        }

        // ------------------------------------------------------------------ meshes

        private void BuildRenderers()
        {
            var list = doc.Nodes;
            for (int i = 0; i < list.Count; i++)
            {
                var n = list[i] as Dictionary<string, object>;
                int meshIndex = n.Int("mesh");
                if (meshIndex < 0) continue;
                try
                {
                    var mesh = GetMesh(meshIndex, out var materialIndices);
                    var materialsArr = new Material[materialIndices.Count];
                    for (int k = 0; k < materialsArr.Length; k++) materialsArr[k] = GetMaterial(materialIndices[k]);

                    int skinIndex = n.Int("skin");
                    var go = nodes[i].gameObject;
                    if (skinIndex >= 0 || mesh.blendShapeCount > 0)
                    {
                        var smr = go.AddComponent<SkinnedMeshRenderer>();
                        smr.sharedMaterials = materialsArr;
                        smr.updateWhenOffscreen = true;
                        smr.shadowCastingMode = ShadowCastingMode.On;
                        if (skinIndex >= 0)
                        {
                            var skinnedMesh = ApplySkin(mesh, skinIndex, smr);
                            smr.sharedMesh = skinnedMesh;
                        }
                        else smr.sharedMesh = mesh;
                    }
                    else
                    {
                        go.AddComponent<MeshFilter>().sharedMesh = mesh;
                        var mr = go.AddComponent<MeshRenderer>();
                        mr.sharedMaterials = materialsArr;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Mesh on node {i} failed to load: {e}");
                }
            }
        }

        private readonly Dictionary<int, List<int>> meshMaterials = new Dictionary<int, List<int>>();

        private Mesh GetMesh(int meshIndex, out List<int> materialIndices)
        {
            if (meshes.TryGetValue(meshIndex, out var cachedMesh))
            {
                materialIndices = meshMaterials[meshIndex];
                return cachedMesh;
            }

            var gm = doc.Meshes[meshIndex] as Dictionary<string, object>;
            var prims = new List<Dictionary<string, object>>();
            foreach (var p in gm.Arr("primitives").Objs())
                if (p.Int("mode", 4) == 4 && p.Obj("attributes").Has("POSITION")) prims.Add(p);

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var weights = new List<BoneWeight>();
            var submeshes = new List<int[]>();
            materialIndices = new List<int>();
            bool missingNormals = false;
            bool skinned = false;
            var primStart = new List<int>();
            // VRoid and UniVRM exports let many primitives share one vertex buffer; only add each block once.
            var blocks = new Dictionary<string, int>();

            foreach (var p in prims)
            {
                var attr = p.Obj("attributes");
                string key = BlockKey(p);
                bool shared = blocks.TryGetValue(key, out int start);
                if (!shared)
                {
                    start = positions.Count;
                    blocks[key] = start;
                }
                primStart.Add(start);
                int count = doc.AccessorCount(attr.Int("POSITION"));
                if (shared)
                {
                    AddIndices(p, start, count, submeshes, materialIndices);
                    continue;
                }
                var pos = doc.ReadFloats(attr.Int("POSITION"), out _);
                for (int v = 0; v < count; v++) positions.Add(V(pos, v * 3));

                if (attr.Has("NORMAL"))
                {
                    var nrm = doc.ReadFloats(attr.Int("NORMAL"), out _);
                    for (int v = 0; v < count; v++) normals.Add(V(nrm, v * 3));
                }
                else
                {
                    missingNormals = true;
                    for (int v = 0; v < count; v++) normals.Add(Vector3.up);
                }

                if (attr.Has("TEXCOORD_0"))
                {
                    var uv = doc.ReadFloats(attr.Int("TEXCOORD_0"), out _);
                    for (int v = 0; v < count; v++) uvs.Add(new Vector2(uv[v * 2], 1f - uv[v * 2 + 1]));
                }
                else for (int v = 0; v < count; v++) uvs.Add(Vector2.zero);

                if (attr.Has("JOINTS_0") && attr.Has("WEIGHTS_0"))
                {
                    skinned = true;
                    var j = doc.ReadFloats(attr.Int("JOINTS_0"), out int jc);
                    var w = doc.ReadFloats(attr.Int("WEIGHTS_0"), out int wc);
                    for (int v = 0; v < count; v++)
                        weights.Add(MakeWeight(j, w, v * jc, v * wc, Math.Min(jc, wc)));
                }
                else for (int v = 0; v < count; v++) weights.Add(new BoneWeight { weight0 = 1f });

                AddIndices(p, start, count, submeshes, materialIndices);
            }

            var mesh = new Mesh { name = gm.Str("name", "mesh" + meshIndex) };
            if (positions.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(positions);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            if (skinned) mesh.boneWeights = weights.ToArray();
            mesh.subMeshCount = submeshes.Count;
            for (int s = 0; s < submeshes.Count; s++) mesh.SetTriangles(submeshes[s], s, false);
            if (missingNormals) mesh.RecalculateNormals();

            AddBlendShapes(mesh, gm, prims, primStart, positions.Count);
            mesh.RecalculateBounds();

            meshes[meshIndex] = mesh;
            meshMaterials[meshIndex] = materialIndices;
            return mesh;
        }

        private static string BlockKey(Dictionary<string, object> p)
        {
            var a = p.Obj("attributes");
            var sb = new System.Text.StringBuilder();
            foreach (var k in new[] { "POSITION", "NORMAL", "TEXCOORD_0", "JOINTS_0", "WEIGHTS_0" })
                sb.Append(a.Int(k)).Append(',');
            foreach (var t in p.Arr("targets").Objs())
                sb.Append(t.Int("POSITION")).Append('/').Append(t.Int("NORMAL")).Append(',');
            return sb.ToString();
        }

        private void AddIndices(Dictionary<string, object> p, int start, int count, List<int[]> submeshes, List<int> materialIndices)
        {
            int[] idx;
            if (p.Has("indices")) idx = doc.ReadIndices(p.Int("indices"));
            else
            {
                idx = new int[count];
                for (int v = 0; v < count; v++) idx[v] = v;
            }
            // Mirroring the coordinate system flips handedness, so reverse the winding.
            int tri = idx.Length - idx.Length % 3;
            var outIdx = new int[tri];
            for (int t = 0; t < tri; t += 3)
            {
                outIdx[t] = idx[t] + start;
                outIdx[t + 1] = idx[t + 2] + start;
                outIdx[t + 2] = idx[t + 1] + start;
            }
            submeshes.Add(outIdx);
            materialIndices.Add(p.Int("material"));
        }

        private static BoneWeight MakeWeight(float[] j, float[] w, int jo, int wo, int n)
        {
            float w0 = n > 0 ? w[wo] : 0, w1 = n > 1 ? w[wo + 1] : 0, w2 = n > 2 ? w[wo + 2] : 0, w3 = n > 3 ? w[wo + 3] : 0;
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 1e-6f) { w0 = 1f; sum = 1f; }
            return new BoneWeight
            {
                boneIndex0 = n > 0 ? (int)j[jo] : 0,
                boneIndex1 = n > 1 ? (int)j[jo + 1] : 0,
                boneIndex2 = n > 2 ? (int)j[jo + 2] : 0,
                boneIndex3 = n > 3 ? (int)j[jo + 3] : 0,
                weight0 = w0 / sum,
                weight1 = w1 / sum,
                weight2 = w2 / sum,
                weight3 = w3 / sum,
            };
        }

        private void AddBlendShapes(Mesh mesh, Dictionary<string, object> gm, List<Dictionary<string, object>> prims,
            List<int> primStart, int vertexCount)
        {
            if (prims.Count == 0) return;
            var firstTargets = prims[0].Arr("targets");
            if (firstTargets == null || firstTargets.Count == 0) return;
            int shapeCount = firstTargets.Count;

            var names = gm.Obj("extras").Arr("targetNames") ?? prims[0].Obj("extras").Arr("targetNames");
            var used = new HashSet<string>();
            for (int s = 0; s < shapeCount; s++)
            {
                var dv = new Vector3[vertexCount];
                var dn = new Vector3[vertexCount];
                var done = new HashSet<int>();
                for (int p = 0; p < prims.Count; p++)
                {
                    var targets = prims[p].Arr("targets");
                    if (targets == null || s >= targets.Count) continue;
                    var t = targets[s] as Dictionary<string, object>;
                    int start = primStart[p];
                    if (!done.Add(start)) continue; // shared vertex block already filled
                    if (t.Has("POSITION"))
                    {
                        var a = doc.ReadFloats(t.Int("POSITION"), out _);
                        for (int v = 0; v * 3 < a.Length && start + v < vertexCount; v++) dv[start + v] = V(a, v * 3);
                    }
                    if (t.Has("NORMAL"))
                    {
                        var a = doc.ReadFloats(t.Int("NORMAL"), out _);
                        for (int v = 0; v * 3 < a.Length && start + v < vertexCount; v++) dn[start + v] = V(a, v * 3);
                    }
                }
                string name = names != null && s < names.Count && names[s] is string str ? str : "blend_" + s;
                string unique = name;
                for (int k = 1; !used.Add(unique); k++) unique = name + "_" + k;
                mesh.AddBlendShapeFrame(unique, 100f, dv, dn, null);
            }
        }

        private Mesh ApplySkin(Mesh mesh, int skinIndex, SkinnedMeshRenderer smr)
        {
            var skin = doc.Skins[skinIndex] as Dictionary<string, object>;
            var joints = skin.Arr("joints");
            var bones = new Transform[joints.Count];
            for (int i = 0; i < bones.Length; i++) bones[i] = nodes[Convert.ToInt32(joints[i])];

            var bindposes = new Matrix4x4[bones.Length];
            if (skin.Has("inverseBindMatrices"))
            {
                var ibm = doc.ReadFloats(skin.Int("inverseBindMatrices"), out _);
                for (int i = 0; i < bones.Length; i++) bindposes[i] = M(ibm, i * 16);
            }
            else
            {
                var rootMatrix = smr.transform.root.localToWorldMatrix;
                for (int i = 0; i < bones.Length; i++) bindposes[i] = bones[i].worldToLocalMatrix * rootMatrix;
            }

            // The same glTF mesh can in theory be bound to different skins; give each its own copy.
            var m = mesh;
            if (mesh.bindposes != null && mesh.bindposes.Length > 0)
            {
                m = UnityEngine.Object.Instantiate(mesh);
                extraMeshes.Add(m);
            }
            m.bindposes = bindposes;
            smr.bones = bones;
            int skel = skin.Int("skeleton");
            smr.rootBone = skel >= 0 ? nodes[skel] : bones.Length > 0 ? bones[0] : null;
            return m;
        }

        // ------------------------------------------------------------------ VRM extensions

        private static readonly Dictionary<string, string> Vrm0BoneRename = new Dictionary<string, string>
        {
            { "leftThumbProximal", "leftThumbMetacarpal" },
            { "leftThumbIntermediate", "leftThumbProximal" },
            { "rightThumbProximal", "rightThumbMetacarpal" },
            { "rightThumbIntermediate", "rightThumbProximal" },
        };

        private void ReadHumanoid(VrmModel model, Func<int, int> nodeIx)
        {
            if (doc.IsVrm1)
            {
                var hb = doc.Vrm1.Obj("humanoid").Obj("humanBones");
                if (hb == null) return;
                foreach (var kv in hb)
                    if (kv.Value is Dictionary<string, object> b && nodeIx(b.Int("node")) is int ix && ix >= 0)
                        model.HumanBones[kv.Key] = ix;
            }
            else
            {
                foreach (var b in doc.Vrm0.Obj("humanoid").Arr("humanBones").Objs())
                {
                    string name = b.Str("bone");
                    int ix = nodeIx(b.Int("node"));
                    if (name == null || ix < 0) continue;
                    if (Vrm0BoneRename.TryGetValue(name, out var renamed)) name = renamed;
                    model.HumanBones[name] = ix;
                }
            }
        }

        private void ReadSpringBones(VrmModel model, Func<int, int> nodeIx, Transform[] all)
        {
            try
            {
                if (doc.IsVrm1) ReadSpringVrm1(model, nodeIx, all);
                else ReadSpringVrm0(model, nodeIx, all);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Spring bones could not be read: " + e.Message);
                model.SpringJoints.Clear();
            }
        }

        private void ReadSpringVrm1(VrmModel model, Func<int, int> nodeIx, Transform[] all)
        {
            var ext = doc.Vrm1Spring;
            if (ext == null) return;
            foreach (var c in ext.Arr("colliders").Objs())
            {
                var shape = c.Obj("shape");
                var def = new ColliderDef { Node = nodeIx(c.Int("node")) };
                var sphere = shape.Obj("sphere");
                var capsule = shape.Obj("capsule");
                var src = sphere ?? capsule;
                if (src == null) { model.Colliders.Add(def); continue; }
                def.Offset = Vec(src.Arr("offset"));
                def.Radius = (float)src.Num("radius");
                if (capsule != null) { def.Capsule = true; def.Tail = Vec(capsule.Arr("tail")); }
                model.Colliders.Add(def);
            }
            var groups = new List<int[]>();
            foreach (var g in ext.Arr("colliderGroups").Objs())
            {
                var l = g.Arr("colliders");
                var arr = new int[l?.Count ?? 0];
                for (int i = 0; i < arr.Length; i++) arr[i] = Convert.ToInt32(l[i]);
                groups.Add(arr);
            }

            foreach (var spring in ext.Arr("springs").Objs())
            {
                var colliders = new List<int>();
                var cg = spring.Arr("colliderGroups");
                if (cg != null)
                    foreach (var gi in cg)
                    {
                        int g = Convert.ToInt32(gi);
                        if (g >= 0 && g < groups.Count) colliders.AddRange(groups[g]);
                    }
                int center = nodeIx(spring.Int("center"));
                var joints = new List<Dictionary<string, object>>(spring.Arr("joints").Objs());
                for (int j = 0; j < joints.Count - 1; j++)
                {
                    var jd = joints[j];
                    int bone = nodeIx(jd.Int("node"));
                    int tail = nodeIx(joints[j + 1].Int("node"));
                    if (bone < 0 || tail < 0) continue;
                    var gdir = jd.Arr("gravityDir");
                    model.SpringJoints.Add(new SpringJointDef
                    {
                        Bone = bone,
                        TailLocal = all[bone].InverseTransformPoint(all[tail].position),
                        Stiffness = (float)jd.Num("stiffness", 1.0),
                        GravityPower = (float)jd.Num("gravityPower", 0.0),
                        GravityDir = gdir != null ? Vec(gdir) : Vector3.down,
                        Drag = (float)jd.Num("dragForce", 0.5),
                        HitRadius = (float)jd.Num("hitRadius", 0.0),
                        Center = center,
                        Colliders = colliders.ToArray(),
                    });
                }
            }
        }

        private void ReadSpringVrm0(VrmModel model, Func<int, int> nodeIx, Transform[] all)
        {
            var sa = doc.Vrm0.Obj("secondaryAnimation");
            if (sa == null) return;
            var groups = new List<int[]>();
            foreach (var g in sa.Arr("colliderGroups").Objs())
            {
                int node = nodeIx(g.Int("node"));
                var ids = new List<int>();
                foreach (var c in g.Arr("colliders").Objs())
                {
                    ids.Add(model.Colliders.Count);
                    model.Colliders.Add(new ColliderDef
                    {
                        Node = node,
                        Offset = Vec(c.Obj("offset")),
                        Radius = (float)c.Num("radius"),
                    });
                }
                groups.Add(ids.ToArray());
            }

            var seen = new HashSet<int>();
            foreach (var bg in sa.Arr("boneGroups").Objs())
            {
                var colliders = new List<int>();
                var cg = bg.Arr("colliderGroups");
                if (cg != null)
                    foreach (var gi in cg)
                    {
                        int g = Convert.ToInt32(gi);
                        if (g >= 0 && g < groups.Count) colliders.AddRange(groups[g]);
                    }
                var proto = new SpringJointDef
                {
                    Stiffness = (float)bg.Num("stiffiness", 1.0),
                    GravityPower = (float)bg.Num("gravityPower", 0.0),
                    GravityDir = bg.Has("gravityDir") ? Vec(bg.Obj("gravityDir")) : Vector3.down,
                    Drag = (float)bg.Num("dragForce", 0.4),
                    HitRadius = (float)bg.Num("hitRadius", 0.02),
                    Center = nodeIx(bg.Int("center")),
                    Colliders = colliders.ToArray(),
                };
                var bones = bg.Arr("bones");
                if (bones == null) continue;
                foreach (var b in bones)
                {
                    int ix = nodeIx(Convert.ToInt32(b));
                    if (ix >= 0) AddVrm0Chain(model, all, all[ix], proto, seen);
                }
            }
        }

        private void AddVrm0Chain(VrmModel model, Transform[] all, Transform bone, SpringJointDef proto, HashSet<int> seen)
        {
            int ix = Array.IndexOf(all, bone);
            if (ix < 0 || !seen.Add(ix)) return;
            Vector3 tailLocal;
            if (bone.childCount > 0)
                tailLocal = bone.InverseTransformPoint(bone.GetChild(0).position);
            else
            {
                var delta = bone.parent != null ? bone.position - bone.parent.position : Vector3.up;
                tailLocal = bone.InverseTransformPoint(bone.position + delta.normalized * 0.07f);
            }
            model.SpringJoints.Add(new SpringJointDef
            {
                Bone = ix,
                TailLocal = tailLocal,
                Stiffness = proto.Stiffness,
                GravityPower = proto.GravityPower,
                GravityDir = proto.GravityDir,
                Drag = proto.Drag,
                HitRadius = proto.HitRadius,
                Center = proto.Center,
                Colliders = proto.Colliders,
            });
            for (int c = 0; c < bone.childCount; c++) AddVrm0Chain(model, all, bone.GetChild(c), proto, seen);
        }

        private void ReadBlink(VrmModel model, Func<int, int> nodeIx)
        {
            if (doc.IsVrm1)
            {
                var preset = doc.Vrm1.Obj("expressions").Obj("preset");
                var blink = preset.Obj("blink");
                var binds = blink.Arr("morphTargetBinds");
                if (binds == null || binds.Count == 0)
                {
                    // Fall back to the separate eye expressions.
                    binds = new List<object>();
                    foreach (var side in new[] { "blinkLeft", "blinkRight" })
                    {
                        var l = preset.Obj(side).Arr("morphTargetBinds");
                        if (l != null) binds.AddRange(l);
                    }
                }
                foreach (var b in binds.Objs())
                {
                    int ix = nodeIx(b.Int("node"));
                    if (ix >= 0)
                        model.Blink.Add(new BlendBind { Renderer = ix, Index = b.Int("index"), Weight = (float)b.Num("weight", 1) * 100f });
                }
            }
            else
            {
                foreach (var g in doc.Vrm0.Obj("blendShapeMaster").Arr("blendShapeGroups").Objs())
                {
                    string preset = (g.Str("presetName", "") ?? "").ToLowerInvariant();
                    if (preset != "blink") continue;
                    foreach (var b in g.Arr("binds").Objs())
                    {
                        int mesh = b.Int("mesh");
                        for (int n = 0; n < doc.Nodes.Count; n++)
                            if (doc.Node(n).Int("mesh") == mesh && nodeIx(n) is int ix && ix >= 0)
                                model.Blink.Add(new BlendBind { Renderer = ix, Index = b.Int("index"), Weight = (float)b.Num("weight", 100) });
                    }
                    break;
                }
            }
        }

        private void ReadFirstPerson(VrmModel model, Func<int, int> nodeIx)
        {
            if (doc.IsVrm1)
            {
                foreach (var a in doc.Vrm1.Obj("firstPerson").Arr("meshAnnotations").Objs())
                {
                    int ix = nodeIx(a.Int("node"));
                    if (ix < 0) continue;
                    string type = a.Str("type", "auto");
                    if (type == "thirdPersonOnly") model.ThirdPersonOnly.Add(ix);
                    else if (type == "firstPersonOnly") model.FirstPersonOnly.Add(ix);
                }
            }
            else
            {
                foreach (var a in doc.Vrm0.Obj("firstPerson").Arr("meshAnnotations").Objs())
                {
                    int mesh = a.Int("mesh");
                    string flag = a.Str("firstPersonFlag", "Auto");
                    for (int n = 0; n < doc.Nodes.Count; n++)
                    {
                        if (doc.Node(n).Int("mesh") != mesh || !(nodeIx(n) is int ix) || ix < 0) continue;
                        if (flag == "ThirdPersonOnly") model.ThirdPersonOnly.Add(ix);
                        else if (flag == "FirstPersonOnly") model.FirstPersonOnly.Add(ix);
                    }
                }
            }
        }
    }
}
