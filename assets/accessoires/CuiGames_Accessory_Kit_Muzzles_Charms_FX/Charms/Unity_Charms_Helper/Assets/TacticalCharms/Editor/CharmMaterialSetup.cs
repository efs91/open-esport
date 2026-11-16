using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TacticalCharms.Editor
{
    // Native FBX preparation only. Transmissive glass is an explicit alpha preview approximation.
    public static class CharmMaterialSetup
    {
        private const string Root = "Assets/TacticalCharms/";
        [Serializable] private sealed class Library { public Entry[] assets = new Entry[0]; }
        [Serializable] private sealed class Entry { public string id = ""; public MaterialRecipe[] materials = new MaterialRecipe[0]; }
        [Serializable] private sealed class MaterialRecipe
        {
            public string name = ""; public float[] baseColor = new float[] { .5f, .5f, .5f, 1f }; public float metallic = 0f; public float roughness = .5f;
            public float[] emission = new float[] { 0f, 0f, 0f }; public float transmission = 0f; public bool doubleSided = false; public string baseTexture = "";
            public string metallicSmoothnessTexture = ""; public string normalTexture = ""; public float normalScale = 1f;
            public float clearcoat = 0f; public float clearcoatRoughness = .2f;
        }
        [MenuItem("Tools/Tactical Charms/Build Materials and Prefabs")]
        public static void Build()
        {
            TextAsset text = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "MaterialRecipes.json");
            if (text == null) throw new FileNotFoundException("Import the complete TacticalCharms folder, including MaterialRecipes.json.");
            Library library = JsonUtility.FromJson<Library>(text.text);
            if (library == null || library.assets == null || library.assets.Length != 3) throw new InvalidDataException("Expected three charm entries.");
            Directory.CreateDirectory(Root + "Generated/Materials"); Directory.CreateDirectory(Root + "Generated/Prefabs"); AssetDatabase.Refresh();
            Scene preview = EditorSceneManager.NewPreviewScene();
            try
            {
                foreach (Entry entry in library.assets)
                {
                    string path = Root + "Models/" + entry.id + ".fbx";
                    ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                    if (importer == null) throw new FileNotFoundException("Missing native FBX: " + path);
                    bool changed = importer.importAnimation || importer.importLights || importer.importCameras || importer.meshCompression != ModelImporterMeshCompression.Off;
                    importer.importAnimation = false; importer.importLights = false; importer.importCameras = false; importer.meshCompression = ModelImporterMeshCompression.Off;
                    if (changed) importer.SaveAndReimport();
                    GameObject model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    GameObject wrapper = new GameObject(entry.id); SceneManager.MoveGameObjectToScene(wrapper, preview);
                    try
                    {
                        GameObject frame = new GameObject("Model"); frame.transform.SetParent(wrapper.transform, false);
                        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(model, preview); instance.transform.SetParent(frame.transform, false);
                        Normalize(frame.transform);
                        var materials = new Dictionary<string, Material>(StringComparer.Ordinal);
                        foreach (MaterialRecipe recipe in entry.materials)
                        {
                            Material generated = CreateMaterial(recipe);
                            string matPath = Root + "Generated/Materials/" + entry.id + "_" + Key(recipe.name) + ".mat";
                            Material saved = AssetDatabase.LoadAssetAtPath<Material>(matPath);
                            if (saved == null) { AssetDatabase.CreateAsset(generated, matPath); saved = generated; }
                            else { EditorUtility.CopySerialized(generated, saved); UnityEngine.Object.DestroyImmediate(generated); }
                            materials.Add(Key(recipe.name), saved);
                        }
                        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                        {
                            Material[] slots = renderer.sharedMaterials;
                            for (int i = 0; i < slots.Length; ++i)
                            {
                                Material replacement;
                                if (slots[i] == null || !materials.TryGetValue(Key(slots[i].name), out replacement))
                                    throw new InvalidDataException(entry.id + ": unmatched material on " + renderer.name + ": " + (slots[i] == null ? "null" : slots[i].name));
                                slots[i] = replacement;
                            }
                            renderer.sharedMaterials = slots;
                        }
                        bool savedPrefab; PrefabUtility.SaveAsPrefabAsset(wrapper, Root + "Generated/Prefabs/" + entry.id + ".prefab", out savedPrefab);
                        if (!savedPrefab) throw new IOException("Could not save prefab: " + entry.id);
                    }
                    finally { UnityEngine.Object.DestroyImmediate(wrapper); }
                }
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
                Debug.Log("Tactical Charms: three native-FBX prefabs generated. Glass uses alpha transparency as a preview approximation; configure project-specific refraction if required. Verify imported scale, materials and appearance in Play Mode.");
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }
        private static string Key(string name)
        {
            name = name.Replace(" (Instance)", ""); int colon = name.LastIndexOf(':'); if (colon >= 0) name = name.Substring(colon + 1);
            var b = new StringBuilder(); foreach (char c in name) if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c)); return b.ToString();
        }
        private static Transform Marker(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (Key(t.name) == Key(name)) return t;
            throw new InvalidDataException("Missing charm marker: " + name);
        }
        private static void Normalize(Transform frame)
        {
            Vector3 p = frame.InverseTransformPoint(Marker(frame, "CHARM_ROOT").position);
            Vector3 f = frame.InverseTransformPoint(Marker(frame, "CHARM_FRONT").position) - p;
            Vector3 u = frame.InverseTransformPoint(Marker(frame, "CHARM_UP").position) - p;
            float unit = f.magnitude / 0.01f;
            if (unit < 0.000001f || Mathf.Abs(u.magnitude / f.magnitude - 1f) > 0.01f || Mathf.Abs(Vector3.Dot(f.normalized, u.normalized)) > 0.01f)
                throw new InvalidDataException("Charm marker basis is invalid; original model was not rescaled or mirrored.");
            Quaternion q = Quaternion.Inverse(Quaternion.LookRotation(f.normalized, u.normalized));
            frame.localRotation = q; frame.localScale = Vector3.one / unit; frame.localPosition = -(q * p) / unit;
        }
        private static Texture2D LoadTexture(string relativePath, bool normal)
        {
            string path = Root + relativePath;
            TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) throw new FileNotFoundException("Missing texture: " + path);
            TextureImporterType kind = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (importer.textureType != kind || importer.sRGBTexture || !importer.mipmapEnabled || importer.alphaIsTransparency)
            {
                importer.textureType = kind; importer.sRGBTexture = false; importer.mipmapEnabled = true; importer.alphaIsTransparency = false;
                importer.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        private static Material CreateMaterial(MaterialRecipe recipe)
        {
            bool urp = GraphicsSettings.currentRenderPipeline != null;
            if (urp && !GraphicsSettings.currentRenderPipeline.GetType().Name.Contains("Universal"))
                throw new InvalidOperationException("This helper supports built-in Standard or URP; other render pipelines require project material setup.");
            Shader shader = urp ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
            if (urp && recipe.clearcoat > 0f)
            {
                Shader coated = Shader.Find("Universal Render Pipeline/Complex Lit");
                if (coated != null && coated.isSupported) shader = coated;
            }
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Use built-in Standard or install URP; HDRP is not configured by this helper.");
            Material m = new Material(shader) { name = recipe.name };
            float[] c = recipe.baseColor ?? new float[] { .5f, .5f, .5f, 1f };
            float alpha = c.Length > 3 ? c[3] : 1f;
            if (recipe.transmission > 0f) alpha = Mathf.Lerp(1f, .20f, Mathf.Clamp01(recipe.transmission));
            Color color = new Color(c[0], c[1], c[2], alpha);
            if (m.HasProperty("_Color")) m.SetColor("_Color", color); if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", recipe.metallic);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f - recipe.roughness); if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 1f - recipe.roughness);
            if (!string.IsNullOrEmpty(recipe.metallicSmoothnessTexture))
            {
                Texture2D packed = LoadTexture(recipe.metallicSmoothnessTexture, false);
                if (m.HasProperty("_MetallicGlossMap")) m.SetTexture("_MetallicGlossMap", packed);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 1f);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 1f);
                if (m.HasProperty("_GlossMapScale")) m.SetFloat("_GlossMapScale", 1f);
                m.EnableKeyword(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
            }
            if (!string.IsNullOrEmpty(recipe.normalTexture))
            {
                Texture2D normal = LoadTexture(recipe.normalTexture, true);
                if (m.HasProperty("_BumpMap")) m.SetTexture("_BumpMap", normal);
                if (m.HasProperty("_BumpScale")) m.SetFloat("_BumpScale", recipe.normalScale);
                m.EnableKeyword("_NORMALMAP");
            }
            if (recipe.clearcoat > 0f)
            {
                if (m.HasProperty("_ClearCoatMask"))
                {
                    m.SetFloat("_ClearCoatMask", recipe.clearcoat);
                    if (m.HasProperty("_ClearCoatSmoothness")) m.SetFloat("_ClearCoatSmoothness", 1f - recipe.clearcoatRoughness);
                    m.EnableKeyword("_CLEARCOAT");
                }
                else Debug.LogWarning(recipe.name + ": clearcoat not represented by this fallback shader; Blender/GLB retain the authored layer.");
            }
            if (!string.IsNullOrEmpty(recipe.baseTexture))
            {
                Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(Root + recipe.baseTexture);
                if (texture == null) throw new FileNotFoundException("Missing base-color texture: " + recipe.baseTexture);
                if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture); if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
            }
            if (recipe.emission != null && recipe.emission.Length >= 3 && m.HasProperty("_EmissionColor"))
            {
                Color e = new Color(recipe.emission[0], recipe.emission[1], recipe.emission[2], 1f); m.SetColor("_EmissionColor", e);
                if (e.maxColorComponent > 0f) m.EnableKeyword("_EMISSION");
            }
            if (alpha < .999f)
            {
                m.SetOverrideTag("RenderType", "Transparent");
                if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f); if (m.HasProperty("_Mode")) m.SetFloat("_Mode", 2f);
                if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha); if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f); m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT"); m.EnableKeyword("_ALPHABLEND_ON"); m.renderQueue = (int)RenderQueue.Transparent;
            }
            if (recipe.doubleSided) { if (m.HasProperty("_Cull")) m.SetFloat("_Cull", (float)CullMode.Off); if (m.HasProperty("_CullMode")) m.SetFloat("_CullMode", (float)CullMode.Off); }
            return m;
        }
    }
}
