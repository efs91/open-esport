using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TacticalAccessoryFX
{
    /// <summary>Reversible, nonfunctional game-art effects. No input, ballistics, or damage logic.</summary>
    [DisallowMultipleComponent]
    public sealed class TacticalFXController : MonoBehaviour
    {
        [SerializeField] private FXEntry entry;
        [SerializeField] private Transform modelRoot;
        [SerializeField] private bool effectEnabled = true;
        [SerializeField] private bool nightVision;
        [SerializeField] private bool aiming;
        [SerializeField] private float magnification = 1f;
        [SerializeField, Range(0f, 4f)] private float brightness = 1f;
        [SerializeField, Range(10f, 120f)] private float viewBaseFieldOfView = 60f;
        [SerializeField, Range(128, 2048)] private int scopeResolution = 512;
        [SerializeField] private float beamRange = 50f;

        // Layer 31 is reserved only on this instance, never renamed or changed project-wide.
        private const int CaptureExclusionLayer = 31;
        private readonly Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
        private readonly List<Material> ownedModelMaterials = new List<Material>();
        private readonly List<UnityEngine.Object> ownedFXResources = new List<UnityEngine.Object>();
        private readonly Dictionary<Transform, int> originalLayers = new Dictionary<Transform, int>();
        private readonly List<Material> emissiveSurfaces = new List<Material>();
        private Camera viewer;
        private Camera scopeCamera;
        private RenderTexture scopeTexture;
        private GameObject effectsRoot;
        private GameObject display;
        private GameObject reticle;
        private GameObject impact;
        private GameObject cone;
        private Light spot;
        private LineRenderer beam;
        private Material reticleMaterial;
        private Material beamMaterial;
        private Material coneMaterial;
        private Material impactMaterial;
        private Transform eyeAnchor;
        private Transform lensAnchor;
        private Transform reticleAnchor;
        private Transform emitterAnchor;
        private bool runtimeReady;

        public FXEntry Entry { get { return entry; } }
        public bool EffectEnabled { get { return effectEnabled && entry != null && entry.kind != "passive"; } }
        public bool NightVision { get { return nightVision; } }
        public bool Aiming { get { return aiming; } }
        public float Magnification { get { return magnification; } }
        public Transform EyeAnchor { get { return eyeAnchor; } }
        public bool IsOptic { get { return entry != null && (entry.kind == "scope" || entry.kind == "holographic"); } }
        public float ViewBaseFieldOfView
        {
            get { return viewBaseFieldOfView; }
            set { viewBaseFieldOfView = Mathf.Clamp(value, 10f, 120f); UpdateScopeCamera(); }
        }

        /// <summary>World bounds of imported model renderers only; beams and marker empties are excluded.</summary>
        public Bounds VisualBounds
        {
            get
            {
                Bounds result = new Bounds(transform.position, Vector3.zero);
                bool found = false;
                if (modelRoot == null) return result;
                foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
                {
                    if (!found) { result = renderer.bounds; found = true; }
                    else result.Encapsulate(renderer.bounds);
                }
                return result;
            }
        }

        public void Configure(FXEntry value, Transform normalizedModelRoot)
        {
            if (value == null) throw new ArgumentNullException("value");
            if (normalizedModelRoot == null) throw new ArgumentNullException("normalizedModelRoot");
            ReleaseResources();
            entry = value;
            modelRoot = normalizedModelRoot;
            effectEnabled = entry.kind != "passive";
            magnification = Mathf.Clamp(Mathf.Max(1f, entry.magnification), MinimumMagnification(), MaximumMagnification());
            ResolveAnchors();
            ApplyModelMaterials();
            if (Application.isPlaying && isActiveAndEnabled) InitializeRuntime();
        }

        /// <summary>
        /// Editor prefab builder calls this after replacing temporary renderer materials with saved .mat assets.
        /// Saved references become the restoration baseline; unused temporary materials are released.
        /// </summary>
        public void AdoptCurrentModelMaterials()
        {
            var assigned = new HashSet<Material>();
            originalMaterials.Clear();
            if (modelRoot != null)
            {
                foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
                {
                    Material[] current = renderer.sharedMaterials;
                    originalMaterials[renderer] = current;
                    foreach (Material material in current) if (material != null) assigned.Add(material);
                }
            }
            for (int i = ownedModelMaterials.Count - 1; i >= 0; --i)
            {
                Material material = ownedModelMaterials[i];
                if (!assigned.Contains(material))
                {
                    TacticalFXFactory.DestroyOwned(material);
                    ownedModelMaterials.RemoveAt(i);
                }
            }
            // A builder must replace, rather than persist, runtime-owned material instances.
            if (ownedModelMaterials.Count != 0 && !Application.isPlaying)
                Debug.LogWarning("TFX: AdoptCurrentModelMaterials still sees transient owned materials. Replace these with persistent clones before saving a prefab.", this);
        }

        public void SetEffectEnabled(bool value)
        {
            effectEnabled = value && entry != null && entry.kind != "passive";
            RefreshVisibility();
        }

        public void SetNightVision(bool value) { nightVision = value; RefreshVisibility(); }
        public void SetAiming(bool value) { aiming = value; UpdateScopeCamera(); }
        public void SetBrightness(float value) { brightness = Mathf.Clamp(value, 0f, 4f); RefreshVisibility(); }
        public void SetMagnification(float value)
        {
            magnification = Mathf.Clamp(value, MinimumMagnification(), MaximumMagnification());
            UpdateScopeCamera();
        }

        public void SetViewer(Camera value)
        {
            if (viewer != value && value != null) viewBaseFieldOfView = Mathf.Clamp(value.fieldOfView, 10f, 120f);
            viewer = value;
            UpdateScopeCamera();
        }

        private float MinimumMagnification() { return entry == null ? 1f : Mathf.Max(1f, entry.minMagnification); }
        private float MaximumMagnification() { return entry == null ? 1f : Mathf.Max(MinimumMagnification(), entry.maxMagnification); }

        private void OnEnable()
        {
            if (Application.isPlaying && entry != null && modelRoot != null)
            {
                try { InitializeRuntime(); }
                catch (Exception ex)
                {
                    ReleaseResources();
                    Debug.LogError("TFX initialization failed for " + entry.id + ": " + ex.Message, this);
                }
            }
        }

        private void OnDisable() { ReleaseResources(); }
        private void OnDestroy() { ReleaseResources(); }

        private void LateUpdate()
        {
            if (!runtimeReady) return;
            UpdateScopeCamera();
            if (EffectVisible() && (entry.kind == "laser" || entry.kind == "infrared")) UpdateBeam();
            if (EffectVisible() && entry.kind == "flashlight") UpdateFlashlightCone();
        }

        private void ResolveAnchors()
        {
            eyeAnchor = TacticalFXFactory.FindMarker(modelRoot, "TFX_SOCKET_EYE");
            lensAnchor = TacticalFXFactory.FindMarker(modelRoot, "TFX_SOCKET_LENS");
            reticleAnchor = TacticalFXFactory.FindMarker(modelRoot, "TFX_SOCKET_RETICLE");
            emitterAnchor = TacticalFXFactory.FindMarker(modelRoot, "TFX_SOCKET_EMITTER");
            if (IsOptic && (eyeAnchor == null || lensAnchor == null || reticleAnchor == null))
                throw new InvalidOperationException("Optic is missing EYE, LENS or RETICLE socket markers.");
            if (entry != null && (entry.kind == "laser" || entry.kind == "infrared" || entry.kind == "flashlight") && emitterAnchor == null)
                throw new InvalidOperationException("Emitter socket marker is missing.");
            // Markers define positions; imported empty rotations are intentionally ignored.
            Transform[] sockets = { eyeAnchor, lensAnchor, reticleAnchor, emitterAnchor };
            foreach (Transform socket in sockets)
                if (socket != null) socket.rotation = transform.rotation;
        }

        private void ApplyModelMaterials()
        {
            if (modelRoot == null || entry == null) return;
            var cache = new Dictionary<FXMaterial, Material>();
            foreach (Renderer renderer in modelRoot.GetComponentsInChildren<Renderer>(true))
            {
                Material[] original = renderer.sharedMaterials;
                originalMaterials[renderer] = original;
                Material[] replacements = (Material[])original.Clone();
                for (int i = 0; i < original.Length; ++i)
                {
                    if (original[i] == null) continue;
                    string sourceName = original[i].name.Replace(" (Instance)", "");
                    // Saved prefab emitter copies retain their catalog identity across pipeline changes.
                    if (sourceName.EndsWith("_TFX_Emitter", StringComparison.Ordinal))
                        sourceName = sourceName.Substring(0, sourceName.Length - "_TFX_Emitter".Length);
                    else if (sourceName.EndsWith("_TFX", StringComparison.Ordinal))
                        sourceName = sourceName.Substring(0, sourceName.Length - "_TFX".Length);
                    FXMaterial definition = TacticalFXFactory.FindMaterial(entry, sourceName);
                    if (definition == null) continue; // An unknown import material is retained, never guessed.
                    Material replacement;
                    if (!cache.TryGetValue(definition, out replacement))
                    {
                        replacement = TacticalFXFactory.CreatePBRMaterial(definition);
                        cache.Add(definition, replacement);
                        ownedModelMaterials.Add(replacement);
                    }
                    replacements[i] = replacement;
                }
                renderer.sharedMaterials = replacements;
                if (HasSurfaceRole(renderer.transform, "emitter"))
                {
                    for (int i = 0; i < replacements.Length; ++i)
                    {
                        Material material = replacements[i];
                        if (material == null) continue;
                        // Emitters sharing an artist material must not illuminate unrelated casing faces.
                        Material emitterCopy = new Material(material) { name = material.name + "_Emitter" };
                        ownedModelMaterials.Add(emitterCopy);
                        replacements[i] = emitterCopy;
                        emissiveSurfaces.Add(emitterCopy);
                    }
                    renderer.sharedMaterials = replacements;
                }
            }
        }

        private bool HasSurfaceRole(Transform candidate, string role)
        {
            if (entry.surfaces == null) return false;
            foreach (FXSurface surface in entry.surfaces)
            {
                if (surface == null || surface.role != role) continue;
                string key = TacticalFXFactory.NameKey(surface.objectName);
                Transform current = candidate;
                while (current != null && current != modelRoot.parent)
                {
                    if (TacticalFXFactory.NameKey(current.name) == key) return true;
                    current = current.parent;
                }
            }
            return false;
        }

        private void InitializeRuntime()
        {
            if (runtimeReady || !Application.isPlaying || entry == null || modelRoot == null) return;
            ResolveAnchors();
            if (originalMaterials.Count == 0) ApplyModelMaterials();
            if (entry.kind == "passive") { runtimeReady = true; return; }
            effectsRoot = new GameObject("RuntimeFX (generated)");
            effectsRoot.transform.SetParent(transform, false);
            effectsRoot.layer = CaptureExclusionLayer;
            switch (entry.kind)
            {
                case "scope": BuildScope(); BuildReticle(false); break;
                case "holographic": BuildReticle(true); break;
                case "flashlight": BuildFlashlight(); break;
                case "laser": case "infrared": BuildBeam(); break;
                default: throw new InvalidOperationException("Unknown effect kind: " + entry.kind);
            }
            runtimeReady = true;
            RefreshVisibility();
        }

        private Material MakeOverlay(string name)
        {
            Shader shader = Shader.Find("TacticalAccessoryFX/Unlit Overlay");
            if (shader == null) throw new InvalidOperationException("Missing TacticalAccessoryFX/Unlit Overlay shader. Run the prefab builder to preserve shader references in builds.");
            Material material = new Material(shader) { name = name };
            ownedFXResources.Add(material);
            return material;
        }

        private GameObject MakeMeshObject(string name, Mesh mesh, Material material)
        {
            GameObject go = new GameObject(name);
            go.layer = CaptureExclusionLayer;
            go.transform.SetParent(effectsRoot.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            ownedFXResources.Add(mesh);
            return go;
        }

        private void BuildScope()
        {
            foreach (Transform child in modelRoot.GetComponentsInChildren<Transform>(true))
            {
                originalLayers[child] = child.gameObject.layer;
                child.gameObject.layer = CaptureExclusionLayer;
            }
            scopeTexture = new RenderTexture(scopeResolution, scopeResolution, 24, RenderTextureFormat.ARGB32)
            {
                name = entry.id + "_ScopeRT",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            scopeTexture.Create();
            Shader shader = Shader.Find("TacticalAccessoryFX/Scope Display");
            if (shader == null) throw new InvalidOperationException("Missing TacticalAccessoryFX/Scope Display shader.");
            Material material = new Material(shader) { name = entry.id + "_ScopeDisplay" };
            material.SetTexture("_MainTex", scopeTexture);
            material.SetColor("_Tint", Color.white);
            ownedFXResources.Add(material);
            display = MakeMeshObject("Scope world display", MakeDisk(Mathf.Max(0.001f, entry.lensDiameter * 0.49f), 72), material);
            display.transform.position = lensAnchor.position - transform.forward * 0.00008f;
            display.transform.rotation = transform.rotation;
            GameObject cameraObject = new GameObject("Scope capture camera");
            cameraObject.layer = CaptureExclusionLayer;
            cameraObject.transform.SetParent(effectsRoot.transform, false);
            scopeCamera = cameraObject.AddComponent<Camera>();
            scopeCamera.targetTexture = scopeTexture;
            scopeCamera.nearClipPlane = 0.01f;
            scopeCamera.farClipPlane = 200f;
            scopeCamera.allowMSAA = false;
            scopeCamera.allowHDR = true;
            scopeCamera.depth = -20f;
            scopeCamera.clearFlags = CameraClearFlags.Skybox;
            scopeCamera.cullingMask = ~(1 << CaptureExclusionLayer);
            UpdateScopeCamera();
        }

        private void BuildReticle(bool holographic)
        {
            reticleMaterial = MakeOverlay(entry.id + "_Reticle");
            Texture2D texture = MakeReticleTexture(holographic);
            ownedFXResources.Add(texture);
            reticleMaterial.SetTexture("_MainTex", texture);
            reticleMaterial.renderQueue = 3010;
            float radius = Mathf.Max(0.001f, entry.reticleDiameter * 0.5f);
            reticle = MakeMeshObject(holographic ? "Holographic emissive reticle" : "Scope emissive reticle", MakeQuad(radius), reticleMaterial);
            reticle.transform.position = reticleAnchor.position - transform.forward * 0.00018f;
            reticle.transform.rotation = transform.rotation;
        }

        private void BuildBeam()
        {
            beamMaterial = MakeOverlay(entry.id + "_Beam");
            GameObject lineObject = new GameObject(entry.kind == "infrared" ? "NV only IR visualization" : "Raycast clipped laser");
            lineObject.layer = CaptureExclusionLayer;
            lineObject.transform.SetParent(effectsRoot.transform, false);
            beam = lineObject.AddComponent<LineRenderer>();
            beam.sharedMaterial = beamMaterial;
            beam.positionCount = 2;
            beam.useWorldSpace = true;
            beam.startWidth = 0.0015f;
            beam.endWidth = 0.001f;
            beam.numCapVertices = 3;
            beam.shadowCastingMode = ShadowCastingMode.Off;
            beam.receiveShadows = false;
            beam.textureMode = LineTextureMode.Stretch;
            impactMaterial = MakeOverlay(entry.id + "_Impact");
            Texture2D soft = MakeSoftDisc();
            ownedFXResources.Add(soft);
            impactMaterial.SetTexture("_MainTex", soft);
            impact = MakeMeshObject("Nearest-hit visual dot", MakeQuad(0.007f), impactMaterial);
            impact.SetActive(false);
        }

        private void BuildFlashlight()
        {
            GameObject lightObject = new GameObject("Flashlight real SpotLight");
            lightObject.layer = CaptureExclusionLayer;
            lightObject.transform.SetParent(effectsRoot.transform, false);
            lightObject.transform.position = emitterAnchor.position;
            lightObject.transform.rotation = transform.rotation;
            spot = lightObject.AddComponent<Light>();
            spot.type = LightType.Spot;
            spot.spotAngle = 32f;
            spot.innerSpotAngle = 22f;
            spot.range = 20f;
            spot.color = new Color(1f, 0.96f, 0.88f);
            spot.shadows = LightShadows.Soft;
            coneMaterial = MakeOverlay(entry.id + "_RestrainedCone");
            cone = MakeMeshObject("Short low-opacity flashlight volume", MakeCone(24), coneMaterial);
            cone.transform.position = emitterAnchor.position;
            cone.transform.rotation = transform.rotation;
        }

        private bool EffectVisible()
        {
            return EffectEnabled && brightness > 0f && (entry.kind != "infrared" || nightVision);
        }

        private void RefreshVisibility()
        {
            bool visible = EffectVisible();
            if (display != null) display.SetActive(visible);
            if (reticle != null) reticle.SetActive(visible);
            if (beam != null) beam.enabled = visible;
            if (impact != null && !visible) impact.SetActive(false);
            if (cone != null) cone.SetActive(visible);
            if (scopeCamera != null) scopeCamera.enabled = visible;
            if (spot != null) { spot.enabled = visible; spot.intensity = 6f * brightness; }
            Color color = entry == null ? Color.red : entry.GetEffectColor();
            if (entry != null && entry.kind == "infrared") color = new Color(0.32f, 1f, 0.48f, 1f);
            Color emission = new Color(color.r * brightness * 2f, color.g * brightness * 2f, color.b * brightness * 2f, color.a);
            if (reticleMaterial != null) reticleMaterial.SetColor("_Color", emission);
            if (beamMaterial != null) beamMaterial.SetColor("_Color", new Color(emission.r, emission.g, emission.b, 0.32f));
            if (impactMaterial != null) impactMaterial.SetColor("_Color", emission);
            if (coneMaterial != null) coneMaterial.SetColor("_Color", new Color(1f, 0.96f, 0.88f, 0.018f * brightness));
            foreach (Material material in emissiveSurfaces)
            {
                if (material == null || !material.HasProperty("_EmissionColor")) continue;
                material.SetColor("_EmissionColor", visible ? emission : Color.black);
                if (visible) material.EnableKeyword("_EMISSION"); else material.DisableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            if (visible && beam != null) UpdateBeam();
            if (visible && cone != null) UpdateFlashlightCone();
        }

        private void UpdateScopeCamera()
        {
            if (scopeCamera == null || lensAnchor == null) return;
            float baseTan = Mathf.Tan(viewBaseFieldOfView * Mathf.Deg2Rad * 0.5f);
            float captureTan = baseTan / Mathf.Max(1f, magnification);
            if (aiming && viewer != null && display != null)
            {
                // The host ADS camera also zooms. A second /magnification capture would produce
                // approximately m squared on screen. Calibrate through the physical display aperture:
                // total gain = (R/d)*tan(base/2)/(tan(viewer/2)*tan(capture/2)).
                // This also compensates the host's smooth FOV and eye-position transition.
                float radius = display.transform.TransformVector(Vector3.up * (entry.lensDiameter * 0.49f)).magnitude;
                float eyeDistance = Mathf.Max(0.0005f, Mathf.Abs(Vector3.Dot(display.transform.position - viewer.transform.position, transform.forward)));
                float viewerTan = Mathf.Max(0.0001f, Mathf.Tan(viewer.fieldOfView * Mathf.Deg2Rad * 0.5f));
                captureTan = radius / eyeDistance * baseTan / (Mathf.Max(1f, magnification) * viewerTan);
            }
            scopeCamera.fieldOfView = Mathf.Clamp(2f * Mathf.Atan(captureTan) * Mathf.Rad2Deg, 0.05f, 170f);
            scopeCamera.transform.position = lensAnchor.position + transform.forward * 0.002f;
            scopeCamera.transform.rotation = transform.rotation;
            scopeCamera.cullingMask = (viewer != null ? viewer.cullingMask : ~0) & ~(1 << CaptureExclusionLayer);
            if (viewer != null)
            {
                scopeCamera.farClipPlane = viewer.farClipPlane;
                scopeCamera.backgroundColor = viewer.backgroundColor;
                scopeCamera.clearFlags = viewer.clearFlags == CameraClearFlags.Nothing || viewer.clearFlags == CameraClearFlags.Depth ? CameraClearFlags.SolidColor : viewer.clearFlags;
            }
        }

        private bool CastAhead(Vector3 origin, Vector3 direction, float range, out RaycastHit nearest)
        {
            nearest = new RaycastHit();
            float distance = range;
            bool found = false;
            // RaycastAll avoids silently missing the true closest hit when a small nonalloc buffer fills.
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, range, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider == null || hit.collider.transform.IsChildOf(transform) || hit.distance >= distance) continue;
                nearest = hit; distance = hit.distance; found = true;
            }
            return found;
        }

        private void UpdateBeam()
        {
            if (beam == null || emitterAnchor == null) return;
            Vector3 direction = transform.forward;
            Vector3 origin = emitterAnchor.position + direction * 0.0005f;
            RaycastHit hit;
            bool found = CastAhead(origin, direction, Mathf.Max(0.1f, beamRange), out hit);
            beam.SetPosition(0, origin);
            beam.SetPosition(1, found ? hit.point : origin + direction * beamRange);
            impact.SetActive(found && EffectVisible());
            if (found)
            {
                impact.transform.position = hit.point + hit.normal * 0.001f;
                impact.transform.rotation = Quaternion.LookRotation(hit.normal, Mathf.Abs(Vector3.Dot(hit.normal, Vector3.up)) > 0.98f ? Vector3.right : Vector3.up);
            }
        }

        private void UpdateFlashlightCone()
        {
            if (cone == null || emitterAnchor == null) return;
            float length = 0.65f;
            RaycastHit hit;
            if (CastAhead(emitterAnchor.position, transform.forward, length, out hit)) length = Mathf.Max(0.005f, hit.distance - 0.002f);
            float radius = length * Mathf.Tan(16f * Mathf.Deg2Rad);
            cone.transform.localScale = new Vector3(radius, radius, length);
        }

        private void ReleaseResources()
        {
            runtimeReady = false;
            if (scopeCamera != null) { scopeCamera.enabled = false; scopeCamera.targetTexture = null; }
            if (effectsRoot != null) { effectsRoot.SetActive(false); TacticalFXFactory.DestroyOwned(effectsRoot); }
            if (scopeTexture != null) { scopeTexture.Release(); TacticalFXFactory.DestroyOwned(scopeTexture); }
            foreach (UnityEngine.Object resource in ownedFXResources) TacticalFXFactory.DestroyOwned(resource);
            ownedFXResources.Clear();
            foreach (KeyValuePair<Transform, int> item in originalLayers)
                if (item.Key != null) item.Key.gameObject.layer = item.Value;
            originalLayers.Clear();
            foreach (KeyValuePair<Renderer, Material[]> item in originalMaterials)
                if (item.Key != null) item.Key.sharedMaterials = item.Value;
            originalMaterials.Clear();
            foreach (Material material in ownedModelMaterials) TacticalFXFactory.DestroyOwned(material);
            ownedModelMaterials.Clear();
            emissiveSurfaces.Clear();
            scopeCamera = null; scopeTexture = null; effectsRoot = null;
            display = null; reticle = null; beam = null; spot = null; cone = null; impact = null;
            reticleMaterial = null; beamMaterial = null; coneMaterial = null; impactMaterial = null;
        }

        private static Mesh MakeQuad(float radius)
        {
            Mesh mesh = new Mesh { name = "TFX procedural quad" };
            mesh.vertices = new[] { new Vector3(-radius, -radius, 0), new Vector3(-radius, radius, 0), new Vector3(radius, radius, 0), new Vector3(radius, -radius, 0) };
            mesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        private static Mesh MakeDisk(float radius, int segments)
        {
            Vector3[] vertices = new Vector3[segments + 1];
            Vector2[] uv = new Vector2[segments + 1];
            int[] triangles = new int[segments * 3];
            uv[0] = new Vector2(0.5f, 0.5f);
            for (int i = 0; i < segments; ++i)
            {
                float a = i * Mathf.PI * 2f / segments;
                float x = Mathf.Cos(a), y = Mathf.Sin(a);
                vertices[i + 1] = new Vector3(x * radius, y * radius, 0f);
                uv[i + 1] = new Vector2(x * 0.5f + 0.5f, y * 0.5f + 0.5f);
                triangles[i * 3] = 0; triangles[i * 3 + 1] = (i + 1) % segments + 1; triangles[i * 3 + 2] = i + 1;
            }
            Mesh mesh = new Mesh { name = "TFX procedural lens display" };
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        private static Mesh MakeCone(int segments)
        {
            Vector3[] vertices = new Vector3[segments + 1];
            Vector2[] uv = new Vector2[segments + 1];
            int[] triangles = new int[segments * 3];
            uv[0] = new Vector2(0.5f, 0);
            for (int i = 0; i < segments; ++i)
            {
                float a = i * Mathf.PI * 2f / segments;
                vertices[i + 1] = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 1f);
                uv[i + 1] = new Vector2((float)i / segments, 1f);
                triangles[i * 3] = 0; triangles[i * 3 + 1] = i + 1; triangles[i * 3 + 2] = (i + 1) % segments + 1;
            }
            Mesh mesh = new Mesh { name = "TFX short volume cone" };
            mesh.vertices = vertices; mesh.uv = uv; mesh.triangles = triangles;
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }

        private static Texture2D MakeReticleTexture(bool holographic)
        {
            const int size = 256;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            { name = holographic ? "TFX raster holo ring-dot" : "TFX raster scope reticle", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; ++y)
            for (int x = 0; x < size; ++x)
            {
                float px = (x + 0.5f - size * 0.5f) / size * 2f;
                float py = (y + 0.5f - size * 0.5f) / size * 2f;
                float radius = Mathf.Sqrt(px * px + py * py);
                float alpha;
                if (holographic)
                    alpha = Mathf.Max(1f - Mathf.Abs(radius - 0.46f) / 0.018f, 1f - radius / 0.025f);
                else
                {
                    bool cross = (Mathf.Abs(px) < 0.010f && Mathf.Abs(py) > 0.055f && Mathf.Abs(py) < 0.85f) ||
                                 (Mathf.Abs(py) < 0.010f && Mathf.Abs(px) > 0.055f && Mathf.Abs(px) < 0.85f);
                    bool hash = false;
                    for (int n = 1; n <= 3; ++n)
                        hash |= (Mathf.Abs(Mathf.Abs(px) - n * 0.2f) < 0.008f && Mathf.Abs(py) < 0.045f) ||
                                (Mathf.Abs(Mathf.Abs(py) - n * 0.2f) < 0.008f && Mathf.Abs(px) < 0.045f);
                    alpha = cross || hash ? 1f : Mathf.Clamp01(1f - radius / 0.018f);
                }
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha) * 255f));
            }
            texture.SetPixels32(pixels); texture.Apply(false, true); return texture;
        }

        private static Texture2D MakeSoftDisc()
        {
            const int size = 64;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            { name = "TFX soft impact dot", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; ++y)
            for (int x = 0; x < size; ++x)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float alpha = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * alpha * 255f));
            }
            texture.SetPixels32(pixels); texture.Apply(false, true); return texture;
        }
    }
}
