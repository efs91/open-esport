using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace TacticalAccessoryFX
{
    /// <summary>
    /// Standalone, nonfunctional accessory-art showcase. Uses OnGUI events so it
    /// also works in projects whose Active Input Handling is Input System only.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TacticalFXDemo : MonoBehaviour
    {
        [Tooltip("Optional existing camera. Its original pose and projection are restored on exit.")]
        public Camera viewer;
        [Range(30f, 90f)] public float baseFieldOfView = 55f;
        [Range(1f, 25f)] public float aimTransitionSpeed = 12f;

        private FXCatalog _catalog;
        private readonly List<string> _assets = new List<string>();
        private readonly List<int> _seats = new List<int>();
        private readonly List<Material> _materials = new List<Material>();
        private GameObject _stage;
        private TacticalFXController _current;
        private Light _sun;
        private Transform _stand;
        private Camera _camera;
        private bool _ownsCamera, _initialized, _started;
        private bool _effect = true, _nightVision, _aiming, _autoOrbit;
        private int _assetIndex, _seatIndex;
        private float _brightness = 1f, _zoom = 1f, _orbitYaw = -35f;
        private Vector3 _center, _freePosition;
        private Quaternion _freeRotation;
        private float _viewDistance;
        private string _error = "";
        private Vector2 _scroll;
        private Vector3 _savedPosition;
        private Quaternion _savedRotation;
        private float _savedFov, _savedNear;
        private Color _savedBackground;
        private int _savedCullingMask;
        private AmbientMode _savedAmbientMode;
        private Color _savedAmbientLight;
        private float _savedAmbientIntensity;
        private bool _savedRenderSettings;

        private void Start() { _started = true; Initialize(); }
        private void OnEnable() { if (_started && Application.isPlaying) Initialize(); }
        private void OnDisable() { Cleanup(); }
        private void OnDestroy() { Cleanup(); }

        public void Initialize()
        {
            if (_initialized || !Application.isPlaying) return;
            _error = "";
            try
            {
                _catalog = TacticalFXFactory.LoadCatalog();
                if (_catalog == null || _catalog.entries == null || _catalog.entries.Length == 0)
                    throw new InvalidOperationException("Resources/TacticalFX/catalog.json has no entries. Run Tools > Tactical Accessory FX > Build Prefabs and Demo.");
                _assets.Clear();
                foreach (FXEntry entry in _catalog.entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.asset)) continue;
                    if (!_assets.Contains(entry.asset)) _assets.Add(entry.asset);
                }
                if (_assets.Count == 0) throw new InvalidOperationException("The catalog has no appearance identifiers.");
                _stage = new GameObject("TacticalFX Demo Stage");
                _stage.transform.SetParent(transform, false);
                CreateCamera();
                CreateEnvironment();
                _initialized = true;
                _assetIndex = Mathf.Clamp(_assetIndex, 0, _assets.Count - 1);
                SelectAsset(_assetIndex);
                SetNightVision(_nightVision);
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                Debug.LogError("Tactical Accessory FX demo: " + exception, this);
                Cleanup();
            }
        }

        private void CreateCamera()
        {
            _camera = viewer != null ? viewer : Camera.main;
            _ownsCamera = _camera == null;
            if (_ownsCamera)
            {
                GameObject cameraObject = new GameObject("TacticalFX Demo Camera", typeof(Camera));
                cameraObject.transform.SetParent(_stage.transform, false);
                _camera = cameraObject.GetComponent<Camera>();
                _camera.tag = "MainCamera";
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.backgroundColor = new Color(0.045f, 0.055f, 0.075f);
                _camera.farClipPlane = 150f;
                if (FindObjectOfType<AudioListener>() == null) cameraObject.AddComponent<AudioListener>();
            }
            _savedPosition = _camera.transform.position;
            _savedRotation = _camera.transform.rotation;
            _savedFov = _camera.fieldOfView;
            _savedNear = _camera.nearClipPlane;
            _savedBackground = _camera.backgroundColor;
            _savedCullingMask = _camera.cullingMask;
            _camera.fieldOfView = baseFieldOfView;
            _camera.nearClipPlane = 0.003f;
            // Layer 31 contains the accessory and is excluded only by its scope camera.
            _camera.cullingMask |= 1 << 31;
        }

        private void CreateEnvironment()
        {
            _savedAmbientMode = RenderSettings.ambientMode;
            _savedAmbientLight = RenderSettings.ambientLight;
            _savedAmbientIntensity = RenderSettings.ambientIntensity;
            _savedRenderSettings = true;
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.13f, 0.15f, 0.19f);
            RenderSettings.ambientIntensity = 1f;

            Material floor = MakeMaterial("Demo floor", new Color(0.14f, 0.16f, 0.19f), 0f, 0.25f);
            Material wall = MakeMaterial("Light receiving wall", new Color(0.46f, 0.49f, 0.53f), 0f, 0.1f);
            Material pale = MakeMaterial("Resolution card", new Color(0.88f, 0.9f, 0.88f), 0f, 0.1f);
            Material dark = MakeMaterial("Resolution marks", new Color(0.018f, 0.022f, 0.026f), 0f, 0.1f);
            Material stand = MakeMaterial("Display plinth", new Color(0.09f, 0.11f, 0.14f), 0.4f, 0.4f);
            Primitive("Floor", PrimitiveType.Cube, new Vector3(0, -0.1f, 3), new Vector3(18, 0.2f, 22), floor);
            Primitive("Receiving wall at 6 m", PrimitiveType.Cube, new Vector3(0, 2.1f, 6), new Vector3(12, 4.2f, 0.18f), wall);
            Primitive("Left receiving panel", PrimitiveType.Cube, new Vector3(-3.1f, 1.6f, 3.6f), new Vector3(0.2f, 3.2f, 4), wall);
            _stand = Primitive("Accessory display plinth", PrimitiveType.Cylinder, new Vector3(0, 1.15f, 0), new Vector3(0.34f, 0.035f, 0.34f), stand).transform;

            // Real colliders on the wall and cards provide visible laser clipping.
            for (int card = -1; card <= 1; ++card)
            {
                float x = card * 1.35f;
                Primitive("Resolution card " + (card + 2), PrimitiveType.Cube, new Vector3(x, 1.4f, 5.87f), new Vector3(1, 1.15f, 0.035f), pale);
                for (int stripe = 0; stripe < 9; ++stripe)
                {
                    float thickness = card == 0 ? 0.025f : 0.04f;
                    Primitive("Vertical contrast bar", PrimitiveType.Cube, new Vector3(x - 0.28f + stripe * 0.07f, 1.52f, 5.845f), new Vector3(thickness, 0.47f, 0.008f), dark);
                }
                Primitive("Horizontal contrast bar", PrimitiveType.Cube, new Vector3(x, 1.12f, 5.842f), new Vector3(0.65f, 0.025f, 0.01f), dark);
                Primitive("Central alignment mark", PrimitiveType.Cube, new Vector3(x, 1.4f, 5.832f), new Vector3(0.045f, 0.045f, 0.015f), pale);
            }
            GameObject lightObject = new GameObject("TacticalFX Demo Key Light", typeof(Light));
            lightObject.transform.SetParent(_stage.transform, false);
            lightObject.transform.rotation = Quaternion.Euler(45f, -35f, 0f);
            _sun = lightObject.GetComponent<Light>();
            _sun.type = LightType.Directional;
            _sun.color = new Color(0.88f, 0.93f, 1f);
            _sun.intensity = 0.7f;
            _sun.shadows = LightShadows.Soft;
        }

        private Material MakeMaterial(string materialName, Color color, float metallic, float smoothness)
        {
            Shader shader = GraphicsSettings.currentRenderPipeline != null ? Shader.Find("Universal Render Pipeline/Lit") : Shader.Find("Standard");
            if (shader == null || !shader.isSupported) shader = Shader.Find("Standard");
            if (shader == null || !shader.isSupported) shader = Shader.Find("Unlit/Color");
            if (shader == null || !shader.isSupported)
                throw new InvalidOperationException("No supported demo surface shader. Build the package shader references for the active render pipeline.");
            Material material = new Material(shader) { name = materialName };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", metallic);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", smoothness);
            _materials.Add(material);
            return material;
        }

        private GameObject Primitive(string objectName, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            GameObject item = GameObject.CreatePrimitive(type);
            item.name = objectName;
            item.transform.SetParent(_stage.transform, false);
            item.transform.position = position;
            item.transform.localScale = scale;
            item.GetComponent<Renderer>().sharedMaterial = material;
            // Explicit references keep these native components in stripped players.
            // They also ensure the runtime-created receiving surfaces retain colliders.
            if (type == PrimitiveType.Cube && item.GetComponent<BoxCollider>() == null)
                item.AddComponent<BoxCollider>();
            if (type == PrimitiveType.Cylinder && item.GetComponent<CapsuleCollider>() == null)
                item.AddComponent<CapsuleCollider>();
            return item;
        }

        private void SelectAsset(int index)
        {
            if (_assets.Count == 0) return;
            _assetIndex = (index + _assets.Count) % _assets.Count;
            _seats.Clear();
            for (int i = 0; i < _catalog.entries.Length; ++i)
                if (_catalog.entries[i].asset == _assets[_assetIndex]) _seats.Add(i);
            _seatIndex = 0;
            SelectSeat(0);
        }

        private void SelectSeat(int index)
        {
            if (_seats.Count == 0) return;
            _seatIndex = (index + _seats.Count) % _seats.Count;
            if (_current != null)
            {
                _current.SetAiming(false);
                _current.gameObject.SetActive(false);
                Destroy(_current.gameObject);
                _current = null;
            }
            _aiming = false;
            _error = "";
            try
            {
                FXEntry entry = _catalog.entries[_seats[_seatIndex]];
                _current = TacticalFXFactory.Create(entry, _stage.transform);
                _current.transform.position = new Vector3(0, 1.4f, 0);
                _current.SetViewer(_camera);
                _current.ViewBaseFieldOfView = baseFieldOfView;
                _current.SetEffectEnabled(_effect);
                _current.SetNightVision(_nightVision);
                _current.SetBrightness(_brightness);
                _zoom = Mathf.Clamp(entry.magnification, Mathf.Max(1, entry.minMagnification), Mathf.Max(1, entry.maxMagnification));
                _current.SetMagnification(_zoom);
                Bounds bounds = _current.VisualBounds;
                _center = bounds.center;
                float radius = Mathf.Max(bounds.extents.magnitude, 0.055f);
                _viewDistance = Mathf.Clamp(radius / Mathf.Tan(baseFieldOfView * Mathf.Deg2Rad * 0.5f) * 2f, 0.28f, 2f);
                _stand.position = new Vector3(_center.x, bounds.min.y - 0.04f, _center.z);
                _orbitYaw = -35f;
                UpdateFreePose();
                _camera.transform.SetPositionAndRotation(_freePosition, _freeRotation);
                _camera.fieldOfView = baseFieldOfView;
            }
            catch (Exception exception)
            {
                _error = exception.Message;
                Debug.LogError("Cannot display accessory: " + exception, this);
            }
        }

        private void UpdateFreePose()
        {
            Vector3 offset = Quaternion.Euler(12f, _orbitYaw, 0f) * (Vector3.back * _viewDistance);
            _freePosition = _center + offset;
            _freeRotation = Quaternion.LookRotation(_center - _freePosition, Vector3.up);
        }

        private void LateUpdate()
        {
            if (!_initialized || _camera == null || _current == null) return;
            if (_autoOrbit && !_aiming)
            {
                _orbitYaw += Time.unscaledDeltaTime * 12f;
                UpdateFreePose();
            }
            Vector3 position = _freePosition;
            Quaternion rotation = _freeRotation;
            float fov = baseFieldOfView;
            if (_aiming && _current.IsOptic && _current.EyeAnchor != null)
            {
                Transform eye = _current.EyeAnchor;
                position = eye.position;
                rotation = eye.rotation;
                fov = 2f * Mathf.Atan(Mathf.Tan(baseFieldOfView * Mathf.Deg2Rad * 0.5f) / Mathf.Max(1f, _current.Magnification)) * Mathf.Rad2Deg;
            }
            float blend = 1f - Mathf.Exp(-aimTransitionSpeed * Time.unscaledDeltaTime);
            _camera.transform.position = Vector3.Lerp(_camera.transform.position, position, blend);
            _camera.transform.rotation = Quaternion.Slerp(_camera.transform.rotation, rotation, blend);
            _camera.fieldOfView = Mathf.Lerp(_camera.fieldOfView, fov, blend);
        }

        private void ToggleAim()
        {
            if (_current == null || !_current.IsOptic || _current.EyeAnchor == null) return;
            _aiming = !_aiming;
            _current.SetAiming(_aiming);
        }

        private void SetNightVision(bool enabled)
        {
            _nightVision = enabled;
            if (_current != null) _current.SetNightVision(enabled);
            if (_savedRenderSettings)
                RenderSettings.ambientLight = enabled ? new Color(0.16f, 0.31f, 0.17f) : new Color(0.13f, 0.15f, 0.19f);
            if (_sun != null) _sun.color = enabled ? new Color(0.45f, 1f, 0.55f) : new Color(0.88f, 0.93f, 1f);
            if (_camera != null) _camera.backgroundColor = enabled ? new Color(0.012f, 0.045f, 0.02f) : _savedBackground;
        }

        private void HandleKeys()
        {
            Event inputEvent = Event.current;
            if (inputEvent.type != EventType.KeyDown || !_initialized) return;
            bool handled = true;
            switch (inputEvent.keyCode)
            {
                case KeyCode.LeftArrow: SelectAsset(_assetIndex - 1); break;
                case KeyCode.RightArrow: SelectAsset(_assetIndex + 1); break;
                case KeyCode.LeftBracket: SelectSeat(_seatIndex - 1); break;
                case KeyCode.RightBracket: SelectSeat(_seatIndex + 1); break;
                case KeyCode.A: ToggleAim(); break;
                case KeyCode.F: _effect = !_effect; if (_current != null) _current.SetEffectEnabled(_effect); break;
                case KeyCode.N: SetNightVision(!_nightVision); break;
                case KeyCode.Home: _orbitYaw = -35f; UpdateFreePose(); break;
                default: handled = false; break;
            }
            if (handled) inputEvent.Use();
        }

        private void OnGUI()
        {
            HandleKeys();
            if (_nightVision && _initialized)
            {
                Color prior = GUI.color;
                GUI.color = new Color(0.1f, 0.8f, 0.2f, 0.06f);
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = prior;
            }
            float panelWidth = Mathf.Min(400f, Screen.width - 24f);
            GUILayout.BeginArea(new Rect(12, 12, panelWidth, Mathf.Max(160, Screen.height - 24)), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);
            GUILayout.Label("TACTICAL ACCESSORY FX  /  VISUAL DEMO", GUI.skin.box);
            GUILayout.Label("Nonfunctional game art • native FBX models");
            if (!_initialized)
            {
                GUILayout.Label(string.IsNullOrEmpty(_error) ? "Starting demo…" : _error);
                if (GUILayout.Button("Retry initialization")) Initialize();
                GUILayout.EndScrollView();
                GUILayout.EndArea();
                return;
            }
            GUILayout.Label(_assets.Count + " appearances  /  " + _catalog.entries.Length + " asset + host seats");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀ Appearance")) SelectAsset(_assetIndex - 1);
            if (GUILayout.Button("Appearance ▶")) SelectAsset(_assetIndex + 1);
            GUILayout.EndHorizontal();
            GUILayout.Label("Appearance " + (_assetIndex + 1) + "/" + _assets.Count + ": " + _assets[_assetIndex]);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀ Seat")) SelectSeat(_seatIndex - 1);
            if (GUILayout.Button("Seat ▶")) SelectSeat(_seatIndex + 1);
            GUILayout.EndHorizontal();
            FXEntry entry = _seats.Count == 0 ? null : _catalog.entries[_seats[_seatIndex]];
            if (entry != null)
            {
                GUILayout.Label("Seat " + (_seatIndex + 1) + "/" + _seats.Count + ": " + entry.id);
                GUILayout.Label(entry.displayName + "  •  " + entry.kind);
            }
            if (!string.IsNullOrEmpty(_error)) GUILayout.Label("LOAD ERROR: " + _error);
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            bool previousEnabled = GUI.enabled;
            bool passive = entry != null && entry.kind == "passive";
            GUI.enabled = previousEnabled && !passive;
            if (GUILayout.Button(passive ? "FX — passive" : "FX " + (_effect ? "ON" : "OFF")))
            {
                _effect = !_effect;
                if (_current != null) _current.SetEffectEnabled(_effect);
            }
            GUI.enabled = previousEnabled && _current != null && _current.IsOptic && _current.EyeAnchor != null;
            if (GUILayout.Button(_aiming ? "Exit aim" : "Aim / ADS")) ToggleAim();
            GUI.enabled = previousEnabled;
            GUILayout.EndHorizontal();
            bool nv = GUILayout.Toggle(_nightVision, "Night vision — reveals infrared effects");
            if (nv != _nightVision) SetNightVision(nv);
            GUILayout.Label("Brightness: " + _brightness.ToString("0.00") + "×");
            float brightness = GUILayout.HorizontalSlider(_brightness, 0f, 4f);
            if (!Mathf.Approximately(brightness, _brightness))
            {
                _brightness = brightness;
                if (_current != null) _current.SetBrightness(_brightness);
            }
            if (_current != null && _current.IsOptic && entry != null)
            {
                GUILayout.Label("Magnification: " + _current.Magnification.ToString("0.0") + "×");
                if (entry.maxMagnification > entry.minMagnification)
                {
                    float zoom = GUILayout.HorizontalSlider(_zoom, entry.minMagnification, entry.maxMagnification);
                    GUILayout.BeginHorizontal();
                    foreach (float preset in new[] { 1f, 2f, 4f, 6f })
                        if (preset >= entry.minMagnification && preset <= entry.maxMagnification && GUILayout.Button(preset + "×")) zoom = preset;
                    GUILayout.EndHorizontal();
                    if (!Mathf.Approximately(zoom, _zoom)) { _zoom = zoom; _current.SetMagnification(_zoom); }
                }
                GUILayout.Label(_aiming ? "Eye anchor view • smooth ADS and magnified FOV" : "Full-body inspection • aim to enter the eye anchor");
            }
            if (entry != null && entry.kind == "infrared" && !_nightVision)
                GUILayout.Label("Infrared is hidden in normal view. Enable night vision.");
            if (entry != null && entry.kind == "passive")
                GUILayout.Label("Passive appearance: no emitted FX.");
            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Orbit ◀")) { _orbitYaw -= 20f; UpdateFreePose(); }
            if (GUILayout.Button("Reset view")) { _orbitYaw = -35f; UpdateFreePose(); if (_aiming) ToggleAim(); }
            if (GUILayout.Button("Orbit ▶")) { _orbitYaw += 20f; UpdateFreePose(); }
            GUILayout.EndHorizontal();
            _autoOrbit = GUILayout.Toggle(_autoOrbit, "Slow inspection orbit");
            GUILayout.Space(6);
            GUILayout.Label("Keys: ← → appearance · [ ] seat · F effects · A aim · N night vision");
            GUILayout.Label("Cards and receiving wall are 6 m ahead. Laser rays stop at colliders.");
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void Cleanup()
        {
            _initialized = false;
            if (_current != null) _current.SetAiming(false);
            _current = null;
            _aiming = false;
            if (_camera != null && !_ownsCamera)
            {
                _camera.transform.SetPositionAndRotation(_savedPosition, _savedRotation);
                _camera.fieldOfView = _savedFov;
                _camera.nearClipPlane = _savedNear;
                _camera.backgroundColor = _savedBackground;
                _camera.cullingMask = _savedCullingMask;
            }
            _camera = null;
            if (_savedRenderSettings)
            {
                RenderSettings.ambientMode = _savedAmbientMode;
                RenderSettings.ambientLight = _savedAmbientLight;
                RenderSettings.ambientIntensity = _savedAmbientIntensity;
                _savedRenderSettings = false;
            }
            // Destroy is deferred until the end of the frame. Disable immediately
            // so a same-frame retry cannot reuse a camera that is about to vanish.
            if (_stage != null)
            {
                _stage.SetActive(false);
                Destroy(_stage);
            }
            _stage = null;
            _sun = null;
            _stand = null;
            foreach (Material material in _materials) if (material != null) Destroy(material);
            _materials.Clear();
        }
    }
}
