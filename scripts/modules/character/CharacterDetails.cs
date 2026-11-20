using Godot;
using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;

// The atlas stores MakeHuman vertex displacements once. Clothes and face attachments
// interpolate those same displacements with their original MakeHuman proxy weights.
public static class CharacterDetails
{
    public sealed class Dial
    {
        public string Id, Page, Section, Label;
        public bool Signed, Face;
    }
    public static readonly List<Dial> Dials = new();
    private readonly record struct Delta(ushort Vertex, Vector3 Move);
    private sealed class Binding
    {
        public Vector3[] Rest;
        public ushort[] References;
        public float[] Weights;
    }
    private static readonly Dictionary<string,ushort[]> Landmarks = new();
    private static Delta[][] _targets;
    private static int _vertexCount;
    private static readonly Dictionary<string, Binding> Bindings = new();

    public static void LoadCatalog()
    {
        if (Dials.Count > 0) return;
        using var file = Godot.FileAccess.Open(CharacterKit.Folder + "details.json", Godot.FileAccess.ModeFlags.Read);
        foreach (var entry in Json.ParseString(file.GetAsText()).AsGodotArray())
        {
            var d = entry.AsGodotDictionary();
            Dials.Add(new Dial { Id = (string)d["id"], Page = (string)d["page"], Section = (string)d["section"],
                Label = (string)d["label"], Signed = (bool)d["signed"], Face = (bool)d["face"] });
        }
    }

    private static void LoadAtlas()
    {
        if (_targets != null) return;
        LoadCatalog();
        using var packed = new MemoryStream(Godot.FileAccess.GetFileAsBytes(CharacterKit.Folder + "details.bytes"));
        using var stream = new GZipStream(packed, CompressionMode.Decompress);
        using var r = new BinaryReader(stream);
        int count = r.ReadInt32();
        if (count != Dials.Count) throw new InvalidDataException("Incompatible MakeHuman catalog");
        _vertexCount = r.ReadInt32();
        var targets = new Delta[count * 2][];
        for (int i = 0; i < targets.Length; i++)
        {
            var entries = new Delta[r.ReadInt32()];
            for (int j = 0; j < entries.Length; j++)
                entries[j] = new Delta(r.ReadUInt16(), new Vector3(r.ReadInt16(), r.ReadInt16(), r.ReadInt16()) * .0001f);
            targets[i] = entries;
        }
        int surfaces = r.ReadInt32();
        for (int i = 0; i < surfaces; i++)
        {
            string name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
            int n = r.ReadInt32();
            var binding = new Binding { Rest = new Vector3[n], References = new ushort[n * 3], Weights = new float[n * 3] };
            for (int j = 0; j < n; j++) binding.Rest[j] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            for (int j = 0; j < n * 3; j++) binding.References[j] = r.ReadUInt16();
            for (int j = 0; j < n * 3; j++) binding.Weights[j] = r.ReadSingle();
            Bindings.Add(name, binding);
        }
        int joints = r.ReadInt32();
        for (int i = 0; i < joints; i++)
        {
            string name = System.Text.Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
            var refs = new ushort[r.ReadInt32()];
            for (int j = 0; j < refs.Length; j++) refs[j] = r.ReadUInt16();
            Landmarks.Add(name,refs);
        }
        _targets = targets;
    }

    public static Vector3[] Displacements(CharacterAppearance look)
    {
        if (look.Details.Count == 0) return null;
        LoadAtlas();
        var moves = new Vector3[_vertexCount];
        for (int i = 0; i < Dials.Count; i++)
        {
            float weight = look.Detail(Dials[i].Id);
            if (weight == 0) continue;
            var entries = _targets[i * 2 + (weight > 0 ? 1 : 0)];
            foreach (var entry in entries) moves[entry.Vertex] += entry.Move * Math.Abs(weight);
        }
        // Stacking several full-body presets must leave a playable silhouette.
        for (int i = 0; i < moves.Length; i++) moves[i] = moves[i].LimitLength(.12f);
        return moves;
    }

