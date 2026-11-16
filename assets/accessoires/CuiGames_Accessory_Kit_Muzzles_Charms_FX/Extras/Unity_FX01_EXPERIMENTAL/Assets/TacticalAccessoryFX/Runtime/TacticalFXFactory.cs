using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace TacticalAccessoryFX
{
    /// <summary>Loads native FBX resources and normalizes them from exported marker positions.</summary>
    public static class TacticalFXFactory
    {
        public static FXCatalog LoadCatalog()
        {
            TextAsset text = Resources.Load<TextAsset>("TacticalFX/catalog");
            if (text == null) throw new InvalidOperationException("Missing Resources/TacticalFX/catalog.json. Import the complete TacticalAccessoryFX package.");
            FXCatalog catalog = JsonUtility.FromJson<FXCatalog>(text.text);
            if (catalog == null || catalog.schemaVersion != 1 || catalog.entries == null)
                throw new InvalidOperationException("Tactical FX catalog is missing entries or uses an unsupported schema version.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (FXEntry entry in catalog.entries)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || !ids.Add(entry.id))
                    throw new InvalidOperationException("Tactical FX catalog has a missing or duplicate entry id.");
            }
            return catalog;
        }

        public static TacticalFXController Create(FXEntry entry, Transform parent = null)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            GameObject source = Resources.Load<GameObject>(entry.modelResource);
            if (source == null) throw new InvalidOperationException("Missing native FBX resource for " + entry.id + ": " + entry.modelResource);
            GameObject wrapper = new GameObject("TFX_" + entry.id);
            wrapper.transform.SetParent(parent, false);
            try
            {
                // A separate frame preserves the imported FBX root's own coordinate conversion.
                Transform frame = new GameObject("Model").transform;
                frame.SetParent(wrapper.transform, false);
                GameObject model = UnityEngine.Object.Instantiate(source, frame, false);
                model.name = entry.id + "_NativeFBX";
                Normalize(frame, entry.id);
                TacticalFXController controller = wrapper.AddComponent<TacticalFXController>();
                controller.Configure(entry, frame);
                return controller;
            }
            catch
            {
                DestroyOwned(wrapper);
                throw;
            }
        }

        private static void Normalize(Transform frame, string entryId)
        {
            Transform origin = FindMarker(frame, "TFX_ORIGIN");
            Transform front = FindMarker(frame, "TFX_AXIS_FRONT");
            Transform up = FindMarker(frame, "TFX_AXIS_UP");
            Transform right = FindMarker(frame, "TFX_AXIS_RIGHT");
            if (origin == null || front == null || up == null || right == null)
                throw new InvalidOperationException(entryId + ": FBX normalization markers are missing. Reimport the complete marked FBX, not a legacy GLB.");
            Vector3 p = frame.InverseTransformPoint(origin.position);
            Vector3 f = frame.InverseTransformPoint(front.position) - p;
            Vector3 u = frame.InverseTransformPoint(up.position) - p;
            Vector3 r = frame.InverseTransformPoint(right.position) - p;
            float unit = f.magnitude;
            if (unit < 0.000001f || u.magnitude < 0.000001f || r.magnitude < 0.000001f ||
                Mathf.Abs(u.magnitude / unit - 1f) > 0.01f || Mathf.Abs(r.magnitude / unit - 1f) > 0.01f)
                throw new InvalidOperationException(entryId + ": marker axes are zero length or have inconsistent unit scales.");
            f.Normalize(); u.Normalize(); r.Normalize();
            if (Mathf.Abs(Vector3.Dot(f, u)) > 0.01f || Mathf.Abs(Vector3.Dot(f, r)) > 0.01f ||
                Mathf.Abs(Vector3.Dot(u, r)) > 0.01f || Vector3.Dot(Vector3.Cross(u, f), r) < 0.99f)
                throw new InvalidOperationException(entryId + ": imported FBX marker frame is not orthogonal/right handed in Unity. Check FBX axis conversion and negative scale; geometry was not guessed or mirrored.");
            Quaternion correction = Quaternion.Inverse(Quaternion.LookRotation(f, u));
            frame.localRotation = correction;
            frame.localScale = Vector3.one / unit;
            frame.localPosition = -(correction * p) / unit;
        }

        internal static Transform FindMarker(Transform root, string name)
        {
            string key = NameKey(name);
            foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
                if (NameKey(child.name) == key) return child;
            return null;
        }

        // FBX exporters may replace punctuation/spaces, prepend namespaces, or add (Instance).
        public static string NameKey(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            int colon = value.LastIndexOf(':');
            if (colon >= 0) value = value.Substring(colon + 1);
            value = value.Replace("(Instance)", "");
            var result = new StringBuilder(value.Length);
            foreach (char c in value) if (char.IsLetterOrDigit(c)) result.Append(char.ToLowerInvariant(c));
            return result.ToString();
        }

        internal static FXMaterial FindMaterial(FXEntry entry, string importedName)
        {
            string key = NameKey(importedName);
            if (entry.materials == null) return null;
            foreach (FXMaterial material in entry.materials)
                if (material != null && NameKey(material.name) == key) return material;
            // Unity sometimes appends an import-only numeric suffix. Match only an unambiguous prefix.
            FXMaterial candidate = null;
            foreach (FXMaterial material in entry.materials)
            {
                if (material == null) continue;
                string sourceKey = NameKey(material.name);
                if (!key.StartsWith(sourceKey, StringComparison.Ordinal) || key.Length == sourceKey.Length) continue;
                bool numeric = true;
                for (int i = sourceKey.Length; i < key.Length; ++i) numeric &= char.IsDigit(key[i]);
                if (!numeric) continue;
                if (candidate != null) return null;
                candidate = material;
            }
            return candidate;
        }

        public static Material CreatePBRMaterial(FXMaterial source)
        {
            if (source == null) throw new ArgumentNullException("source");
            bool srp = GraphicsSettings.currentRenderPipeline != null;
            Shader shader = srp ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("No supported Lit shader found. Include URP/Lit or built-in Standard.");
            Material material = new Material(shader) { name = source.name + "_TFX" };
            Color color = FXEntry.ReadColor(source.baseColor, Color.gray);
            color.a = Mathf.Clamp01(source.alpha);
            // Catalog RGB values are linear artist values. Unity color properties are stored linear.
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", Mathf.Clamp01(source.metallic));
            float smoothness = 1f - Mathf.Clamp01(source.roughness);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            if (color.a < 0.999f) SetTransparent(material);
            return material;
        }

        internal static void SetTransparent(Material material)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 3f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.EnableKeyword("_ALPHABLEND_ON");
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            material.renderQueue = (int)RenderQueue.Transparent;
        }

        internal static void DestroyOwned(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(value);
            else UnityEngine.Object.DestroyImmediate(value);
        }
    }
}
