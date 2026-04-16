using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace NdmfMToon10ToLilToon
{
    public enum RenderType
    {
        Opaque,
        Cutout,
        Transparent,
    }

    [Serializable]
    public sealed class LilToonGlobalOverrides
    {
        public Color shadowBorderColor = Color.black;
        [Range(0f, 1f)] public float shadowBorderStrength = 0f;
        public Color distanceFadeColor = Color.black;
        [Range(0f, 1f)] public float distanceFadeStrength = 0f;
        public Color backlightColor = Color.black;
        [Range(0f, 1f)] public float backlightStrength = 0f;
    }

    [Serializable]
    public sealed class HairMaterialSelection
    {
        public Material material;
        public bool selected;
    }

    public sealed class ConversionWarning
    {
        public ConversionWarning(string message) => Message = message;
        public string Message { get; }
    }

    public sealed class ConversionReport
    {
        public int ScannedMaterialCount;
        public int ConvertedMaterialCount;
        public int SkippedMaterialCount;
        public readonly List<ConversionWarning> Warnings = new();
        public readonly Dictionary<string, int> UnsupportedPropertySummary = new();

        public void RegisterUnsupported(string propertyName)
        {
            if (!UnsupportedPropertySummary.TryAdd(propertyName, 1))
            {
                UnsupportedPropertySummary[propertyName]++;
            }
        }
    }

    public static class MToonDetector
    {
        private static readonly string[] MToonLikeShaderNames =
        {
            "MToon",
            "VRM10/MToon10",
            "UniVRM",
        };

        public static bool IsMToonLike(Material material)
        {
            if (material == null || material.shader == null) return false;
            var shaderName = material.shader.name;
            if (MToonLikeShaderNames.Any(token => shaderName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return true;
            }

            var score = 0;
            if (HasAny(material, "_BaseColor", "_Color")) score++;
            if (HasAny(material, "_BaseMap", "_MainTex")) score++;
            if (HasAny(material, "_ShadeColor", "_ShadeColorFactor")) score++;
            if (HasAny(material, "_ShadingShiftFactor", "_ShadingToonyFactor")) score++;
            return score >= 3;
        }

        private static bool HasAny(Material material, params string[] properties)
        {
            return properties.Any(material.HasProperty);
        }
    }

    public static class HairMaterialSelector
    {
        public static List<HairMaterialSelection> BuildDefaultSelections(IEnumerable<Material> materials)
        {
            return materials
                .Where(m => m != null)
                .Distinct()
                .Select(m => new HairMaterialSelection
                {
                    material = m,
                    selected = m.name.IndexOf("HAIR", StringComparison.OrdinalIgnoreCase) >= 0,
                })
                .ToList();
        }
    }

    public static class RenderTypeResolver
    {
        public static RenderType ResolveFromMaterial(Material material)
        {
            if (material == null) return RenderType.Opaque;
            if (material.HasProperty("_AlphaMode"))
            {
                var alphaMode = Mathf.RoundToInt(material.GetFloat("_AlphaMode"));
                if (alphaMode == 1) return RenderType.Cutout;
                if (alphaMode == 2 || alphaMode == 3) return RenderType.Transparent;
            }
            if (material.IsKeywordEnabled("_ALPHATEST_ON")) return RenderType.Cutout;
            if (material.IsKeywordEnabled("_ALPHABLEND_ON") || material.GetTag("RenderType", false, "") == "Transparent")
            {
                return RenderType.Transparent;
            }

            if (material.HasProperty("_Surface"))
            {
                var surface = Mathf.RoundToInt(material.GetFloat("_Surface"));
                if (surface == 1) return RenderType.Transparent;
            }

            return RenderType.Opaque;
        }

        public static RenderType ResolveMergeType(IEnumerable<Material> selectedMaterials)
        {
            var counts = new Dictionary<RenderType, int>
            {
                [RenderType.Opaque] = 0,
                [RenderType.Cutout] = 0,
                [RenderType.Transparent] = 0,
            };

            foreach (var material in selectedMaterials)
            {
                counts[ResolveFromMaterial(material)]++;
            }

            var highest = counts.Values.Max();
            var winners = counts.Where(p => p.Value == highest).Select(p => p.Key).ToArray();
            if (winners.Length == 1) return winners[0];

            if (winners.Contains(RenderType.Opaque)) return RenderType.Opaque;
            if (winners.Contains(RenderType.Cutout)) return RenderType.Cutout;
            return RenderType.Transparent;
        }
    }

    public static class MToonToLilToonMapper
    {
        public static readonly string[] SupportedTextureBakeTargets =
        {
            "main texture",
            "shade texture",
            "outline mask",
            "emission mask",
            "normal map",
        };

        public static bool TryConvert(Material source, Shader lilToonShader, LilToonGlobalOverrides overrides, out Material converted, ConversionReport report)
        {
            converted = null;
            if (source == null || lilToonShader == null) return false;
            if (!MToonDetector.IsMToonLike(source)) return false;

            try
            {
                converted = new Material(lilToonShader)
                {
                    name = $"{source.name}_lilToon",
                };

                CopyColor(source, converted, new[] { "_BaseColor", "_Color" }, new[] { "_Color", "_BaseColor" }, report);
                CopyTexture(source, converted, new[] { "_BaseMap", "_MainTex" }, new[] { "_MainTex", "_BaseMap" }, report);
                CopyColor(source, converted, new[] { "_ShadeColor", "_ShadeColorFactor" }, new[] { "_ShadowColor", "_Shadow1stColor" }, report);
                CopyTexture(source, converted, new[] { "_ShadeMap", "_ShadeMultiplyTexture", "_ShadeColorTexture" }, new[] { "_ShadowColorTex", "_Shadow1stColorTex" }, report);
                CopyFloat(source, converted, new[] { "_ShadingShiftFactor" }, new[] { "_ShadowBorder" }, report);
                CopyFloat(source, converted, new[] { "_ShadingToonyFactor" }, new[] { "_ShadowBlur" }, report);
                CopyTexture(source, converted, new[] { "_NormalMap", "_BumpMap" }, new[] { "_BumpMap", "_NormalMap" }, report);
                CopyColor(source, converted, new[] { "_EmissiveFactor", "_EmissionColor" }, new[] { "_EmissionColor" }, report);
                CopyTexture(source, converted, new[] { "_EmissiveMap", "_EmissionMap" }, new[] { "_EmissionMap" }, report);

                ApplyRenderState(source, converted, report);
                var renderType = RenderTypeResolver.ResolveFromMaterial(source);
                ApplyFallback(source, converted, renderType);
                ApplyRenderQueue(converted, renderType);
                ApplyLilToonOverrides(converted, overrides);
                ApplyShadow2OpacityZero(converted);

                return true;
            }
            catch (Exception ex)
            {
                report?.Warnings.Add(new ConversionWarning($"{source.name}: conversion failed ({ex.Message})"));
                return false;
            }
        }

        private static void ApplyLilToonOverrides(Material material, LilToonGlobalOverrides overrides)
        {
            if (overrides == null) return;
            SetIfExists(material, "_ShadowBorderColor", overrides.shadowBorderColor);
            SetIfExists(material, "_ShadowBorderRange", overrides.shadowBorderStrength);
            SetIfExists(material, "_DistanceFadeColor", overrides.distanceFadeColor);
            SetIfExists(material, "_DistanceFade", overrides.distanceFadeStrength);
            SetIfExists(material, "_BacklightColor", overrides.backlightColor);
            SetIfExists(material, "_BacklightStrength", overrides.backlightStrength);
        }

        private static void ApplyFallback(Material source, Material destination, RenderType renderType)
        {
            var hasOutline = HasOutline(source);

            var fallback = renderType == RenderType.Transparent
                ? "Unlit/Transparent"
                : hasOutline
                    ? "ToonStandardOutline"
                    : "ToonStandard";

            destination.SetOverrideTag("VRCFallback", fallback);
        }

        private static void ApplyRenderQueue(Material destination, RenderType renderType)
        {
            if (renderType == RenderType.Transparent)
            {
                destination.renderQueue = 2460;
                return;
            }

            destination.renderQueue = renderType == RenderType.Cutout
                ? (int)RenderQueue.AlphaTest
                : (int)RenderQueue.Geometry;
        }

        private static bool HasOutline(Material source)
        {
            if (source == null) return false;
            if (source.IsKeywordEnabled("_OUTLINE_ON")) return true;

            if (source.HasProperty("_OutlineWidthMode") && Mathf.RoundToInt(source.GetFloat("_OutlineWidthMode")) > 0)
            {
                return true;
            }

            if (source.HasProperty("_OutlineWidth") && source.GetFloat("_OutlineWidth") > 0f)
            {
                return true;
            }

            if (source.HasProperty("_OutlineWidthFactor") && source.GetFloat("_OutlineWidthFactor") > 0f)
            {
                return true;
            }

            return false;
        }

        private static void ApplyRenderState(Material source, Material destination, ConversionReport report)
        {
            CopyFloat(source, destination, new[] { "_AlphaCutoff", "_Cutoff" }, new[] { "_Cutoff" }, report);
            CopyFloat(source, destination, new[] { "_CullMode", "_Cull" }, new[] { "_Cull" }, report);
            CopyFloat(source, destination, new[] { "_ZWrite" }, new[] { "_ZWrite" }, report);
            CopyFloat(source, destination, new[] { "_SrcBlend" }, new[] { "_SrcBlend" }, report);
            CopyFloat(source, destination, new[] { "_DstBlend" }, new[] { "_DstBlend" }, report);

            if (source.HasProperty("_BaseMap"))
            {
                destination.mainTextureScale = source.mainTextureScale;
                destination.mainTextureOffset = source.mainTextureOffset;
            }

            CopyFloat(source, destination, new[] { "_UvAnimScrollX", "_UvAnimationScrollXSpeedFactor" }, new[] { "_MainTex_ScrollRotateX", "_Main2ndTexAngle" }, report);
            CopyFloat(source, destination, new[] { "_UvAnimScrollY", "_UvAnimationScrollYSpeedFactor" }, new[] { "_MainTex_ScrollRotateY", "_Main2ndTex_ScrollRotate" }, report);
            CopyFloat(source, destination, new[] { "_UvAnimRotation", "_UvAnimationRotationSpeedFactor" }, new[] { "_MainTex_ScrollRotateR", "_Main2ndTex_ScrollRotate" }, report);

            var renderType = RenderTypeResolver.ResolveFromMaterial(source);
            ApplyAlphaMode(source, destination, renderType);
            ApplyBlendSetup(destination, renderType);
        }

        private static void ApplyShadow2OpacityZero(Material destination)
        {
            if (!destination.HasProperty("_Shadow2ndColor")) return;
            var shadow2 = destination.GetColor("_Shadow2ndColor");
            shadow2.a = 0f;
            destination.SetColor("_Shadow2ndColor", shadow2);
        }

        private static void CopyTexture(Material source, Material destination, IReadOnlyList<string> fromCandidates, IReadOnlyList<string> toCandidates, ConversionReport report)
        {
            if (!TryFindExistingProperty(source, fromCandidates, out var from)) return;
            if (!TryFindExistingProperty(destination, toCandidates, out var to))
            {
                report?.RegisterUnsupported(from);
                return;
            }
            destination.SetTexture(to, source.GetTexture(from));
        }

        private static void CopyColor(Material source, Material destination, IReadOnlyList<string> fromCandidates, IReadOnlyList<string> toCandidates, ConversionReport report)
        {
            if (!TryFindExistingProperty(source, fromCandidates, out var from)) return;
            if (!TryFindExistingProperty(destination, toCandidates, out var to))
            {
                report?.RegisterUnsupported(from);
                return;
            }
            destination.SetColor(to, source.GetColor(from));
        }

        private static void CopyFloat(Material source, Material destination, IReadOnlyList<string> fromCandidates, IReadOnlyList<string> toCandidates, ConversionReport report)
        {
            if (!TryFindExistingProperty(source, fromCandidates, out var from)) return;
            if (!TryFindExistingProperty(destination, toCandidates, out var to))
            {
                report?.RegisterUnsupported(from);
                return;
            }
            destination.SetFloat(to, source.GetFloat(from));
        }

        private static void SetIfExists(Material material, string propertyName, Color value)
        {
            if (!material.HasProperty(propertyName)) return;
            if (TryGetPropertyType(material, propertyName, out var propertyType)
                && propertyType != ShaderPropertyType.Color
                && propertyType != ShaderPropertyType.Vector) return;
            material.SetColor(propertyName, value);
        }

        private static void SetIfExists(Material material, string propertyName, float value)
        {
            if (!material.HasProperty(propertyName)) return;
            if (TryGetPropertyType(material, propertyName, out var propertyType)
                && propertyType != ShaderPropertyType.Float
                && propertyType != ShaderPropertyType.Range) return;
            material.SetFloat(propertyName, value);
        }

        private static void ApplyAlphaMode(Material source, Material destination, RenderType renderType)
        {
            var cutoff = source.HasProperty("_AlphaCutoff")
                ? source.GetFloat("_AlphaCutoff")
                : source.HasProperty("_Cutoff")
                    ? source.GetFloat("_Cutoff")
                    : 0.5f;

            switch (renderType)
            {
                case RenderType.Opaque:
                    destination.DisableKeyword("_ALPHATEST_ON");
                    destination.DisableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    break;
                case RenderType.Cutout:
                    destination.EnableKeyword("_ALPHATEST_ON");
                    destination.DisableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    SetIfExists(destination, "_Cutoff", cutoff);
                    break;
                case RenderType.Transparent:
                    destination.DisableKeyword("_ALPHATEST_ON");
                    destination.EnableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    break;
            }
        }

        private static void ApplyBlendSetup(Material destination, RenderType renderType)
        {
            switch (renderType)
            {
                case RenderType.Opaque:
                    SetIfExists(destination, "_SrcBlend", (float)BlendMode.One);
                    SetIfExists(destination, "_DstBlend", (float)BlendMode.Zero);
                    SetIfExists(destination, "_ZWrite", 1f);
                    SetAnyIfExists(destination, new[] { "_Surface", "_TransparentMode", "_BlendMode", "_Mode" }, 0f);
                    break;
                case RenderType.Cutout:
                    SetIfExists(destination, "_SrcBlend", (float)BlendMode.One);
                    SetIfExists(destination, "_DstBlend", (float)BlendMode.Zero);
                    SetIfExists(destination, "_ZWrite", 1f);
                    SetAnyIfExists(destination, new[] { "_Surface", "_TransparentMode", "_BlendMode", "_Mode" }, 1f);
                    break;
                case RenderType.Transparent:
                    SetIfExists(destination, "_SrcBlend", (float)BlendMode.SrcAlpha);
                    SetIfExists(destination, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    SetIfExists(destination, "_ZWrite", 0f);
                    SetAnyIfExists(destination, new[] { "_Surface", "_TransparentMode", "_BlendMode", "_Mode" }, 2f);
                    break;
            }
        }

        private static void SetAnyIfExists(Material material, IReadOnlyList<string> candidates, float value)
        {
            if (!TryFindExistingProperty(material, candidates, out var to)) return;
            SetIfExists(material, to, value);
        }

        private static bool TryGetPropertyType(Material material, string propertyName, out ShaderPropertyType propertyType)
        {
            propertyType = ShaderPropertyType.Float;
            if (material == null || material.shader == null || string.IsNullOrEmpty(propertyName)) return false;

            var shader = material.shader;
            var propertyCount = shader.GetPropertyCount();
            for (var i = 0; i < propertyCount; i++)
            {
                if (shader.GetPropertyName(i) != propertyName) continue;
                propertyType = shader.GetPropertyType(i);
                return true;
            }

            return false;
        }

        private static bool TryFindExistingProperty(Material material, IReadOnlyList<string> candidates, out string propertyName)
        {
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!material.HasProperty(candidate)) continue;
                propertyName = candidate;
                return true;
            }

            propertyName = null;
            return false;
        }
    }
}