    public static Dictionary<string,Vector3> JointOffsets(Vector3[] moves)
    {
        var result = new Dictionary<string,Vector3>();
        foreach (var (name, refs) in Landmarks)
        {
            Vector3 offset = Vector3.Zero;
            foreach (int index in refs) offset += moves[index];
            result[name] = offset / refs.Length;
        }
        return result;
    }

    public static ArrayMesh Fit(ArrayMesh source, MeshInstance3D part, Vector3[] moves, Dictionary<string,float> macros, Dictionary<string,Vector3> jointOffsets)

    {
        var mesh = new ArrayMesh();
        var skin = part.Skin;
        var offsets = new Vector3[skin.GetBindCount()];
        var skeleton = part.GetNode<Skeleton3D>(part.Skeleton);
        for (int k = 0; k < offsets.Length; k++)
        {
            string bone = skin.GetBindName(k);
            if (string.IsNullOrEmpty(bone)) bone = skeleton.GetBoneName(skin.GetBindBone(k));
            jointOffsets.TryGetValue(bone,out offsets[k]);
        }
        for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
        {
            var a = source.SurfaceGetArrays(surface);
            var rest = a[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            var verts = (Vector3[])rest.Clone();
            var name = part.Name.ToString();
            var bind = Bindings[name + "_" + surface];
            var bones = a[(int)Mesh.ArrayType.Bones].AsInt32Array();
            var weights = a[(int)Mesh.ArrayType.Weights].AsFloat32Array();
            int influences = bones.Length / rest.Length;
            Dictionary<Vector3,int> byPosition = null;
            for (int j = 0; j < verts.Length; j++)
            {
                int index = j;
                if (j >= bind.Rest.Length || !rest[j].IsEqualApprox(bind.Rest[j]))
                {
                    // Import optimization may reorder or split seam vertices on another platform.
                    if (byPosition == null)
                    {
                        byPosition = new Dictionary<Vector3,int>();
                        for (int k = 0; k < bind.Rest.Length; k++) byPosition[bind.Rest[k]] = k;
                    }
                    if (!byPosition.TryGetValue(rest[j], out index)) throw new InvalidDataException("Regenerate MakeHuman bindings for " + name);
                }
                for (int k = j*influences; k < (j+1)*influences; k++)
                    verts[j] -= offsets[bones[k]] * weights[k];
                for (int k = index*3; k < index*3+3; k++) verts[j] += moves[bind.References[k]] * bind.Weights[k];
            }
            var shapes = source.SurfaceGetBlendShapeArrays(surface);
            for (int k = 0; k < shapes.Count; k++)
            {
                if (!macros.TryGetValue(source.GetBlendShapeName(k), out float w) || w == 0) continue;
                var shape = shapes[k][(int)Mesh.ArrayType.Vertex].AsVector3Array();
                for (int j = 0; j < verts.Length; j++) verts[j] += (shape[j] - rest[j]) * w;
            }
            var indices = a[(int)Mesh.ArrayType.Index].AsInt32Array();
            var normals = a[(int)Mesh.ArrayType.Normal].AsVector3Array();
            var before = new Vector3[verts.Length];
            var after = new Vector3[verts.Length];
            for (int k = 0; k < indices.Length; k += 3)
            {
                int x = indices[k], y = indices[k+1], z = indices[k+2];
                var n = (rest[z] - rest[x]).Cross(rest[y] - rest[x]);
                var m = (verts[z] - verts[x]).Cross(verts[y] - verts[x]);
                before[x] += n; before[y] += n; before[z] += n;
                after[x] += m; after[y] += m; after[z] += m;
            }
            for (int k = 0; k < normals.Length; k++)
                normals[k] = (normals[k] + after[k].Normalized() - before[k].Normalized()).Normalized();
            a[(int)Mesh.ArrayType.Vertex] = verts;
            a[(int)Mesh.ArrayType.Normal] = normals;
            mesh.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(surface), a);
            mesh.SurfaceSetMaterial(surface,source.SurfaceGetMaterial(surface));
        }
        return mesh;
    }
}
