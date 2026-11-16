using System;
using UnityEngine;

namespace TacticalAccessoryFX
{
    [Serializable]
    public sealed class FXCatalog
    {
        public int schemaVersion = 1;
        public FXEntry[] entries = new FXEntry[0];
    }

    [Serializable]
    public sealed class FXEntry
    {
        public string id;
        public string asset;
        public string host;
        public string displayName;
        public string modelResource;
        public string kind;
        public float magnification = 1f;
        public float minMagnification = 1f;
        public float maxMagnification = 1f;
        public float lensDiameter = 0.025f;
        public float reticleDiameter = 0.013f;
        public float[] effectColor;
        public string sourceSha256;
        public string exportSha256;
        public FXSurface[] surfaces = new FXSurface[0];
        public FXMaterial[] materials = new FXMaterial[0];

        public Color GetEffectColor()
        {
            return ReadColor(effectColor, new Color(1f, 0.1f, 0.045f, 1f));
        }

        internal static Color ReadColor(float[] values, Color fallback)
        {
            if (values == null || values.Length < 3) return fallback;
            return new Color(values[0], values[1], values[2], values.Length > 3 ? values[3] : 1f);
        }
    }

    [Serializable]
    public sealed class FXSurface
    {
        public string objectName;
        public string role;
    }

    [Serializable]
    public sealed class FXMaterial
    {
        public string name;
        public float[] baseColor;
        public float metallic;
        public float roughness = 0.5f;
        public float alpha = 1f;
    }
}
