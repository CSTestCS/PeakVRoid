using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PeakVRoid.Vrm
{
    /// <summary>
    /// A parsed .glb / .vrm container: the glTF JSON plus its binary chunk, with typed
    /// accessor readers. Deliberately has no UnityEngine dependency so it can be unit-tested.
    /// </summary>
    public sealed class GltfDocument
    {
        public Dictionary<string, object> Root { get; private set; }
        public byte[] Bin { get; private set; }

        public List<object> Nodes => Root.Arr("nodes") ?? new List<object>();
        public List<object> Meshes => Root.Arr("meshes") ?? new List<object>();
        public List<object> Skins => Root.Arr("skins") ?? new List<object>();
        public List<object> Materials => Root.Arr("materials") ?? new List<object>();
        public List<object> Textures => Root.Arr("textures") ?? new List<object>();
        public List<object> Images => Root.Arr("images") ?? new List<object>();
        public List<object> Samplers => Root.Arr("samplers") ?? new List<object>();
        public List<object> Accessors => Root.Arr("accessors") ?? new List<object>();
        public List<object> BufferViews => Root.Arr("bufferViews") ?? new List<object>();

        public Dictionary<string, object> Extensions => Root.Obj("extensions");

        /// <summary>VRM 0.x stores its data in extensions.VRM.</summary>
        public Dictionary<string, object> Vrm0 => Extensions.Obj("VRM");

        /// <summary>VRM 1.0 stores its data in extensions.VRMC_vrm.</summary>
        public Dictionary<string, object> Vrm1 => Extensions.Obj("VRMC_vrm");

        public Dictionary<string, object> Vrm1Spring => Extensions.Obj("VRMC_springBone");

        public bool IsVrm0 => Vrm0 != null && Vrm1 == null;
        public bool IsVrm1 => Vrm1 != null;

        public static GltfDocument Load(string path) => Parse(File.ReadAllBytes(path));

        public static GltfDocument Parse(byte[] data)
        {
            if (data.Length < 20) throw new InvalidDataException("File is too small to be a VRM/GLB.");
            uint magic = BitConverter.ToUInt32(data, 0);
            if (magic != 0x46546C67) // "glTF"
                throw new InvalidDataException("Not a binary glTF (.vrm/.glb) file.");
            uint version = BitConverter.ToUInt32(data, 4);
            if (version != 2) throw new InvalidDataException($"Unsupported glTF version {version}.");

            int offset = 12;
            string json = null;
            byte[] bin = null;
            while (offset + 8 <= data.Length)
            {
                int len = BitConverter.ToInt32(data, offset);
                uint type = BitConverter.ToUInt32(data, offset + 4);
                offset += 8;
                if (offset + len > data.Length) throw new InvalidDataException("Truncated GLB chunk.");
                if (type == 0x4E4F534A) // JSON
                    json = Encoding.UTF8.GetString(data, offset, len);
                else if (type == 0x004E4942 && bin == null) // BIN
                {
                    bin = new byte[len];
                    Buffer.BlockCopy(data, offset, bin, 0, len);
                }
                offset += len;
                offset = (offset + 3) & ~3;
            }
            if (json == null) throw new InvalidDataException("GLB has no JSON chunk.");
            var root = Json.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("glTF JSON root is not an object.");
            return new GltfDocument { Root = root, Bin = bin ?? Array.Empty<byte>() };
        }

        public Dictionary<string, object> Node(int i) => Nodes[i] as Dictionary<string, object>;

        // ---------------------------------------------------------------- buffer views

        /// <summary>Returns the bytes of a bufferView (only the GLB-embedded buffer 0 is supported).</summary>
        public ArraySegment<byte> BufferViewBytes(int index, out int byteStride)
        {
            var bv = BufferViews[index] as Dictionary<string, object>;
            int buffer = bv.Int("buffer", 0);
            if (buffer != 0) throw new NotSupportedException("External glTF buffers are not supported in .vrm files.");
            int off = bv.Int("byteOffset", 0);
            int len = bv.Int("byteLength", 0);
            byteStride = bv.Int("byteStride", 0);
            return new ArraySegment<byte>(Bin, off, len);
        }

        public byte[] ImageBytes(int imageIndex, out string mime)
        {
            var img = Images[imageIndex] as Dictionary<string, object>;
            mime = img.Str("mimeType", "image/png");
            int bv = img.Int("bufferView");
            if (bv < 0)
            {
                string uri = img.Str("uri");
                const string marker = ";base64,";
                int k = uri?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
                if (k >= 0) return Convert.FromBase64String(uri.Substring(k + marker.Length));
                throw new NotSupportedException("External image files are not supported in .vrm files.");
            }
            var seg = BufferViewBytes(bv, out _);
            var bytes = new byte[seg.Count];
            Buffer.BlockCopy(seg.Array, seg.Offset, bytes, 0, seg.Count);
            return bytes;
        }

        // ---------------------------------------------------------------- accessors

        public int AccessorCount(int accessor) => (Accessors[accessor] as Dictionary<string, object>).Int("count", 0);

        private static int ComponentCount(string type)
        {
            switch (type)
            {
                case "SCALAR": return 1;
                case "VEC2": return 2;
                case "VEC3": return 3;
                case "VEC4": return 4;
                case "MAT2": return 4;
                case "MAT3": return 9;
                case "MAT4": return 16;
                default: throw new NotSupportedException("Accessor type " + type);
            }
        }

        private static int ComponentSize(int componentType)
        {
            switch (componentType)
            {
                case 5120: case 5121: return 1; // (u)byte
                case 5122: case 5123: return 2; // (u)short
                case 5125: case 5126: return 4; // uint, float
                default: throw new NotSupportedException("Component type " + componentType);
            }
        }

        private static float ReadComponent(byte[] b, int pos, int componentType, bool normalized)
        {
            switch (componentType)
            {
                case 5126: return BitConverter.ToSingle(b, pos);
                case 5121: return normalized ? b[pos] / 255f : b[pos];
                case 5120: return normalized ? Math.Max((sbyte)b[pos] / 127f, -1f) : (sbyte)b[pos];
                case 5123: { ushort v = BitConverter.ToUInt16(b, pos); return normalized ? v / 65535f : v; }
                case 5122: { short v = BitConverter.ToInt16(b, pos); return normalized ? Math.Max(v / 32767f, -1f) : v; }
                case 5125: return BitConverter.ToUInt32(b, pos);
                default: throw new NotSupportedException("Component type " + componentType);
            }
        }

        /// <summary>
        /// Reads any accessor as a flat float array (count * components), handling byteStride,
        /// normalized integers and sparse storage.
        /// </summary>
        public float[] ReadFloats(int accessor, out int components)
        {
            var acc = Accessors[accessor] as Dictionary<string, object>;
            int count = acc.Int("count", 0);
            components = ComponentCount(acc.Str("type", "SCALAR"));
            int ct = acc.Int("componentType", 5126);
            bool normalized = acc.Bool("normalized");
            var result = new float[count * components];

            int bvIndex = acc.Int("bufferView");
            if (bvIndex >= 0)
            {
                var seg = BufferViewBytes(bvIndex, out int stride);
                int csize = ComponentSize(ct);
                int elem = csize * components;
                if (stride == 0) stride = elem;
                int baseOff = seg.Offset + acc.Int("byteOffset", 0);
                for (int i = 0; i < count; i++)
                {
                    int p = baseOff + i * stride;
                    for (int c = 0; c < components; c++)
                        result[i * components + c] = ReadComponent(seg.Array, p + c * csize, ct, normalized);
                }
            }

            var sparse = acc.Obj("sparse");
            if (sparse != null)
            {
                int sc = sparse.Int("count", 0);
                var idx = sparse.Obj("indices");
                var val = sparse.Obj("values");
                var idxSeg = BufferViewBytes(idx.Int("bufferView"), out _);
                var valSeg = BufferViewBytes(val.Int("bufferView"), out _);
                int ict = idx.Int("componentType", 5125);
                int isz = ComponentSize(ict);
                int csize = ComponentSize(ct);
                int ip = idxSeg.Offset + idx.Int("byteOffset", 0);
                int vp = valSeg.Offset + val.Int("byteOffset", 0);
                for (int s = 0; s < sc; s++)
                {
                    int target = (int)ReadComponent(idxSeg.Array, ip + s * isz, ict, false);
                    for (int c = 0; c < components; c++)
                        result[target * components + c] =
                            ReadComponent(valSeg.Array, vp + (s * components + c) * csize, ct, normalized);
                }
            }
            return result;
        }

        public int[] ReadInts(int accessor)
        {
            var f = ReadFloats(accessor, out _);
            var r = new int[f.Length];
            for (int i = 0; i < f.Length; i++) r[i] = (int)f[i];
            return r;
        }

        /// <summary>Index buffers are read separately so large uint indices stay exact.</summary>
        public int[] ReadIndices(int accessor)
        {
            var acc = Accessors[accessor] as Dictionary<string, object>;
            int ct = acc.Int("componentType", 5125);
            if (ct != 5125 || acc.Has("sparse")) return ReadInts(accessor);
            int count = acc.Int("count", 0);
            var seg = BufferViewBytes(acc.Int("bufferView"), out int stride);
            if (stride == 0) stride = 4;
            int baseOff = seg.Offset + acc.Int("byteOffset", 0);
            var r = new int[count];
            for (int i = 0; i < count; i++) r[i] = (int)BitConverter.ToUInt32(seg.Array, baseOff + i * stride);
            return r;
        }
    }
}
