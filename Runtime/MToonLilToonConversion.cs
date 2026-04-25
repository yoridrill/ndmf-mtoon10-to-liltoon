using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Serialization;
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
        public bool enableShadowReceive = true;
        [Range(0f, 1f)] public float shadowReceive = 0.5f;
        public bool enableShadowBorder = true;
        public Color shadowBorderColor = new(1f, 25f / 255f, 0f, 1f);
        [Range(0f, 1f)] public float shadowBorderStrength = 0.08f;
        public bool enableBacklight = true;
        [ColorUsage(false, true)] public Color backlightColor = Color.white;
        [FormerlySerializedAs("backlightStrength")]
        [Range(0f, 1f)] public float backlightMainStrength = 0.5f;
        public bool enableDistanceFade = true;
        public Color distanceFadeColor = new(10f / 255f, 7f / 255f, 7f / 255f, 1f);
        [Range(0f, 1f)] public float distanceFadeStrength = 1f;
        public float outlineZBias = 0.003f;
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
        private static readonly string[] ConvertibleShaderNamePrefixes =
        {
            "VRM10/MToon10",
            "VRM/MToon",
        };

        public static bool IsMToonLike(Material material)
        {
            if (material == null || material.shader == null) return false;
            var shaderName = material.shader.name;
            return ConvertibleShaderNamePrefixes.Any(prefix =>
                shaderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class HairMaterialSelector
    {
        public static List<HairMaterialSelection> BuildDefaultSelections(IEnumerable<Material> materials)
        {
            var distinctMaterials = materials
                .Where(m => m != null)
                .Distinct()
                .ToList();

            var nameMatched = distinctMaterials
                .Where(m => m.name.IndexOf("HAIR", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            var transparentCount = nameMatched.Count(m => RenderTypeResolver.ResolveFromMaterial(m) == RenderType.Transparent);
            var nonTransparentCount = nameMatched.Count - transparentCount;
            var transparentDominant = transparentCount > nonTransparentCount;
            var dominantCullMode = CullModeResolver.ResolveMergeCullMode(nameMatched);

            return distinctMaterials
                .Select(m => new HairMaterialSelection
                {
                    material = m,
                    selected = nameMatched.Contains(m)
                        && IsInDominantRenderGroup(m, transparentDominant)
                        && CullModeResolver.ResolveFromMaterial(m) == dominantCullMode,
                })
                .ToList();
        }

        private static bool IsInDominantRenderGroup(Material material, bool transparentDominant)
        {
            if (material == null) return false;
            var renderType = RenderTypeResolver.ResolveFromMaterial(material);
            return transparentDominant
                ? renderType == RenderType.Transparent
                : renderType != RenderType.Transparent;
        }
    }

    public static class CullModeResolver
    {
        public static CullMode ResolveFromMaterial(Material material)
        {
            if (material == null) return CullMode.Back;

            if (material.HasProperty("_DoubleSided"))
            {
                var doubleSided = material.GetFloat("_DoubleSided") > 0.5f;
                return doubleSided ? CullMode.Off : CullMode.Back;
            }

            if (TryResolveFromProperty(material, "_CullMode", out var fromCullMode))
            {
                return fromCullMode;
            }

            if (TryResolveFromProperty(material, "_Cull", out var fromCull))
            {
                return fromCull;
            }

            if (material.IsKeywordEnabled("_CULL_OFF"))
            {
                return CullMode.Off;
            }

            return CullMode.Back;
        }

        public static CullMode ResolveMergeCullMode(IEnumerable<Material> materials)
        {
            var counts = new Dictionary<CullMode, int>
            {
                [CullMode.Off] = 0,
                [CullMode.Front] = 0,
                [CullMode.Back] = 0,
            };

            foreach (var material in materials.Where(m => m != null))
            {
                counts[ResolveFromMaterial(material)]++;
            }

            var highest = counts.Values.Max();
            var winners = counts.Where(p => p.Value == highest).Select(p => p.Key).ToArray();
            if (winners.Length == 1) return winners[0];

            if (winners.Contains(CullMode.Back)) return CullMode.Back;
            if (winners.Contains(CullMode.Off)) return CullMode.Off;
            return CullMode.Front;
        }

        private static bool TryResolveFromProperty(Material material, string propertyName, out CullMode cullMode)
        {
            cullMode = CullMode.Back;
            if (material == null || !material.HasProperty(propertyName)) return false;

            var raw = Mathf.RoundToInt(material.GetFloat(propertyName));
            raw = Mathf.Clamp(raw, (int)CullMode.Off, (int)CullMode.Back);
            cullMode = (CullMode)raw;
            return true;
        }
    }

    public static class RenderTypeResolver
    {
        public static RenderType ResolveFromMaterial(Material material)
        {
            if (material == null) return RenderType.Opaque;

            if (material.HasProperty("_AlphaMode"))
            {
                if (TryGetMToon10RenderType(material, out var renderType))
                {
                    return renderType;
                }
            }

            if (material.HasProperty("_BlendMode"))
            {
                var blendMode = Mathf.RoundToInt(material.GetFloat("_BlendMode"));
                if (blendMode == 1) return RenderType.Cutout;
                if (blendMode >= 2) return RenderType.Transparent;
            }

            if (material.IsKeywordEnabled("_ALPHATEST_ON")) return RenderType.Cutout;
            if (material.IsKeywordEnabled("_ALPHABLEND_ON")) return RenderType.Transparent;

            if (material.renderQueue >= (int)RenderQueue.Transparent) return RenderType.Transparent;
            if (material.renderQueue >= (int)RenderQueue.AlphaTest) return RenderType.Cutout;

            return RenderType.Opaque;
        }

        private static bool TryGetMToon10RenderType(Material material, out RenderType renderType)
        {
            renderType = RenderType.Opaque;
            if (!material.HasProperty("_AlphaMode")) return false;

            var alphaMode = Mathf.RoundToInt(material.GetFloat("_AlphaMode"));
            switch (alphaMode)
            {
                // glTF alphaMode == OPAQUE
                case 0:
                    renderType = RenderType.Opaque;
                    return true;
                // glTF alphaMode == MASK
                case 1:
                    renderType = RenderType.Cutout;
                    return true;
                // glTF alphaMode == BLEND
                case 2:
                    renderType = RenderType.Transparent;
                    return true;
                default:
                    return false;
            }
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
                var renderType = RenderTypeResolver.ResolveFromMaterial(source);
                var transparentWithZWrite = IsTransparentWithZWrite(source, renderType);
                var hasOutline = HasOutline(source);
                var destinationShader = ResolveLilToonBakedShader(lilToonShader, renderType, transparentWithZWrite, hasOutline);
                converted = new Material(destinationShader)
                {
                    name = $"{source.name}_lilToon",
                };

                CopyColor(source, converted, new[] { "_BaseColor", "_Color" }, new[] { "_Color", "_BaseColor" }, report);
                CopyTexture(source, converted, new[] { "_BaseMap", "_MainTex" }, new[] { "_MainTex", "_BaseMap" }, report);
                CopyColor(source, converted, new[] { "_ShadeColor", "_ShadeColorFactor" }, new[] { "_ShadowColor", "_Shadow1stColor" }, report);
                ApplyShadeTextureMapping(source, converted, report);
                ApplyShadingFactorMapping(source, converted);
                CopyTexture(source, converted, new[] { "_NormalMap", "_BumpMap" }, new[] { "_BumpMap", "_NormalMap" }, report, ignoreTinyDummyTexture: true);
                CopyFloat(source, converted, new[] { "_BumpScale" }, new[] { "_BumpScale" }, report);
                CopyColor(source, converted, new[] { "_EmissiveFactor", "_EmissionColor" }, new[] { "_EmissionColor" }, report);
                CopyTexture(source, converted, new[] { "_EmissiveMap", "_EmissionMap" }, new[] { "_EmissionMap" }, report, ignoreTinyDummyTexture: true);
                CopyColor(source, converted, new[] { "_MatcapColor" }, new[] { "_MatCapColor" }, report);
                CopyTexture(source, converted, new[] { "_MatcapTex" }, new[] { "_MatCapTex" }, report, ignoreTinyDummyTexture: true);
                CopyColor(source, converted, new[] { "_RimColor" }, new[] { "_RimColor" }, report);
                CopyTexture(source, converted, new[] { "_RimTex" }, new[] { "_RimColorTex" }, report, ignoreTinyDummyTexture: true);
                CopyFloat(source, converted, new[] { "_RimFresnelPower" }, new[] { "_RimFresnelPower" }, report);
                CopyColor(source, converted, new[] { "_OutlineColorFactor", "_OutlineColor" }, new[] { "_OutlineColor" }, report);
                CopyTexture(source, converted, new[] { "_OutlineWidthTex", "_OutlineWidthMultiplyTexture", "_OutlineMask" }, new[] { "_OutlineWidthMask", "_OutlineTex", "_OutlineMask" }, report, ignoreTinyDummyTexture: true);

                ApplyRenderState(source, converted, report);
                ApplyOutlineState(source, converted);
                ApplyShadowState(source, converted);
                ApplyRimState(source, converted);
                ApplyUvAnimationMapping(source, converted);
                ApplyFeatureEnables(source, converted);
                ApplyFallback(converted, renderType);
                ApplyRenderQueue(source, converted, renderType);
                ApplyTransparentMode(converted, renderType);
                ApplyTransparentZWrite(converted, renderType, transparentWithZWrite);
                ApplyRenderTypeTag(converted, renderType);
                ApplyGlobalOverridesToMaterial(converted, overrides);
                ApplyShadow2OpacityZero(converted);

                return true;
            }
            catch (Exception ex)
            {
                report?.Warnings.Add(new ConversionWarning($"{source.name}: conversion failed ({ex.Message})"));
                return false;
            }
        }

        private static bool IsTransparentWithZWrite(Material source, RenderType renderType)
        {
            if (renderType != RenderType.Transparent || source == null) return false;

            if (source.HasProperty("_TransparentWithZWrite"))
            {
                return source.GetFloat("_TransparentWithZWrite") > 0.5f;
            }

            if (source.HasProperty("_BlendMode"))
            {
                // Legacy MToon: TransparentWithZWrite == 3
                return Mathf.RoundToInt(source.GetFloat("_BlendMode")) == 3;
            }

            if (source.HasProperty("_M_ZWrite"))
            {
                return source.GetFloat("_M_ZWrite") > 0.5f;
            }

            return source.HasProperty("_ZWrite") && source.GetFloat("_ZWrite") > 0.5f;
        }

        private static Shader ResolveLilToonBakedShader(Shader fallbackShader, RenderType renderType, bool transparentWithZWrite, bool hasOutline)
        {
            string hiddenShaderName;
            switch (renderType)
            {
                case RenderType.Opaque:
                    hiddenShaderName = hasOutline ? "Hidden/lilToonOutline" : "Hidden/lilToon";
                    break;
                case RenderType.Cutout:
                    hiddenShaderName = hasOutline ? "Hidden/lilToonCutoutOutline" : "Hidden/lilToonCutout";
                    break;
                case RenderType.Transparent:
                    hiddenShaderName = transparentWithZWrite
                        ? (hasOutline ? "Hidden/lilToonTransparentOutlineZWrite" : "Hidden/lilToonTransparentZWrite")
                        : (hasOutline ? "Hidden/lilToonTransparentOutline" : "Hidden/lilToonTransparent");
                    break;
                default:
                    hiddenShaderName = hasOutline ? "Hidden/lilToonOutline" : "Hidden/lilToon";
                    break;
            }

            var hidden = Shader.Find(hiddenShaderName);
            if (hidden == null && hasOutline)
            {
                var nonOutlineName = hiddenShaderName
                    .Replace("OutlineZWrite", "ZWrite")
                    .Replace("Outline", string.Empty);
                hidden = Shader.Find(nonOutlineName);
            }
            return hidden != null ? hidden : fallbackShader;
        }

        private static void ApplyShadingFactorMapping(Material source, Material destination)
        {
            if (source == null || destination == null) return;

            if (source.HasProperty("_ShadingShiftFactor"))
            {
                // MToon: -1 で影が覆い尽くす / +1 で影が消える（レンジ -1..1）
                // lilToon: 1 で影が覆い尽くす / 0 で影が消える（レンジ 0..1）
                var shift = Mathf.Clamp(source.GetFloat("_ShadingShiftFactor"), -1f, 1f);
                SetIfExists(destination, "_ShadowBorder", (1f - shift) * 0.5f);
            }

            if (source.HasProperty("_ShadingToonyFactor"))
            {
                // MToon: 値が大きいほどくっきり
                // lilToon _ShadowBlur: 値が大きいほどぼかし
                var toony = Mathf.Clamp01(source.GetFloat("_ShadingToonyFactor"));
                SetIfExists(destination, "_ShadowBlur", 1f - toony);
            }

            if (source.HasProperty("_ShadingShiftTex"))
            {
                var shiftTex = source.GetTexture("_ShadingShiftTex");
                SetTextureIfExists(destination, "_ShadowBorderMask", shiftTex);
            }

            if (source.HasProperty("_ShadingShiftTexScale") && destination.HasProperty("_ShadowBorder"))
            {
                // 1:1 対応先はないため、境界位置へ全体補正として反映する。
                var scale = Mathf.Max(0f, source.GetFloat("_ShadingShiftTexScale"));
                var current = destination.GetFloat("_ShadowBorder");
                SetIfExists(destination, "_ShadowBorder", Mathf.Clamp01(current * scale));
            }
        }

        private static void ApplyShadeTextureMapping(Material source, Material destination, ConversionReport report)
        {
            if (source == null || destination == null) return;

            if (!TryFindExistingProperty(source, new[] { "_ShadeTex", "_ShadeMap", "_ShadeMultiplyTexture", "_ShadeColorTexture" }, out var shadeProp))
            {
                ApplyShadowTextureUsage(destination, false);
                return;
            }

            if (!TryFindExistingProperty(destination, new[] { "_ShadowColorTex", "_Shadow1stColorTex" }, out var destinationShadeProp))
            {
                report?.RegisterUnsupported(shadeProp);
                return;
            }

            var shadeTex = source.GetTexture(shadeProp);
            if (shadeTex == null)
            {
                destination.SetTexture(destinationShadeProp, null);
                ApplyShadowTextureUsage(destination, false);
                return;
            }

            Texture baseTex = null;
            if (source.HasProperty("_BaseMap")) baseTex = source.GetTexture("_BaseMap");
            else if (source.HasProperty("_MainTex")) baseTex = source.GetTexture("_MainTex");

            // MToon 側で shade テクスチャが base と同一の場合、
            // lilToon では乗算が強く出て影が濃くなりやすいので転送しない。
            if (baseTex != null && shadeTex == baseTex)
            {
                destination.SetTexture(destinationShadeProp, null);
                ApplyShadowTextureUsage(destination, false);
                return;
            }

            destination.SetTexture(destinationShadeProp, shadeTex);
            ApplyShadowTextureUsage(destination, true);
        }

        private static void ApplyShadowTextureUsage(Material destination, bool enabled)
        {
            if (destination == null) return;
            SetIfExists(destination, "_UseShadowColorTex", enabled ? 1f : 0f);
            SetIfExists(destination, "_UseShadow1stColorTex", enabled ? 1f : 0f);
        }

        public static void ApplyGlobalOverridesToMaterial(Material material, LilToonGlobalOverrides overrides)
        {
            if (overrides == null) return;
            if (overrides.enableShadowReceive)
            {
                SetIfExists(material, "_ShadowReceive", overrides.shadowReceive);
            }
            else
            {
                SetIfExists(material, "_ShadowReceive", 0f);
            }

            if (overrides.enableShadowBorder)
            {
                SetIfExists(material, "_ShadowBorderColor", overrides.shadowBorderColor);
                SetIfExists(material, "_ShadowBorderRange", overrides.shadowBorderStrength);
            }
            else
            {
                SetIfExists(material, "_ShadowBorderColor", Color.black);
                SetIfExists(material, "_ShadowBorderRange", 0f);
            }

            if (overrides.enableDistanceFade)
            {
                SetIfExists(material, "_DistanceFadeColor", overrides.distanceFadeColor);
                SetVectorComponentIfExists(material, "_DistanceFade", 2, overrides.distanceFadeStrength);
            }
            else
            {
                SetIfExists(material, "_DistanceFadeColor", Color.black);
                SetVectorComponentIfExists(material, "_DistanceFade", 2, 0f);
            }

            var useBacklight = overrides.enableBacklight;
            SetIfExists(material, "_UseBacklight", useBacklight ? 1f : 0f);
            if (useBacklight)
            {
                SetIfExists(material, "_BacklightColor", overrides.backlightColor);
                SetIfExists(material, "_BacklightMainStrength", overrides.backlightMainStrength);
            }

            var hasOutline = (material.HasProperty("_UseOutline") && material.GetFloat("_UseOutline") > 0.5f)
                || (material.HasProperty("_OutlineEnable") && material.GetFloat("_OutlineEnable") > 0.5f)
                || material.IsKeywordEnabled("_OUTLINE_ON");
            if (hasOutline)
            {
                SetIfExists(material, "_OutlineZBias", overrides.outlineZBias);
            }
        }

        private static void ApplyShadowState(Material source, Material destination)
        {
            if (source == null || destination == null) return;

            var useShadow = true;
            if (source.HasProperty("_ReceiveShadowRate"))
            {
                useShadow = source.GetFloat("_ReceiveShadowRate") > 0.001f;
            }

            SetIfExists(destination, "_UseShadow", useShadow ? 1f : 0f);
            SetIfExists(destination, "_ReceiveShadowRate", useShadow ? 1f : 0f);
            SetIfExists(destination, "_ShadowEnvStrength", 0.5f);
        }

        private static void ApplyRimState(Material source, Material destination)
        {
            if (source == null || destination == null) return;

            CopyFloat(source, destination, new[] { "_RimLift" }, new[] { "_RimBorder" }, null);
            CopyFloat(source, destination, new[] { "_RimLightingMix" }, new[] { "_RimEnableLighting" }, null);
            CopyFloat(source, destination, new[] { "_OutlineLightingMix" }, new[] { "_OutlineEnableLighting" }, null);
        }

        private static void ApplyFeatureEnables(Material source, Material destination)
        {
            if (source == null || destination == null) return;

            var hasEmissionTexture = HasTexture(source, true, "_EmissiveMap", "_EmissionMap");
            var useEmission = hasEmissionTexture;
            SetIfExists(destination, "_UseEmission", useEmission ? 1f : 0f);

            var hasMatCapTexture = HasTexture(source, true, "_MatcapTex");
            var useMatCap = hasMatCapTexture;
            SetIfExists(destination, "_UseMatCap", useMatCap ? 1f : 0f);

            var hasRimTexture = HasTexture(source, true, "_RimTex");
            var useRim = HasNonDefaultColor(source, new[] { "_RimColor" }, Color.black)
                || hasRimTexture;
            SetIfExists(destination, "_UseRim", useRim ? 1f : 0f);

            var useNormalMap = HasTexture(source, true, "_NormalMap", "_BumpMap")
                && HasNonDefaultFloat(source, new[] { "_BumpScale" }, 0f);
            SetIfExists(destination, "_UseBumpMap", useNormalMap ? 1f : 0f);
            SetIfExists(destination, "_UseNormalMap", useNormalMap ? 1f : 0f);
        }

        private static void ApplyOutlineState(Material source, Material destination)
        {
            if (source == null || destination == null) return;

            var useOutline = HasOutline(source);
            if (useOutline)
            {
                // Inspector での OFF->ON トグルと同等の状態遷移を先に入れる。
                SetIfExists(destination, "_UseOutline", 0f);
                SetIfExists(destination, "_OutlineEnable", 0f);
                destination.DisableKeyword("_OUTLINE_ON");
                destination.SetShaderPassEnabled("Outline", false);

                SetIfExists(destination, "_UseOutline", 1f);
                SetIfExists(destination, "_OutlineEnable", 1f);
                destination.EnableKeyword("_OUTLINE_ON");
                destination.SetShaderPassEnabled("Outline", true);
            }
            else
            {
                SetIfExists(destination, "_UseOutline", 0f);
                SetIfExists(destination, "_OutlineEnable", 0f);
                destination.DisableKeyword("_OUTLINE_ON");
                destination.SetShaderPassEnabled("Outline", false);
            }

            if (!useOutline)
            {
                SetIfExists(destination, "_OutlineWidth", 0f);
                return;
            }

            var sourceWidth = 0f;
            if (source.HasProperty("_OutlineWidthFactor")) sourceWidth = source.GetFloat("_OutlineWidthFactor");
            else if (source.HasProperty("_OutlineWidth")) sourceWidth = source.GetFloat("_OutlineWidth");

            // MToon と lilToon で輪郭線の幅スケールが大きく異なるため補正する。
            SetIfExists(destination, "_OutlineWidth", sourceWidth * 100f);
            SetIfExists(destination, "_OutlineCull", (float)CullMode.Front);
            ApplyOutlineWidthMode(source, destination);

            var sourceMainTex = source.HasProperty("_BaseMap")
                ? source.GetTexture("_BaseMap")
                : source.HasProperty("_MainTex")
                    ? source.GetTexture("_MainTex")
                    : null;
            if (sourceMainTex == null) return;

            var sourceOutlineMask = source.HasProperty("_OutlineWidthTex")
                ? source.GetTexture("_OutlineWidthTex")
                : source.HasProperty("_OutlineWidthMultiplyTexture")
                    ? source.GetTexture("_OutlineWidthMultiplyTexture")
                    : source.HasProperty("_OutlineMask")
                        ? source.GetTexture("_OutlineMask")
                        : null;
            if (IsLikelyDummyTexture(sourceOutlineMask))
            {
                sourceOutlineMask = null;
            }

            // MToon には _OutlineTex がないため、mask がある場合はそれを優先、
            // 無い場合のみメインテクスチャを輪郭線テクスチャへ入れる。
            if (destination.HasProperty("_OutlineTex"))
            {
                destination.SetTexture("_OutlineTex", sourceOutlineMask != null ? sourceOutlineMask : sourceMainTex);
            }

            if (sourceOutlineMask == null) return;
            SetTextureIfExists(destination, "_OutlineWidthMask", sourceOutlineMask);
            SetTextureIfExists(destination, "_OutlineMask", sourceOutlineMask);
        }

        private static void ApplyOutlineWidthMode(Material source, Material destination)
        {
            if (source == null || destination == null || !source.HasProperty("_OutlineWidthMode")) return;

            // MToon10: 0=None, 1=WorldCoordinates, 2=ScreenCoordinates
            var mode = Mathf.RoundToInt(source.GetFloat("_OutlineWidthMode"));
            var useOutline = mode > 0 ? 1f : 0f;
            var fixWidth = mode == 2 ? 1f : 0f;
            var vertexR2Width = mode == 1 ? 1f : 0f;

            SetIfExists(destination, "_UseOutline", useOutline);
            SetIfExists(destination, "_OutlineFixWidth", fixWidth);
            SetIfExists(destination, "_OutlineVertexR2Width", vertexR2Width);
        }

        private static void ApplyFallback(Material destination, RenderType renderType)
        {
            var fallback = renderType switch
            {
                RenderType.Cutout => "UnlitCutout",
                RenderType.Transparent => "Unlit/Transparent",
                _ => "Unlit"
            };
            destination.SetOverrideTag("VRCFallback", fallback);
        }

        private static void ApplyRenderQueue(Material source, Material destination, RenderType renderType)
        {
            var queueOffset = ResolveRenderQueueOffset(source, renderType);

            switch (renderType)
            {
                case RenderType.Opaque:
                    destination.renderQueue = (int)RenderQueue.Geometry;
                    return;
                case RenderType.Cutout:
                    destination.renderQueue = (int)RenderQueue.AlphaTest;
                    return;
                case RenderType.Transparent:
                    // VRChat アバター向け運用では 2460 開始の帯を使い、
                    // 個々のマテリアルは後段の ReindexTransparentQueues で詰めて再採番する。
                    destination.renderQueue = 2460 + queueOffset;
                    return;
            }
        }

        private static int ResolveRenderQueueOffset(Material source, RenderType renderType)
        {
            if (source == null || renderType != RenderType.Transparent) return 0;

            var offset = 0;
            if (source.HasProperty("_RenderQueueOffsetNumber"))
            {
                offset = Mathf.RoundToInt(source.GetFloat("_RenderQueueOffsetNumber"));
            }
            else if (source.HasProperty("_RenderQueueOffset"))
            {
                offset = Mathf.RoundToInt(source.GetFloat("_RenderQueueOffset"));
            }

            // VRoid 等で不適切な queue 値が入っていても、offset は安全側で小さく保つ。
            return Mathf.Clamp(offset, -9, 9);
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
            ApplyCullState(source, destination, report);
            CopyFloat(source, destination, new[] { "_M_SrcBlend", "_SrcBlend" }, new[] { "_SrcBlend" }, report);
            CopyFloat(source, destination, new[] { "_M_DstBlend", "_DstBlend" }, new[] { "_DstBlend" }, report);
            CopyFloat(source, destination, new[] { "_M_ZWrite", "_ZWrite" }, new[] { "_ZWrite" }, report);
            CopyFloat(source, destination, new[] { "_ZTest" }, new[] { "_ZTest" }, report);
            CopyFloat(source, destination, new[] { "_ColorMask" }, new[] { "_ColorMask" }, report);
            CopyFloat(source, destination, new[] { "_M_AlphaToMask", "_AlphaToMask" }, new[] { "_AlphaToMask" }, report);

            if (source.HasProperty("_BaseMap"))
            {
                destination.mainTextureScale = source.mainTextureScale;
                destination.mainTextureOffset = source.mainTextureOffset;
            }

            var renderType = RenderTypeResolver.ResolveFromMaterial(source);
            ApplyAlphaMode(source, destination, renderType);
            ApplyBlendSetup(source, destination, renderType);
        }

        private static void ApplyCullState(Material source, Material destination, ConversionReport report)
        {
            if (source == null || destination == null) return;

            // MToon10 は glTF doubleSided 由来で _DoubleSided を利用することがある。
            // この値が true の場合は lilToon 側を Cull Off に揃える。
            if (source.HasProperty("_DoubleSided"))
            {
                var doubleSided = source.GetFloat("_DoubleSided") > 0.5f;
                var cull = doubleSided ? (float)CullMode.Off : (float)CullMode.Back;
                SetIfExists(destination, "_Cull", cull);
                ApplyBackfaceRenderStateForCull(destination, cull);
                return;
            }

            if (TryFindExistingProperty(source, new[] { "_M_CullMode", "_CullMode", "_Cull" }, out var sourceCull))
            {
                var cull = source.GetFloat(sourceCull);
                SetIfExists(destination, "_Cull", cull);
                ApplyBackfaceRenderStateForCull(destination, cull);
                return;
            }

            // 旧式 MToon では culling が keyword で制御される場合がある。
            if (source.IsKeywordEnabled("_CULL_OFF"))
            {
                var cull = (float)CullMode.Off;
                SetIfExists(destination, "_Cull", cull);
                ApplyBackfaceRenderStateForCull(destination, cull);
                return;
            }

            report?.RegisterUnsupported("CullMode");
        }

        private static void ApplyBackfaceRenderStateForCull(Material destination, float cull)
        {
            if (destination == null) return;

            var cullMode = (CullMode)Mathf.Clamp(Mathf.RoundToInt(cull), (int)CullMode.Off, (int)CullMode.Back);
            if (cullMode != CullMode.Off) return;

            SetIfExists(destination, "_FlipNormal", 1f);
            SetIfExists(destination, "_BackfaceForceShadow", 0.5f);
        }

        private static void ApplyShadow2OpacityZero(Material destination)
        {
            if (!destination.HasProperty("_Shadow2ndColor")) return;
            var shadow2 = destination.GetColor("_Shadow2ndColor");
            shadow2.a = 0f;
            destination.SetColor("_Shadow2ndColor", shadow2);
        }

        private static void CopyTexture(Material source, Material destination, IReadOnlyList<string> fromCandidates, IReadOnlyList<string> toCandidates, ConversionReport report, bool ignoreTinyDummyTexture = false)
        {
            if (!TryFindExistingProperty(source, fromCandidates, out var from)) return;
            if (!TryFindExistingProperty(destination, toCandidates, out var to))
            {
                report?.RegisterUnsupported(from);
                return;
            }
            var texture = source.GetTexture(from);
            if (ignoreTinyDummyTexture && IsLikelyDummyTexture(texture))
            {
                destination.SetTexture(to, null);
                return;
            }

            destination.SetTexture(to, texture);
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
            if (TryGetPropertyType(material, propertyName, out var propertyType) && !IsNumericPropertyType(propertyType)) return;
            material.SetFloat(propertyName, value);
        }

        private static void SetTextureIfExists(Material material, string propertyName, Texture texture)
        {
            if (material == null) return;
            if (!material.HasProperty(propertyName)) return;
            material.SetTexture(propertyName, texture);
        }

        private static void SetVectorComponentIfExists(Material material, string propertyName, int componentIndex, float value)
        {
            if (material == null) return;
            if (!material.HasProperty(propertyName)) return;
            if (!TryGetPropertyType(material, propertyName, out var propertyType)
                || propertyType != ShaderPropertyType.Vector) return;
            if (componentIndex < 0 || componentIndex > 3) return;

            var vector = material.GetVector(propertyName);
            vector[componentIndex] = value;
            material.SetVector(propertyName, vector);
        }

        private static void ApplyUvAnimationMapping(Material source, Material destination)
        {
            if (source == null || destination == null) return;

            var hasScrollX = TryGetFloat(source, new[] { "_UvAnimScrollXSpeed", "_UvAnimScrollX", "_UvAnimationScrollXSpeedFactor" }, out var scrollX);
            var hasScrollY = TryGetFloat(source, new[] { "_UvAnimScrollYSpeed", "_UvAnimScrollY", "_UvAnimationScrollYSpeedFactor" }, out var scrollY);
            var hasRotation = TryGetFloat(source, new[] { "_UvAnimRotationSpeed", "_UvAnimRotation", "_UvAnimationRotationSpeedFactor" }, out var rotation);

            if (destination.HasProperty("_EmissionBlendMask_ScrollRotate") && (hasScrollX || hasScrollY || hasRotation))
            {
                var current = destination.GetVector("_EmissionBlendMask_ScrollRotate");
                current.x = hasScrollX ? scrollX : current.x;
                current.y = hasScrollY ? scrollY : current.y;
                current.w = hasRotation ? rotation : current.w;
                destination.SetVector("_EmissionBlendMask_ScrollRotate", current);
            }

            if (source.HasProperty("_UvAnimMaskTex"))
            {
                var maskTex = source.GetTexture("_UvAnimMaskTex");
                SetTextureIfExists(destination, "_EmissionBlendMask", maskTex);
            }
        }

        private static bool TryGetFloat(Material material, IReadOnlyList<string> candidates, out float value)
        {
            value = 0f;
            if (!TryFindExistingProperty(material, candidates, out var propertyName)) return false;
            value = material.GetFloat(propertyName);
            return true;
        }

        private static bool HasTexture(Material material, bool ignoreTinyDummyTexture, params string[] properties)
        {
            if (material == null) return false;
            for (var i = 0; i < properties.Length; i++)
            {
                var propertyName = properties[i];
                if (!material.HasProperty(propertyName)) continue;
                var texture = material.GetTexture(propertyName);
                if (texture == null) continue;
                if (ignoreTinyDummyTexture && IsLikelyDummyTexture(texture)) continue;
                return true;
            }

            return false;
        }

        private static bool IsLikelyDummyTexture(Texture texture)
        {
            if (texture == null) return false;
            return texture.width == 8 && texture.height == 8;
        }

        private static bool HasNonDefaultColor(Material material, IReadOnlyList<string> candidates, Color defaultColor, float epsilon = 0.0001f)
        {
            if (material == null) return false;
            if (!TryFindExistingProperty(material, candidates, out var propertyName)) return false;
            var color = material.GetColor(propertyName);
            return Mathf.Abs(color.r - defaultColor.r) > epsilon
                || Mathf.Abs(color.g - defaultColor.g) > epsilon
                || Mathf.Abs(color.b - defaultColor.b) > epsilon
                || Mathf.Abs(color.a - defaultColor.a) > epsilon;
        }

        private static bool HasAnyProperty(Material material, params string[] propertyNames)
        {
            if (material == null) return false;
            for (var i = 0; i < propertyNames.Length; i++)
            {
                if (material.HasProperty(propertyNames[i])) return true;
            }

            return false;
        }

        private static bool HasNonDefaultFloat(Material material, IReadOnlyList<string> candidates, float defaultValue, float epsilon = 0.0001f)
        {
            if (material == null) return false;
            if (!TryFindExistingProperty(material, candidates, out var propertyName)) return false;
            return Mathf.Abs(material.GetFloat(propertyName) - defaultValue) > epsilon;
        }

        private static bool IsNumericPropertyType(ShaderPropertyType propertyType)
        {
            // Unity バージョン差で ShaderPropertyType.Int が存在しない場合があるため、
            // enum 比較ではなく名前比較で Int を許可する。
            return propertyType == ShaderPropertyType.Float
                || propertyType == ShaderPropertyType.Range
                || propertyType.ToString() == "Int";
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
                    SetIfExists(destination, "_UseClipping", 0f);
                    SetIfExists(destination, "_AlphaMode", 0f);
                    break;
                case RenderType.Cutout:
                    destination.EnableKeyword("_ALPHATEST_ON");
                    destination.DisableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    SetIfExists(destination, "_Cutoff", cutoff);
                    SetIfExists(destination, "_UseClipping", 1f);
                    SetIfExists(destination, "_AlphaMode", 1f);
                    break;
                case RenderType.Transparent:
                    destination.DisableKeyword("_ALPHATEST_ON");
                    destination.EnableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    SetIfExists(destination, "_Cutoff", 0.001f);
                    SetIfExists(destination, "_UseClipping", 0f);
                    SetIfExists(destination, "_AlphaMode", 2f);
                    break;
            }
        }

        private static void ApplyBlendSetup(Material source, Material destination, RenderType renderType)
        {
            var hasExplicitSrcBlend = source != null && HasAnyProperty(source, "_M_SrcBlend", "_SrcBlend");
            var hasExplicitDstBlend = source != null && HasAnyProperty(source, "_M_DstBlend", "_DstBlend");
            var hasExplicitZWrite = source != null && HasAnyProperty(source, "_M_ZWrite", "_ZWrite");

            switch (renderType)
            {
                case RenderType.Opaque:
                case RenderType.Cutout:
                    if (!hasExplicitSrcBlend) SetIfExists(destination, "_SrcBlend", (float)BlendMode.One);
                    if (!hasExplicitDstBlend) SetIfExists(destination, "_DstBlend", (float)BlendMode.Zero);
                    if (!hasExplicitZWrite) SetIfExists(destination, "_ZWrite", 1f);
                    break;
                case RenderType.Transparent:
                    if (!hasExplicitSrcBlend) SetIfExists(destination, "_SrcBlend", (float)BlendMode.SrcAlpha);
                    if (!hasExplicitDstBlend) SetIfExists(destination, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    break;
            }
        }

        private static void ApplyTransparentMode(Material destination, RenderType renderType)
        {
            var modeValue = renderType switch
            {
                RenderType.Opaque => 0f,
                RenderType.Cutout => 1f,
                _ => 2f,
            };

            // lilToon はバージョンによって命名差分があるため候補をまとめて設定する。
            SetIfExists(destination, "_TransparentMode", modeValue);
            SetIfExists(destination, "_RenderingMode", modeValue);
            SetIfExists(destination, "_RenderMode", modeValue);
        }

        private static void ApplyTransparentZWrite(Material destination, RenderType renderType, bool transparentWithZWrite)
        {
            if (renderType != RenderType.Transparent)
            {
                SetIfExists(destination, "_ZWrite", 1f);
                return;
            }

            SetIfExists(destination, "_ZWrite", transparentWithZWrite ? 1f : 0f);
        }

        private static void ApplyRenderTypeTag(Material destination, RenderType renderType)
        {
            if (destination == null) return;
            switch (renderType)
            {
                case RenderType.Opaque:
                    destination.SetOverrideTag("RenderType", "Opaque");
                    break;
                case RenderType.Cutout:
                    destination.SetOverrideTag("RenderType", "TransparentCutout");
                    break;
                case RenderType.Transparent:
                    destination.SetOverrideTag("RenderType", "Transparent");
                    break;
            }
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
