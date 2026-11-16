using System.Collections.Generic;
using UnityEngine;

namespace PeakVRoid.Vrm
{
    /// <summary>
    /// A loaded VRM: an inactive template GameObject plus avatar metadata. All references
    /// into the hierarchy are stored as indices into
    /// <c>Template.GetComponentsInChildren&lt;Transform&gt;(true)</c>, which has the same
    /// order on every <see cref="Object.Instantiate(Object)"/> clone.
    /// </summary>
    public sealed class VrmModel
    {
        public string FilePath;
        public string Title;
        public string Author;
        public int SpecVersion; // 0 = VRM 0.x, 1 = VRM 1.0
        public GameObject Template;

        /// <summary>Meshes, materials and textures created for this model (destroyed on reload).</summary>
        public readonly List<Object> Assets = new List<Object>();

        /// <summary>VRM 1.0 humanoid bone name (e.g. "leftUpperArm") → transform index.</summary>
        public readonly Dictionary<string, int> HumanBones = new Dictionary<string, int>();

        public readonly List<SpringJointDef> SpringJoints = new List<SpringJointDef>();
        public readonly List<ColliderDef> Colliders = new List<ColliderDef>();

        public readonly List<BlendBind> Blink = new List<BlendBind>();
        public readonly List<int> ThirdPersonOnly = new List<int>();
        public readonly List<int> FirstPersonOnly = new List<int>();

        /// <summary>Distance from the hips to the floor in the rest pose (model units).</summary>
        public float HipHeight;
        /// <summary>Distance from the floor to the top of the head bone in the rest pose.</summary>
        public float HeadHeight;
    }

    public sealed class SpringJointDef
    {
        public int Bone;
        /// <summary>Tail position in the bone's local space, measured in the rest pose.</summary>
        public Vector3 TailLocal;
        public float Stiffness = 1f;
        public float GravityPower;
        public Vector3 GravityDir = Vector3.down;
        public float Drag = 0.4f;
        public float HitRadius = 0.02f;
        public int Center = -1;
        public int[] Colliders = new int[0];
    }

    public sealed class ColliderDef
    {
        public int Node;
        public Vector3 Offset;
        public float Radius;
        public bool Capsule;
        public Vector3 Tail;
    }

    public struct BlendBind
    {
        public int Renderer; // transform index of the SkinnedMeshRenderer
        public int Index;
        public float Weight; // Unity weight 0..100
    }
}
