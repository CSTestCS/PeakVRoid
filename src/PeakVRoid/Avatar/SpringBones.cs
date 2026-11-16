using System.Collections.Generic;
using PeakVRoid.Vrm;
using UnityEngine;

namespace PeakVRoid.Avatar
{
    /// <summary>
    /// VRM spring bone simulation (hair, skirts, accessories), following the algorithm in
    /// the VRM specification: verlet integration of each joint's tail with stiffness,
    /// drag, gravity, length constraint and sphere/capsule colliders.
    /// </summary>
    public sealed class SpringBones
    {
        private sealed class Joint
        {
            public Transform Bone;
            public Transform Center;
            public Vector3 TailLocal;
            public Vector3 BoneAxis;
            public Quaternion InitialLocalRotation;
            public Vector3 CurrentTail; // in center space (world if Center is null)
            public Vector3 PrevTail;
            public SpringJointDef Def;
            public Collider[] Colliders;
        }

        private struct Collider
        {
            public Transform Node;
            public ColliderDef Def;
        }

        private readonly List<Joint> joints = new List<Joint>();

        public SpringBones(VrmModel model, Transform[] t)
        {
            var colliders = new Collider[model.Colliders.Count];
            for (int i = 0; i < colliders.Length; i++)
            {
                var d = model.Colliders[i];
                colliders[i] = new Collider { Node = d.Node >= 0 ? t[d.Node] : null, Def = d };
            }
            foreach (var d in model.SpringJoints)
            {
                var bone = t[d.Bone];
                if (bone.parent == null || d.TailLocal.sqrMagnitude < 1e-10f) continue;
                var cs = new List<Collider>();
                foreach (var ci in d.Colliders)
                    if (ci >= 0 && ci < colliders.Length && colliders[ci].Node != null) cs.Add(colliders[ci]);
                joints.Add(new Joint
                {
                    Bone = bone,
                    Center = d.Center >= 0 ? t[d.Center] : null,
                    TailLocal = d.TailLocal,
                    BoneAxis = d.TailLocal.normalized,
                    InitialLocalRotation = bone.localRotation,
                    Def = d,
                    Colliders = cs.ToArray(),
                });
            }
        }

        public int Count => joints.Count;

        private static Vector3 ToCenter(Joint j, Vector3 world) => j.Center != null ? j.Center.InverseTransformPoint(world) : world;
        private static Vector3 FromCenter(Joint j, Vector3 local) => j.Center != null ? j.Center.TransformPoint(local) : local;

        /// <summary>Snap every tail back to the rest pose (after teleports or when first shown).</summary>
        public void Reset()
        {
            foreach (var j in joints)
            {
                j.Bone.localRotation = j.InitialLocalRotation;
                var tail = ToCenter(j, j.Bone.TransformPoint(j.TailLocal));
                j.CurrentTail = j.PrevTail = tail;
            }
        }

        public void Update(float dt, float scale)
        {
            if (dt <= 0f) return;
            dt = Mathf.Min(dt, 1f / 30f);
            foreach (var j in joints)
            {
                var d = j.Def;
                var bone = j.Bone;
                Quaternion parentRot = bone.parent.rotation;
                Vector3 head = bone.position;
                float length = (bone.TransformPoint(j.TailLocal) - head).magnitude;
                if (length < 1e-6f) continue;

                Vector3 current = FromCenter(j, j.CurrentTail);
                Vector3 prev = FromCenter(j, j.PrevTail);

                Vector3 restDir = parentRot * j.InitialLocalRotation * j.BoneAxis;
                Vector3 next = current
                               + (current - prev) * (1f - d.Drag)
                               + restDir * (d.Stiffness * dt * scale)
                               + d.GravityDir * (d.GravityPower * dt * scale);

                next = head + (next - head).normalized * length;

                // Collisions.
                float hit = d.HitRadius * scale;
                foreach (var c in j.Colliders)
                {
                    float r = c.Def.Radius * scale + hit;
                    Vector3 p = c.Node.TransformPoint(c.Def.Offset);
                    if (c.Def.Capsule)
                    {
                        Vector3 q = c.Node.TransformPoint(c.Def.Tail);
                        Vector3 pq = q - p;
                        float tt = pq.sqrMagnitude > 1e-10f ? Mathf.Clamp01(Vector3.Dot(next - p, pq) / pq.sqrMagnitude) : 0f;
                        p += pq * tt;
                    }
                    Vector3 delta = next - p;
                    if (delta.sqrMagnitude < r * r)
                    {
                        next = p + delta.normalized * r;
                        next = head + (next - head).normalized * length;
                    }
                }

                if (float.IsNaN(next.x) || float.IsNaN(next.y) || float.IsNaN(next.z)) next = bone.TransformPoint(j.TailLocal);

                j.PrevTail = j.CurrentTail;
                j.CurrentTail = ToCenter(j, next);

                Quaternion restRot = parentRot * j.InitialLocalRotation;
                bone.rotation = Quaternion.FromToRotation(restRot * j.BoneAxis, next - head) * restRot;
            }
        }
    }
}
