using Godot;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;

internal static class ClothingCoverage
{
    private static readonly Dictionary<string, int> Layers = new();
    private sealed record Surface(Vector3[] Positions, int[] Flags, int Used);
    private static readonly Dictionary<string, Surface> Surfaces = new();
    private static readonly ConditionalWeakTable<ArrayMesh, Dictionary<int, WeakReference<ArrayMesh>>> Cache = new();

    public static int Equipped(HashSet<string> visible)
    {
        if (Layers.Count == 0) Load();
        int mask = 0;
        foreach (var name in visible) mask |= Layers.GetValueOrDefault(name);
        return mask;
    }

    public static int Layer(string name) => Layers.GetValueOrDefault(name);

    private static void Load()
    {
        using var file = new MemoryStream(Godot.FileAccess.GetFileAsBytes(CharacterKit.Folder + "coverage.bytes"));
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip);
        int layers = reader.ReadInt32();
        for (int i = 0; i < layers; i++) Layers.Add(reader.ReadString(), 1 << i);
        int surfaces = reader.ReadInt32();
        for (int s = 0; s < surfaces; s++)
        {
            string name = reader.ReadString();
            int count = reader.ReadInt32();
            var positions = new Vector3[count];
            var flags = new int[count];
            for (int i = 0; i < count; i++)
                positions[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int used = 0;
            for (int i = 0; i < count; i++) used |= flags[i] = reader.ReadInt32();
            Surfaces.Add(name, new Surface(positions, flags, used));
        }
    }

    // Each kit piece has one indexed surface. Keep the original vertex order for morphs and skinning.
    public static ArrayMesh Apply(ArrayMesh source, string name, int equipped)
    {
        if (!Surfaces.TryGetValue(name + "_0", out var surface)) return source;
        int active = surface.Used & equipped;
        if (active == 0) return source;
        var variants = Cache.GetOrCreateValue(source);
        if (variants.TryGetValue(active, out var weak) && weak.TryGetTarget(out var cached)
            && GodotObject.IsInstanceValid(cached)) return cached;

        var arrays = source.SurfaceGetArrays(0);
        var vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        var hidden = new bool[vertices.Length];
        Dictionary<Vector3, int> byPosition = null;
        for (int i = 0; i < vertices.Length; i++)
        {
            int flags;
            if (i < surface.Positions.Length && vertices[i] == surface.Positions[i]) flags = surface.Flags[i];
            else
            {
                // Another platform's importer can reorder or split vertices at UV seams.
                if (byPosition == null)
                {
                    byPosition = new();
                    for (int j = 0; j < surface.Flags.Length; j++) byPosition[surface.Positions[j]] = surface.Flags[j];
                }
                byPosition.TryGetValue(vertices[i], out flags);
            }
            hidden[i] = (active & flags) != 0;
        }
        var indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        bool hairCards = name.StartsWith("h_", StringComparison.Ordinal);
        var keep = new List<int>(indices.Length);
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i+1], c = indices[i+2];
            if (hairCards ? hidden[a] || hidden[b] || hidden[c] : hidden[a] && hidden[b] && hidden[c]) continue;
            keep.Add(a);
            keep.Add(b);
            keep.Add(c);
        }
        var result = source;
        if (keep.Count != indices.Length)
        {
            result = new ArrayMesh { BlendShapeMode = source.BlendShapeMode };
            if (keep.Count > 0)
            {
                for (int i = 0; i < source.GetBlendShapeCount(); i++) result.AddBlendShape(source.GetBlendShapeName(i));
                arrays[(int)Mesh.ArrayType.Index] = keep.ToArray();
                result.AddSurfaceFromArrays(source.SurfaceGetPrimitiveType(0), arrays, source.SurfaceGetBlendShapeArrays(0));
                result.SurfaceSetMaterial(0, source.SurfaceGetMaterial(0));
            }
        }
        variants[active] = new(result);
        return result;
    }
}
