using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace PeakVRoid.Vrm
{
    /// <summary>
    /// Turns glTF materials into materials using shaders that already exist in PEAK
    /// (URP Lit / Unlit). MToon is approximated: base colour + texture, alpha mode,
    /// culling, emission, and an optional "shade lift" that keeps toon avatars from
    /// looking muddy under PEAK's lighting.
    /// </summary>
    public sealed class MaterialFactory
    {
        private static readonly string[] LitShaders =
        {
            "Universal Render Pipeline/Lit",
            "Universal Render Pipeline/Simple Lit",
            "Universal Render Pipeline/Unlit",
            "Standard",
        };

        private static readonly string[] UnlitShaders =
        {
            "Universal Render Pipeline/Unlit",
            "Unlit/Texture",
            "Universal Render Pipeline/Lit",
            "Standard",
        };

        public string ShaderOverride = "";
        public bool Unlit;
        public float ShadeLift = 0.15f;
        /// <summary>Last-resort material whose shader is known to render (PEAK's own character material).</summary>
        public Material FallbackMaterial;

        private Shader cached;
        private bool cachedUnlit;

        public static Shader FindShader(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var s = Shader.Find(name);
            if (s != null && s.isSupported) return s;
            foreach (var loaded in Resources.FindObjectsOfTypeAll<Shader>())
                if (loaded != null && loaded.name == name && loaded.isSupported) return loaded;
            return null;
        }

        private Shader ResolveShader()
        {
            if (cached != null && cachedUnlit == Unlit) return cached;
            cached = FindShader(ShaderOverride);
            if (cached == null)
                foreach (var n in Unlit ? UnlitShaders : LitShaders)
                    if ((cached = FindShader(n)) != null) break;
            cachedUnlit = Unlit;
            if (cached != null) Plugin.Log.LogInfo($"VRM materials use shader '{cached.name}'");
            else Plugin.Log.LogWarning("No URP shader found; falling back to PEAK's character material.");
            return cached;
        }

        public Material Create(Dictionary<string, object> gm, System.Func<int, Texture2D> texture)
        {
            var shader = ResolveShader();
            Material m;
            if (shader != null) m = new Material(shader);
            else if (FallbackMaterial != null) m = new Material(FallbackMaterial);
            else m = new Material(Shader.Find("Hidden/InternalErrorShader"));

            m.name = gm.Str("name", "VRM Material");
            var pbr = gm.Obj("pbrMetallicRoughness");
            var factor = pbr.Floats("baseColorFactor");
            Color baseColor = factor != null && factor.Length >= 4
                ? new Color(factor[0], factor[1], factor[2], factor[3]).gamma
                : Color.white;

            Texture2D baseTex = null;
            var bct = pbr.Obj("baseColorTexture");
            if (bct != null) baseTex = texture(bct.Int("index"));

            SetColor(m, "_BaseColor", baseColor);
            SetColor(m, "_Color", baseColor);
            if (baseTex != null)
            {
                SetTex(m, "_BaseMap", baseTex);
                SetTex(m, "_MainTex", baseTex);
            }
            SetFloat(m, "_Metallic", 0f);
            SetFloat(m, "_Smoothness", 0.05f);
            SetFloat(m, "_Glossiness", 0.05f);
            SetFloat(m, "_SpecularHighlights", 0f);
            m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            SetFloat(m, "_EnvironmentReflections", 0f);
            m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            SetFloat(m, "_ReceiveShadows", 1f);

            bool doubleSided = gm.Bool("doubleSided");
            SetFloat(m, "_Cull", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);
            SetFloat(m, "_CullMode", doubleSided ? (float)CullMode.Off : (float)CullMode.Back);

            string alpha = gm.Str("alphaMode", "OPAQUE");
            if (alpha == "MASK")
            {
                SetFloat(m, "_AlphaClip", 1f);
                SetFloat(m, "_Cutoff", (float)gm.Num("alphaCutoff", 0.5));
                m.EnableKeyword("_ALPHATEST_ON");
                m.SetOverrideTag("RenderType", "TransparentCutout");
                m.renderQueue = (int)RenderQueue.AlphaTest;
            }
            else if (alpha == "BLEND")
            {
                SetFloat(m, "_Surface", 1f);
                SetFloat(m, "_Blend", 0f);
                SetFloat(m, "_SrcBlend", (float)BlendMode.SrcAlpha);
                SetFloat(m, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                SetFloat(m, "_SrcBlendAlpha", (float)BlendMode.One);
                SetFloat(m, "_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
                SetFloat(m, "_ZWrite", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)RenderQueue.Transparent;
            }

            // Emission: the file's own emission plus the optional shade lift (base texture as faint emission).
            var ef = gm.Floats("emissiveFactor");
            Color emissive = ef != null && ef.Length >= 3 ? new Color(ef[0], ef[1], ef[2]).gamma : Color.black;
            Texture2D emissiveTex = null;
            var et = gm.Obj("emissiveTexture");
            if (et != null) emissiveTex = texture(et.Int("index"));

            if (!Unlit && emissiveTex == null && ShadeLift > 0f && baseTex != null && emissive.maxColorComponent < 0.001f)
            {
                emissiveTex = baseTex;
                emissive = baseColor * ShadeLift;
            }
            if (emissive.maxColorComponent > 0.001f)
            {
                SetColor(m, "_EmissionColor", emissive);
                if (emissiveTex != null) SetTex(m, "_EmissionMap", emissiveTex);
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            return m;
        }

        private static void SetColor(Material m, string p, Color c) { if (m.HasProperty(p)) m.SetColor(p, c); }
        private static void SetFloat(Material m, string p, float v) { if (m.HasProperty(p)) m.SetFloat(p, v); }
        private static void SetTex(Material m, string p, Texture t) { if (m.HasProperty(p)) m.SetTexture(p, t); }
    }
}
