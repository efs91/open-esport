using UnityEngine;

namespace TacticalAccessoryFX
{
    /// <summary>Small scene entry point. All demo content is created at runtime.</summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public sealed class TacticalFXBootstrap : MonoBehaviour
    {
        [Tooltip("Start the accessory showcase when this scene enters Play Mode.")]
        public bool startAutomatically = true;

        private void Start()
        {
            if (!startAutomatically) return;
            StartDemo();
        }

        [ContextMenu("Start Demo")]
        public void StartDemo()
        {
            if (!Application.isPlaying) return;
            TacticalFXDemo demo = GetComponent<TacticalFXDemo>();
            if (demo == null) demo = gameObject.AddComponent<TacticalFXDemo>();
            demo.enabled = true;
            demo.Initialize();
        }
    }
}
