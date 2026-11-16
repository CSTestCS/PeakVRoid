using System;
using System.Collections.Generic;
using System.Linq;
using PeakVRoid.Vrm;

// Parses each VRM given on the command line and exercises every accessor the mod reads.
int failures = 0;
foreach (var path in args)
{
    try
    {
        var doc = GltfDocument.Load(path);
        string ver = doc.IsVrm1 ? "VRM 1.0" : doc.IsVrm0 ? "VRM 0.x" : "plain glTF";
        Console.WriteLine($"== {System.IO.Path.GetFileName(path)}: {ver}, {doc.Nodes.Count} nodes, {doc.Meshes.Count} meshes, " +
                          $"{doc.Materials.Count} materials, {doc.Images.Count} images");

        int bones;
        if (doc.IsVrm1) bones = doc.Vrm1.Obj("humanoid").Obj("humanBones").Count;
        else bones = doc.Vrm0.Obj("humanoid").Arr("humanBones").Count;
        Console.WriteLine($"   humanoid bones: {bones}");
        if (bones < 15) throw new Exception("too few humanoid bones");

        long verts = 0, tris = 0, shapes = 0;
        foreach (var m in doc.Meshes.Objs())
            foreach (var p in m.Arr("primitives").Objs())
            {
                var a = p.Obj("attributes");
                var pos = doc.ReadFloats(a.Int("POSITION"), out int pc);
                if (pc != 3 || pos.Length == 0) throw new Exception("bad POSITION");
                verts += pos.Length / 3;
                foreach (var key in new[] { "NORMAL", "TEXCOORD_0", "JOINTS_0", "WEIGHTS_0" })
                    if (a.Has(key))
                    {
                        var f = doc.ReadFloats(a.Int(key), out int c);
                        if (f.Length / c != pos.Length / 3) throw new Exception(key + " count mismatch");
                        if (f.Any(float.IsNaN)) throw new Exception(key + " has NaN");
                    }
                if (p.Has("indices"))
                {
                    var idx = doc.ReadIndices(p.Int("indices"));
                    if (idx.Any(i => i < 0 || i >= pos.Length / 3)) throw new Exception("index out of range");
                    tris += idx.Length / 3;
                }
                var targets = p.Arr("targets");
                if (targets != null)
                    foreach (var t in targets.Objs())
                        if (t.Has("POSITION")) { doc.ReadFloats(t.Int("POSITION"), out _); shapes++; }
            }
        Console.WriteLine($"   {verts} vertices, {tris} triangles, {shapes} morph target reads");

        foreach (var s in doc.Skins.Objs())
            if (s.Has("inverseBindMatrices"))
            {
                var ibm = doc.ReadFloats(s.Int("inverseBindMatrices"), out int c);
                if (c != 16 || ibm.Length / 16 != s.Arr("joints").Count) throw new Exception("IBM count mismatch");
            }

        int imgBytes = 0;
        for (int i = 0; i < doc.Images.Count; i++)
        {
            var b = doc.ImageBytes(i, out string mime);
            bool png = b.Length > 4 && b[0] == 0x89 && b[1] == 'P';
            bool jpg = b.Length > 2 && b[0] == 0xFF && b[1] == 0xD8;
            if (!png && !jpg) throw new Exception($"image {i} ({mime}) is not PNG/JPEG");
            imgBytes += b.Length;
        }
        Console.WriteLine($"   images OK ({imgBytes / 1024} KiB)");

        int springs = 0, blink = 0;
        if (doc.IsVrm1)
        {
            springs = doc.Vrm1Spring?.Arr("springs")?.Count ?? 0;
            blink = doc.Vrm1.Obj("expressions").Obj("preset").Obj("blink").Arr("morphTargetBinds")?.Count ?? 0;
        }
        else
        {
            springs = doc.Vrm0.Obj("secondaryAnimation").Arr("boneGroups")?.Count ?? 0;
            var g = doc.Vrm0.Obj("blendShapeMaster").Arr("blendShapeGroups").Objs()
                .FirstOrDefault(x => (x.Str("presetName") ?? "").ToLowerInvariant() == "blink");
            blink = g?.Arr("binds")?.Count ?? 0;
        }
        Console.WriteLine($"   spring groups: {springs}, blink binds: {blink}");
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine($"!! {path}: {e}");
    }
}
Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
