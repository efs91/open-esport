using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace TacticalAccessoryFX.Editor
{
    /// <summary>Native FBX import and repeatable authoring. No third-party importer is needed.</summary>
    public static class TacticalFXBuilder
    {
        private const string Root = "Assets/TacticalAccessoryFX";
        private const string Generated = Root + "/Generated";
        private const string Prefabs = Generated + "/Prefabs";
        private const string Materials = Generated + "/Materials";
        private const string ShaderReferences = Root + "/Resources/TacticalFX/ShaderReferences";
        private const string DemoScene = Generated + "/TacticalAccessoryFX_Demo.unity";

        [MenuItem("Tools/Tactical Accessory FX/Build Prefabs and Demo", priority = 100)]
        public static void BuildPrefabsAndDemo()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Tactical Accessory FX", "Exit Play Mode before generating assets.", "OK");
                return;
            }
            // Unity presents Save / Don't Save / Cancel for every modified user scene.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            SceneSetup[] previousScenes = EditorSceneManager.GetSceneManagerSetup();
            bool completed = false;
            try
            {
                AssetDatabase.Refresh();
                FXCatalog catalog = TacticalFXFactory.LoadCatalog();
                ValidateCatalog(catalog);
                EnsureFolder(Prefabs);
                EnsureFolder(Materials);
                EnsureFolder(ShaderReferences);
                CreateShaderReferences();
                for (int i = 0; i < catalog.entries.Length; ++i)
                {
                    EditorUtility.DisplayProgressBar("Tactical Accessory FX", "Importing native FBX: " + catalog.entries[i].id, (float)i / catalog.entries.Length * 0.4f);
                    PrepareNativeModel(catalog.entries[i]);
                }
                Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                for (int i = 0; i < catalog.entries.Length; ++i)
                {
                    FXEntry entry = catalog.entries[i];
                    EditorUtility.DisplayProgressBar("Tactical Accessory FX", "Building prefab: " + entry.id, 0.4f + (float)i / catalog.entries.Length * 0.55f);
                    TacticalFXController instance = null;
                    try
                    {
                        instance = TacticalFXFactory.Create(entry);
                        instance.name = entry.id;
                        PersistModelMaterials(instance, entry.id);
                        instance.AdoptCurrentModelMaterials();
                        bool saved;
                        PrefabUtility.SaveAsPrefabAsset(instance.gameObject, Prefabs + "/" + entry.id + ".prefab", out saved);
                        if (!saved) throw new InvalidOperationException("Unity did not save prefab " + entry.id + ".");
                    }
                    finally
                    {
                        if (instance != null) UnityEngine.Object.DestroyImmediate(instance.gameObject);
                    }
                }
                new GameObject("Tactical Accessory FX — press Play", typeof(TacticalFXBootstrap));
                EditorSceneManager.MarkSceneDirty(scene);
                if (!EditorSceneManager.SaveScene(scene, DemoScene)) throw new IOException("Unity did not save the demo scene.");
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                completed = true;
                Debug.Log("Tactical Accessory FX: built " + catalog.entries.Length + " native-FBX prefabs and " + DemoScene + ". Enter Play Mode to inspect the demo. Runtime FX are initialized on enable; generated meshes and render textures are not serialized into the prefabs.");
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScene);
                EditorUtility.DisplayDialog("Tactical Accessory FX", "Built 29 native-FBX prefabs and opened the demo scene.\n\nPress Play for appearance/seat selection, effects, ADS, L01 zoom, night vision, and brightness.\n\nUse Validate Package for asset checks. A real Play Mode and target-pipeline review is still required.", "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Tactical Accessory FX build failed", exception.Message + "\n\nSee the Console for details. Your previous scene setup will be restored.", "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (!completed && previousScenes.Length > 0) EditorSceneManager.RestoreSceneManagerSetup(previousScenes);
            }
        }

        [MenuItem("Tools/Tactical Accessory FX/Validate Package", priority = 110)]
        public static void ValidatePackage()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Tactical Accessory FX", "Run package validation outside Play Mode.", "OK");
                return;
            }
            var failures = new List<string>();
            Scene preview = EditorSceneManager.NewPreviewScene();
            GameObject holder = new GameObject("TacticalFX validation temporary root") { hideFlags = HideFlags.HideAndDontSave };
            SceneManager.MoveGameObjectToScene(holder, preview);
            try
            {
                FXCatalog catalog = TacticalFXFactory.LoadCatalog();
                ValidateCatalog(catalog);
                for (int i = 0; i < catalog.entries.Length; ++i)
                {
                    FXEntry entry = catalog.entries[i];
                    EditorUtility.DisplayProgressBar("Validate Tactical Accessory FX", entry.id, (float)i / catalog.entries.Length);
                    TacticalFXController instance = null;
                    try
                    {
                        GameObject nativeModel = GetNativeModel(entry);
                        ValidateMarkers(nativeModel, entry);
                        foreach (MeshFilter filter in nativeModel.GetComponentsInChildren<MeshFilter>(true))
                            if (filter.sharedMesh != null && !filter.sharedMesh.isReadable)
                                throw new InvalidOperationException("Imported mesh is not readable. Run Build Prefabs and Demo first.");
                        instance = TacticalFXFactory.Create(entry, holder.transform);
                        Bounds bounds = instance.VisualBounds;
                        if (!Finite(bounds.center) || !Finite(bounds.size) || bounds.size.sqrMagnitude <= 0.00000001f)
                            throw new InvalidOperationException("Normalized visual bounds are invalid or empty.");
                        if (instance.IsOptic && instance.EyeAnchor == null)
                            throw new InvalidOperationException("Optic has no runtime eye anchor.");
                        foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>(true))
                            foreach (Material material in renderer.sharedMaterials)
                            {
                                if (material == null || material.shader == null || !material.shader.isSupported)
                                    throw new InvalidOperationException("A model renderer has a missing or unsupported shader.");
                                if (AssetDatabase.Contains(material))
                                    throw new InvalidOperationException("Imported material was not rebuilt from the source catalog: " + renderer.name + " / " + material.name + ". Check material names and catalog definitions.");
                            }
                        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + "/" + entry.id + ".prefab");
                        if (prefab == null) throw new InvalidOperationException("Generated prefab is missing. Run Build Prefabs and Demo.");
                        TacticalFXController saved = prefab.GetComponent<TacticalFXController>();
                        if (saved == null || saved.Entry == null || saved.Entry.id != entry.id)
                            throw new InvalidOperationException("Prefab controller metadata is missing or mismatched.");
                        foreach (Renderer renderer in prefab.GetComponentsInChildren<Renderer>(true))
                            foreach (Material material in renderer.sharedMaterials)
                                if (material == null || !AssetDatabase.Contains(material))
                                    throw new InvalidOperationException("Prefab refers to a transient or missing material.");
                    }
                    catch (Exception exception) { failures.Add(entry.id + ": " + exception.Message); }
                    finally { if (instance != null) UnityEngine.Object.DestroyImmediate(instance.gameObject); }
                }
                if (AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScene) == null) failures.Add("Generated demo scene is missing.");
                ValidateShaderReferences(failures);
                if (failures.Count == 0)
                {
                    Debug.Log("Tactical Accessory FX asset validation passed for 29 seats: native FBX resources, marker basis, readable meshes, normalized bounds, eye anchors, supported model shaders, serialized prefab metadata/materials, demo scene, shader reference assets. This is an Editor asset check; it does not certify Play Mode behavior, URP rendering, shader build variants, or target-platform performance.");
                    EditorUtility.DisplayDialog("Tactical Accessory FX", "Asset validation passed for 29 seats.\n\nEnter Play Mode and review the target render pipeline/platform before accepting the FX branch.", "OK");
                }
                else
                {
                    string details = string.Join("\n", failures.ToArray());
                    Debug.LogError("Tactical Accessory FX validation:\n" + details);
                    EditorUtility.DisplayDialog("Tactical Accessory FX validation", failures.Count + " issue(s). See the Console.\n\n" + failures[0], "OK");
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Tactical Accessory FX validation", exception.Message, "OK");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        private static void ValidateCatalog(FXCatalog catalog)
        {
            if (catalog == null || catalog.schemaVersion != 1 || catalog.entries == null)
                throw new InvalidOperationException("Missing or incompatible Resources/TacticalFX/catalog.json (schemaVersion 1 required).");
            if (catalog.entries.Length != 29) throw new InvalidOperationException("Expected all 29 accepted asset + host seats; catalog has " + catalog.entries.Length + ".");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var assets = new HashSet<string>(StringComparer.Ordinal);
            foreach (FXEntry entry in catalog.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || string.IsNullOrEmpty(entry.asset) || string.IsNullOrEmpty(entry.host))
                    throw new InvalidOperationException("Catalog contains an incomplete entry.");
                if (!ids.Add(entry.id) || entry.id != entry.asset + "_" + entry.host)
                    throw new InvalidOperationException("Duplicate or invalid catalog id: " + entry.id);
                if ((entry.asset == "FL01" || entry.asset == "FG01") && entry.host == "A5_Rail")
                    throw new InvalidOperationException("Catalog invents an unaccepted A5_Rail seat: " + entry.id);
                if (entry.minMagnification < 1f || entry.maxMagnification < entry.minMagnification || entry.magnification < entry.minMagnification || entry.magnification > entry.maxMagnification)
                    throw new InvalidOperationException("Invalid magnification range: " + entry.id);
                assets.Add(entry.asset);
                GetNativeModel(entry);
            }
            if (assets.Count != 11) throw new InvalidOperationException("Expected 11 appearances; catalog has " + assets.Count + ".");
        }

        private static GameObject GetNativeModel(FXEntry entry)
        {
            GameObject model = Resources.Load<GameObject>(entry.modelResource);
            if (model == null) throw new InvalidOperationException("Native FBX resource not found: " + entry.modelResource);
            string path = AssetDatabase.GetAssetPath(model);
            if (!string.Equals(Path.GetExtension(path), ".fbx", StringComparison.OrdinalIgnoreCase) || !(AssetImporter.GetAtPath(path) is ModelImporter))
                throw new InvalidOperationException("Model must be a native imported FBX: " + path);
            return model;
        }

        private static void PrepareNativeModel(FXEntry entry)
        {
            GameObject model = GetNativeModel(entry);
            ModelImporter importer = (ModelImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(model));
            bool changed = !importer.isReadable || importer.importAnimation || importer.animationType != ModelImporterAnimationType.None || importer.importCameras || importer.importLights || importer.meshCompression != ModelImporterMeshCompression.Off;
            importer.isReadable = true;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            if (changed) importer.SaveAndReimport();
            ValidateMarkers(GetNativeModel(entry), entry);
        }

        private static void ValidateMarkers(GameObject model, FXEntry entry)
        {
            var markers = new Dictionary<string, Transform>(StringComparer.Ordinal);
            foreach (Transform item in model.GetComponentsInChildren<Transform>(true))
                if (item.name.StartsWith("TFX_", StringComparison.Ordinal)) markers[item.name] = item;
            foreach (string marker in new[] { "TFX_ORIGIN", "TFX_AXIS_FRONT", "TFX_AXIS_UP", "TFX_AXIS_RIGHT" })
                if (!markers.ContainsKey(marker)) throw new InvalidOperationException("FBX is missing marker " + marker);
            Vector3 origin = markers["TFX_ORIGIN"].position;
            Vector3 forward = markers["TFX_AXIS_FRONT"].position - origin;
            Vector3 up = markers["TFX_AXIS_UP"].position - origin;
            Vector3 right = markers["TFX_AXIS_RIGHT"].position - origin;
            if (forward.sqrMagnitude < 0.000001f || up.sqrMagnitude < 0.000001f || right.sqrMagnitude < 0.000001f || Mathf.Abs(Vector3.Dot(forward.normalized, up.normalized)) > 0.01f || Vector3.Dot(Vector3.Cross(up.normalized, forward.normalized), right.normalized) < 0.99f)
                throw new InvalidOperationException("FBX marker basis is degenerate, nonorthogonal, or has the wrong handedness.");
            bool optic = entry.kind == "holographic" || entry.kind == "scope";
            foreach (string marker in optic ? new[] { "TFX_SOCKET_EYE", "TFX_SOCKET_LENS", "TFX_SOCKET_RETICLE" } : entry.kind == "passive" ? new string[0] : new[] { "TFX_SOCKET_EMITTER" })
                if (!markers.ContainsKey(marker)) throw new InvalidOperationException("FBX is missing marker " + marker);
        }

        private static void PersistModelMaterials(TacticalFXController controller, string id)
        {
            string folder = Materials + "/" + id;
            EnsureFolder(folder);
            var remap = new Dictionary<Material, Material>();
            int index = 0;
            foreach (Renderer renderer in controller.GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                for (int slot = 0; slot < materials.Length; ++slot)
                {
                    Material source = materials[slot];
                    if (source == null || source.shader == null || !source.shader.isSupported)
                        throw new InvalidOperationException(id + " has a missing or unsupported material shader on " + renderer.name + ".");
                    if (AssetDatabase.Contains(source))
                        throw new InvalidOperationException(id + ": imported material was not rebuilt from the source catalog: " + renderer.name + " / " + source.name + ". Check the native FBX material name and its catalog definition.");
                    Material persistent;
                    if (!remap.TryGetValue(source, out persistent))
                    {
                        string path = folder + "/" + index.ToString("D2") + "_" + SafeName(source.name) + ".mat";
                        persistent = AssetDatabase.LoadAssetAtPath<Material>(path);
                        if (persistent == null)
                        {
                            persistent = new Material(source) { name = source.name, hideFlags = HideFlags.None };
                            AssetDatabase.CreateAsset(persistent, path);
                        }
                        else
                        {
                            EditorUtility.CopySerialized(source, persistent);
                            persistent.hideFlags = HideFlags.None;
                            EditorUtility.SetDirty(persistent);
                        }
                        remap.Add(source, persistent);
                        ++index;
                    }
                    materials[slot] = persistent;
                }
                renderer.sharedMaterials = materials;
            }
        }

        private static void CreateShaderReferences()
        {
            var shaders = new HashSet<Shader>();
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { Root + "/Shaders" }))
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                if (shader != null) shaders.Add(shader);
            }
            foreach (string shaderName in new[] { "Standard", "Unlit/Color", "Unlit/Texture", "Universal Render Pipeline/Lit", "Universal Render Pipeline/Unlit" })
            {
                Shader shader = Shader.Find(shaderName);
                if (shader != null) shaders.Add(shader);
            }
            if (shaders.Count == 0) throw new InvalidOperationException("No shaders are available for build references.");
            foreach (Shader shader in shaders)
            {
                string path = ShaderReferences + "/" + SafeName(shader.name) + ".mat";
                Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null) AssetDatabase.CreateAsset(new Material(shader) { name = "Build reference - " + shader.name }, path);
                else { material.shader = shader; EditorUtility.SetDirty(material); }
            }
            // Materials inside Resources retain these Shader.Find dependencies in builds.
            // This does not prove a particular shader variant/platform works.
        }

        private static void ValidateShaderReferences(List<string> failures)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Shader", new[] { Root + "/Shaders" }))
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(AssetDatabase.GUIDToAssetPath(guid));
                if (shader == null) { failures.Add("A custom shader asset could not be loaded."); continue; }
                Material reference = AssetDatabase.LoadAssetAtPath<Material>(ShaderReferences + "/" + SafeName(shader.name) + ".mat");
                if (reference == null || reference.shader != shader) failures.Add("Missing build-inclusion material for " + shader.name + ". Run Build Prefabs and Demo.");
                if (!shader.isSupported) failures.Add("Current editor GPU/pipeline reports unsupported shader " + shader.name + ".");
            }
        }

        private static bool Finite(Vector3 value)
        {
            return !(float.IsNaN(value.x) || float.IsNaN(value.y) || float.IsNaN(value.z) || float.IsInfinity(value.x) || float.IsInfinity(value.y) || float.IsInfinity(value.z));
        }

        private static string SafeName(string value)
        {
            var result = new StringBuilder();
            foreach (char character in value)
                result.Append(char.IsLetterOrDigit(character) || character == '_' || character == '-' ? character : '_');
            return result.Length == 0 ? "Material" : result.ToString();
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
