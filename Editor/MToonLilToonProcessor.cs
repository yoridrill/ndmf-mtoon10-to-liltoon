using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace NdmfMToon10ToLilToon
{
    internal static class MToonLilToonProcessor
    {
        private sealed class HairMergeCacheEntry
        {
            public Material mergedTemplate;
            public Material fakeShadowTemplate;
            public List<Rect> atlasRects;
        }

        private static readonly Dictionary<string, HairMergeCacheEntry> HairMergeCache = new();

        internal static void ApplyGlobalOverridesToConvertedMaterials(MToonLilToonComponent component, LilToonGlobalOverrides overrides)
        {
            if (component == null || overrides == null) return;

            var materials = component.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer != null ? renderer.sharedMaterials : System.Array.Empty<Material>())
                .Where(material => material != null
                    && material.shader != null
                    && material.shader.name.IndexOf("liltoon", System.StringComparison.OrdinalIgnoreCase) >= 0)
                .Distinct()
                .ToList();

            for (var i = 0; i < materials.Count; i++)
            {
                MToonToLilToonMapper.ApplyGlobalOverridesToMaterial(materials[i], overrides);
            }
        }

        internal static void ApplyOnBuild(MToonLilToonComponent component, System.Action<string> onProgress = null)
        {
            if (component == null) return;
            EnsureHairSelectionsMatchAvatarMaterials(component);

            if (component.isPreviewing)
            {
                component.isPreviewing = false;
                if (component.verboseLog)
                {
                    Debug.LogWarning("[MToon10ToLilToon] Preview state was active on this component and has been reset before conversion.", component);
                }
            }

            var report = new ConversionReport();
            var lilToonShader = ResolveLilToonShader(component);
            if (lilToonShader == null)
            {
                component.warnings = new List<string> { "lilToon shader was not found in this project. Conversion skipped." };
                component.scannedMaterialCount = 0;
                component.convertedMaterialCount = 0;
                component.skippedMaterialCount = 0;
                component.unsupportedProperties = new List<string>();
                if (component.verboseLog)
                {
                    Debug.LogWarning("[MToon10ToLilToon] lilToon shader was not found. Conversion skipped.", component);
                }
                return;
            }

            var selectedForMerge = component.enableHairMerge
                ? component.hairSelections.Where(s => s.selected && s.material != null).Select(s => s.material).ToHashSet()
                : new HashSet<Material>();
            var convertedBySource = new Dictionary<Material, Material>();
            var fakeShadowPairs = new List<(Material hair, Material fake)>();
            var mergedHairMaterials = new List<Material>();
            onProgress?.Invoke("Converting materials...");
            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                ProcessRenderer(
                    renderer,
                    selectedForMerge,
                    lilToonShader,
                    component.globalOverrides,
                    component.enableFakeShadow,
                    component.fakeShadowDirection,
                    component.fakeShadowOffset,
                    convertedBySource,
                    fakeShadowPairs,
                    mergedHairMaterials,
                    report,
                    onProgress);
            }

            var resolvedFaceMaterial = component.fakeShadowFaceMaterial != null
                ? (convertedBySource.TryGetValue(component.fakeShadowFaceMaterial, out var convertedFace)
                    ? convertedFace
                    : component.fakeShadowFaceMaterial)
                : null;
            var resolvedFaceShadowMaterial = component.faceShadowFaceMaterial != null
                ? (convertedBySource.TryGetValue(component.faceShadowFaceMaterial, out var convertedFaceShadow)
                    ? convertedFaceShadow
                    : component.faceShadowFaceMaterial)
                : null;
            var resolvedEyebrowMaterial = component.eyebrowStencilMaterial != null
                ? (convertedBySource.TryGetValue(component.eyebrowStencilMaterial, out var convertedEyebrow)
                    ? convertedEyebrow
                    : component.eyebrowStencilMaterial)
                : null;

            if (component.enableEyebrowStencil
                && resolvedFaceMaterial != null
                && resolvedEyebrowMaterial != null)
            {
                ApplyEyebrowMaterialOverride(resolvedEyebrowMaterial);
                ApplyStencilSettingsForFace(resolvedFaceMaterial);
                ApplyStencilSettingsForEyebrow(resolvedEyebrowMaterial);
            }

            if (resolvedFaceMaterial != null
                && (component.enableEyebrowStencil || component.enableFakeShadow))
            {
                for (var i = 0; i < mergedHairMaterials.Count; i++)
                {
                    ApplyStencilSettingsForFrontHair(mergedHairMaterials[i]);
                }
            }

            if (component.enableFakeShadow
                && resolvedFaceMaterial != null
                && fakeShadowPairs.Count > 0)
            {
                for (var i = 0; i < fakeShadowPairs.Count; i++)
                {
                    ApplyStencilSettingsForFace(resolvedFaceMaterial);
                    ApplyStencilSettingsForFrontHair(fakeShadowPairs[i].hair);
                    ApplyStencilSettingsForFakeShadow(fakeShadowPairs[i].fake);
                    SyncFakeShadowColor(resolvedFaceMaterial, fakeShadowPairs[i].fake);
                }
            }

            if (resolvedFaceShadowMaterial != null && component.enableFaceShadowTuning)
            {
                ApplyFaceShadowMaskSettings(
                    resolvedFaceShadowMaterial,
                    component.faceShadowSdfTexture,
                    component.faceShadowMaskType,
                    component.shadowStrengthMaskLod);
            }

            if (component.disableShadowReceiveForFace || component.disableBacklightStrengthForFace)
            {
                ApplyFaceGlobalExclusionSettings(
                    resolvedFaceMaterial,
                    component.disableShadowReceiveForFace,
                    component.disableBacklightStrengthForFace);
                if (resolvedFaceShadowMaterial != resolvedFaceMaterial)
                {
                    ApplyFaceGlobalExclusionSettings(
                        resolvedFaceShadowMaterial,
                        component.disableShadowReceiveForFace,
                        component.disableBacklightStrengthForFace);
                }
            }

            component.scannedMaterialCount = report.ScannedMaterialCount;
            component.convertedMaterialCount = report.ConvertedMaterialCount;
            component.skippedMaterialCount = report.SkippedMaterialCount;
            component.warnings = report.Warnings.Select(w => w.Message).ToList();
            component.unsupportedProperties = report.UnsupportedPropertySummary.Select(kv => $"{kv.Key}:{kv.Value}").ToList();
            LogVerboseReportIfNeeded(component, report);
        }

        private static void EnsureHairSelectionsMatchAvatarMaterials(MToonLilToonComponent component)
        {
            if (component == null || !component.enableHairMerge || component.hairSelections == null || component.hairSelections.Count == 0) return;

            var avatarMaterials = component.GetComponentsInChildren<Renderer>(true)
                .SelectMany(renderer => renderer != null ? renderer.sharedMaterials : System.Array.Empty<Material>())
                .Where(material => material != null)
                .Distinct()
                .ToHashSet();

            var hasExternalReference = component.hairSelections
                .Where(selection => selection != null && selection.material != null)
                .Any(selection => !avatarMaterials.Contains(selection.material));
            if (!hasExternalReference) return;

            component.hairSelections = HairMaterialSelector.BuildDefaultSelections(
                avatarMaterials.Where(MToonDetector.IsMToonLike));
        }

        private static void LogVerboseReportIfNeeded(MToonLilToonComponent component, ConversionReport report)
        {
            if (component == null || report == null || !component.verboseLog) return;

            var unsupportedSummary = report.UnsupportedPropertySummary.Count > 0
                ? string.Join(", ", report.UnsupportedPropertySummary.Select(kv => $"{kv.Key}:{kv.Value}"))
                : "none";
            var warnings = report.Warnings.Count > 0
                ? string.Join(" | ", report.Warnings.Select(w => w.Message))
                : "none";

            Debug.Log(
                $"[MToon10ToLilToon] scanned={report.ScannedMaterialCount}, converted={report.ConvertedMaterialCount}, skipped={report.SkippedMaterialCount}, warnings={warnings}, unsupported={unsupportedSummary}",
                component);
        }

        private static void ProcessRenderer(
            Renderer renderer,
            HashSet<Material> selectedForMerge,
            Shader lilToonShader,
            LilToonGlobalOverrides globalOverrides,
            bool enableFakeShadow,
            Vector3 fakeShadowDirection,
            float fakeShadowOffset,
            IDictionary<Material, Material> convertedBySource,
            IList<(Material hair, Material fake)> fakeShadowPairs,
            IList<Material> mergedHairMaterials,
            ConversionReport report,
            System.Action<string> onProgress)
        {
            if (renderer == null) return;

            var original = renderer.sharedMaterials;
            var result = new List<Material>(original.Length);
            var resultSourceIndices = new List<int>(original.Length);
            var transparentRanks = BuildTransparentRanks(original);
            report.ScannedMaterialCount += original.Length;

            var mergedMaterialCreated = false;
            Material mergedMaterial = null;
            Material fakeShadowMaterial = null;
            var mergedIndices = new List<int>();
            var mergedRects = new List<Rect>();

            for (var i = 0; i < original.Length; i++)
            {
                var source = original[i];
                if (source == null)
                {
                    result.Add(null);
                    resultSourceIndices.Add(i);
                    report.SkippedMaterialCount++;
                    continue;
                }

                var canMerge = selectedForMerge.Contains(source);
                if (MToonToLilToonMapper.TryConvert(source, lilToonShader, globalOverrides, out var converted, report))
                {
                    result.Add(converted);
                    report.ConvertedMaterialCount++;
                    if (canMerge)
                    {
                        mergedIndices.Add(i);
                    }
                    resultSourceIndices.Add(i);
                    if (convertedBySource != null && source != null && !convertedBySource.ContainsKey(source))
                    {
                        convertedBySource[source] = converted;
                    }
                }
                else
                {
                    result.Add(source);
                    report.SkippedMaterialCount++;
                    report.Warnings.Add(new ConversionWarning($"{source.name}: skipped (not convertible)"));
                    resultSourceIndices.Add(i);
                    if (convertedBySource != null && source != null && !convertedBySource.ContainsKey(source))
                    {
                        convertedBySource[source] = source;
                    }
                }
            }

            var mergedOutputRenderType = mergedIndices.Count >= 1
                ? ResolveMergedOutputRenderType(original, mergedIndices)
                : RenderType.Cutout;

            if (mergedIndices.Count >= 1
                && TryMergeHairMaterials(
                    original,
                    mergedIndices,
                    mergedOutputRenderType,
                    lilToonShader,
                    globalOverrides,
                    enableFakeShadow,
                    fakeShadowDirection,
                    fakeShadowOffset,
                    report,
                    out mergedMaterial,
                    out fakeShadowMaterial,
                    out mergedRects,
                    onProgress))
            {
                mergedMaterialCreated = true;
                var mergedRepresentativeIndex = ResolveMergedRepresentativeIndex(original, mergedIndices, transparentRanks, mergedOutputRenderType);
                onProgress?.Invoke("Rebuilding mesh...");
                ApplyMergedMaterialAndMesh(renderer, result, resultSourceIndices, mergedIndices, mergedRepresentativeIndex, mergedMaterial, fakeShadowMaterial, mergedRects, report);
                if (mergedHairMaterials != null && mergedMaterial != null)
                {
                    mergedHairMaterials.Add(mergedMaterial);
                }
                if (fakeShadowPairs != null && fakeShadowMaterial != null)
                {
                    fakeShadowPairs.Add((mergedMaterial, fakeShadowMaterial));
                }
            }

            if (!mergedMaterialCreated && mergedIndices.Count >= 1)
            {
                report.Warnings.Add(new ConversionWarning("hair merge/atlas failed, fallback to per-material conversion"));
            }

            ReindexTransparentQueues(result, resultSourceIndices, transparentRanks);
            renderer.sharedMaterials = result.ToArray();
        }

        private static Shader ResolveLilToonShader(MToonLilToonComponent component)
        {
            if (component.lilToonShader != null) return component.lilToonShader;

            var resolved = Shader.Find("lilToon");
            if (resolved == null)
            {
                var guids = AssetDatabase.FindAssets("lilToon t:Shader");
                resolved = guids
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .Select(AssetDatabase.LoadAssetAtPath<Shader>)
                    .FirstOrDefault(shader => shader != null && shader.name == "lilToon")
                    ?? guids
                        .Select(AssetDatabase.GUIDToAssetPath)
                        .Select(AssetDatabase.LoadAssetAtPath<Shader>)
                        .FirstOrDefault(shader => shader != null && shader.name.IndexOf("liltoon", System.StringComparison.OrdinalIgnoreCase) >= 0);
            }

            component.lilToonShader = resolved;
            return resolved;
        }

        private static bool TryMergeHairMaterials(
            IReadOnlyList<Material> original,
            IReadOnlyList<int> mergedIndices,
            RenderType mergedOutputRenderType,
            Shader lilToonShader,
            LilToonGlobalOverrides overrides,
            bool enableFakeShadow,
            Vector3 fakeShadowDirection,
            float fakeShadowOffset,
            ConversionReport report,
            out Material mergedMaterial,
            out Material fakeShadowMaterial,
            out List<Rect> atlasRects,
            System.Action<string> onProgress)
        {
            mergedMaterial = null;
            fakeShadowMaterial = null;
            atlasRects = null;

            var cacheKey = BuildHairMergeCacheKey(original, mergedIndices, mergedOutputRenderType, enableFakeShadow);
            if (TryGetCachedHairMergeResult(cacheKey, overrides, fakeShadowDirection, fakeShadowOffset, out mergedMaterial, out fakeShadowMaterial, out atlasRects))
            {
                return true;
            }

            if (!MToonToLilToonMapper.TryConvert(original[mergedIndices[0]], lilToonShader, overrides, out mergedMaterial, report))
            {
                return false;
            }
            ForceMergedRenderType(mergedMaterial, original[mergedIndices[0]], mergedOutputRenderType);
            fakeShadowMaterial = CreateFakeShadowMaterial(mergedMaterial, enableFakeShadow, fakeShadowDirection, fakeShadowOffset, report);

            if (mergedIndices.Count == 1)
            {
                atlasRects = new List<Rect> { new(0f, 0f, 1f, 1f) };
                return true;
            }

            onProgress?.Invoke("Baking atlas...");
            var atlasTextures = new List<Texture2D>();
            for (var i = 0; i < mergedIndices.Count; i++)
            {
                var source = original[mergedIndices[i]];
                Texture texture = null;
                Vector2 scale = Vector2.one;
                Vector2 offset = Vector2.zero;
                if (source != null)
                {
                    if (source.HasProperty("_BaseMap"))
                    {
                        texture = source.GetTexture("_BaseMap");
                        scale = source.GetTextureScale("_BaseMap");
                        offset = source.GetTextureOffset("_BaseMap");
                    }
                    else if (source.HasProperty("_MainTex"))
                    {
                        texture = source.GetTexture("_MainTex");
                        scale = source.GetTextureScale("_MainTex");
                        offset = source.GetTextureOffset("_MainTex");
                    }
                }

                atlasTextures.Add(ToReadableTextureWithTransform(texture, scale, offset));
            }

            if (atlasTextures.All(t => t == null))
            {
                atlasRects = mergedIndices.Select(_ => new Rect(0f, 0f, 1f, 1f)).ToList();
                return true;
            }

            var fallback = FirstNonNullTexture(atlasTextures) ?? NewSolidTexture(Color.white);
            var atlasMaxSize = ResolveAtlasMaxSize(atlasTextures);
            var packTextures = PrepareBaseAtlasTextures(atlasTextures, fallback);
            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            atlasRects = atlas.PackTextures(packTextures, 2, atlasMaxSize, false).ToList();
            BleedTransparentPixels(atlas, 2);
            mergedMaterial.SetTexture("_MainTex", atlas);
            if (mergedMaterial.HasProperty("_MainTex"))
            {
                mergedMaterial.SetTextureScale("_MainTex", Vector2.one);
                mergedMaterial.SetTextureOffset("_MainTex", Vector2.zero);
            }

            BakeOptionalAtlas(new[] { "_ShadowColorTex", "_Shadow1stColorTex" }, original, mergedIndices, mergedMaterial, new[] { "_ShadeMap", "_ShadeMultiplyTexture" }, atlas.width, atlas.height, atlasRects);
            BakeOptionalAtlas(new[] { "_EmissionMap" }, original, mergedIndices, mergedMaterial, new[] { "_EmissiveMap", "_EmissionMap" }, atlas.width, atlas.height, atlasRects);
            BakeOptionalAtlas(new[] { "_BumpMap" }, original, mergedIndices, mergedMaterial, new[] { "_NormalMap", "_BumpMap" }, atlas.width, atlas.height, atlasRects);
            BakeOptionalAtlas(new[] { "_OutlineTex", "_OutlineMask" }, original, mergedIndices, mergedMaterial, new[] { "_OutlineWidthMultiplyTexture", "_OutlineMask" }, atlas.width, atlas.height, atlasRects);
            CacheHairMergeResult(cacheKey, mergedMaterial, fakeShadowMaterial, atlasRects);

            return true;
        }

        private static string BuildHairMergeCacheKey(
            IReadOnlyList<Material> original,
            IReadOnlyList<int> mergedIndices,
            RenderType mergedOutputRenderType,
            bool enableFakeShadow)
        {
            var parts = new List<string> { mergedOutputRenderType.ToString(), enableFakeShadow ? "1" : "0" };
            for (var i = 0; i < mergedIndices.Count; i++)
            {
                var index = mergedIndices[i];
                if (index < 0 || index >= original.Count) continue;
                var mat = original[index];
                if (mat == null)
                {
                    parts.Add("null");
                    continue;
                }

                var tex = mat.HasProperty("_BaseMap") ? mat.GetTexture("_BaseMap") : (mat.HasProperty("_MainTex") ? mat.GetTexture("_MainTex") : null);
                var texId = tex != null ? tex.GetInstanceID() : 0;
                var scale = mat.HasProperty("_BaseMap")
                    ? mat.GetTextureScale("_BaseMap")
                    : (mat.HasProperty("_MainTex") ? mat.GetTextureScale("_MainTex") : Vector2.one);
                var offset = mat.HasProperty("_BaseMap")
                    ? mat.GetTextureOffset("_BaseMap")
                    : (mat.HasProperty("_MainTex") ? mat.GetTextureOffset("_MainTex") : Vector2.zero);
                parts.Add($"{index}:{mat.GetInstanceID()}:{texId}:{scale.x:G5},{scale.y:G5}:{offset.x:G5},{offset.y:G5}");
            }

            return string.Join("|", parts);
        }

        private static bool TryGetCachedHairMergeResult(
            string cacheKey,
            LilToonGlobalOverrides overrides,
            Vector3 fakeShadowDirection,
            float fakeShadowOffset,
            out Material mergedMaterial,
            out Material fakeShadowMaterial,
            out List<Rect> atlasRects)
        {
            mergedMaterial = null;
            fakeShadowMaterial = null;
            atlasRects = null;
            if (string.IsNullOrEmpty(cacheKey)) return false;
            if (!HairMergeCache.TryGetValue(cacheKey, out var cached) || cached == null || cached.mergedTemplate == null) return false;

            mergedMaterial = new Material(cached.mergedTemplate)
            {
                name = $"{cached.mergedTemplate.name}_Cached",
            };
            MToonToLilToonMapper.ApplyGlobalOverridesToMaterial(mergedMaterial, overrides);
            atlasRects = cached.atlasRects?.Select(rect => rect).ToList() ?? new List<Rect>();

            if (cached.fakeShadowTemplate != null)
            {
                fakeShadowMaterial = new Material(cached.fakeShadowTemplate)
                {
                    name = $"{cached.fakeShadowTemplate.name}_Cached",
                };
                ApplyFakeShadowOverrides(fakeShadowMaterial, true, fakeShadowDirection, fakeShadowOffset);
            }

            return true;
        }

        private static void CacheHairMergeResult(string cacheKey, Material mergedMaterial, Material fakeShadowMaterial, IReadOnlyList<Rect> atlasRects)
        {
            if (string.IsNullOrEmpty(cacheKey) || mergedMaterial == null) return;

            var mergedTemplate = new Material(mergedMaterial)
            {
                name = $"{mergedMaterial.name}_Template",
            };
            var fakeTemplate = fakeShadowMaterial != null
                ? new Material(fakeShadowMaterial) { name = $"{fakeShadowMaterial.name}_Template" }
                : null;
            HairMergeCache[cacheKey] = new HairMergeCacheEntry
            {
                mergedTemplate = mergedTemplate,
                fakeShadowTemplate = fakeTemplate,
                atlasRects = atlasRects?.Select(rect => rect).ToList() ?? new List<Rect>(),
            };
        }

        private static void ForceMergedRenderType(Material destination, Material source, RenderType outputRenderType)
        {
            if (destination == null) return;

            var cutoff = 0.5f;
            if (source != null)
            {
                if (source.HasProperty("_AlphaCutoff"))
                {
                    cutoff = source.GetFloat("_AlphaCutoff");
                }
                else if (source.HasProperty("_Cutoff"))
                {
                    cutoff = source.GetFloat("_Cutoff");
                }
            }

            switch (outputRenderType)
            {
                case RenderType.Transparent:
                    destination.DisableKeyword("_ALPHATEST_ON");
                    destination.EnableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    destination.SetOverrideTag("RenderType", "Transparent");
                    destination.renderQueue = (int)RenderQueue.Transparent;
                    SetFloatIfExists(destination, "_UseClipping", 0f);
                    SetFloatIfExists(destination, "_AlphaMode", 2f);
                    SetFloatIfExists(destination, "_SrcBlend", (float)BlendMode.SrcAlpha);
                    SetFloatIfExists(destination, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                    SetFloatIfExists(destination, "_ZWrite", 0f);
                    SetFloatIfExists(destination, "_TransparentMode", 2f);
                    SetFloatIfExists(destination, "_RenderingMode", 2f);
                    SetFloatIfExists(destination, "_RenderMode", 2f);
                    break;
                case RenderType.Cutout:
                default:
                    destination.EnableKeyword("_ALPHATEST_ON");
                    destination.DisableKeyword("_ALPHABLEND_ON");
                    destination.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    destination.SetOverrideTag("RenderType", "TransparentCutout");
                    destination.renderQueue = (int)RenderQueue.AlphaTest;
                    SetFloatIfExists(destination, "_Cutoff", cutoff);
                    SetFloatIfExists(destination, "_UseClipping", 1f);
                    SetFloatIfExists(destination, "_AlphaMode", 1f);
                    SetFloatIfExists(destination, "_SrcBlend", (float)BlendMode.One);
                    SetFloatIfExists(destination, "_DstBlend", (float)BlendMode.Zero);
                    SetFloatIfExists(destination, "_ZWrite", 1f);
                    SetFloatIfExists(destination, "_TransparentMode", 1f);
                    SetFloatIfExists(destination, "_RenderingMode", 1f);
                    SetFloatIfExists(destination, "_RenderMode", 1f);
                    break;
            }
        }

        private static RenderType ResolveMergedOutputRenderType(IReadOnlyList<Material> original, IReadOnlyList<int> mergedIndices)
        {
            var transparentCount = 0;
            for (var i = 0; i < mergedIndices.Count; i++)
            {
                var source = original[mergedIndices[i]];
                if (RenderTypeResolver.ResolveFromMaterial(source) == RenderType.Transparent)
                {
                    transparentCount++;
                }
            }

            var nonTransparentCount = mergedIndices.Count - transparentCount;
            return transparentCount > nonTransparentCount ? RenderType.Transparent : RenderType.Cutout;
        }

        private static int ResolveMergedRepresentativeIndex(IReadOnlyList<Material> original, IReadOnlyList<int> mergedIndices, IReadOnlyDictionary<int, int> transparentRanks, RenderType mergedOutputRenderType)
        {
            if (mergedOutputRenderType == RenderType.Transparent)
            {
                return mergedIndices
                    .OrderBy(i => transparentRanks.TryGetValue(i, out var rank) ? rank : int.MaxValue)
                    .ThenBy(i => i)
                    .First();
            }

            return mergedIndices
                .OrderBy(i => RenderTypeResolver.ResolveFromMaterial(original[i]) == RenderType.Transparent ? 1 : 0)
                .ThenBy(i => i)
                .First();
        }

        private static void SetFloatIfExists(Material material, string propertyName, float value)
        {
            if (material == null || !material.HasProperty(propertyName)) return;
            material.SetFloat(propertyName, value);
        }

        private static void ApplyFakeShadowOverrides(Material material, bool enableFakeShadow, Vector3 fakeShadowDirection, float fakeShadowOffset)
        {
            if (material == null) return;
            if (!enableFakeShadow)
            {
                SetFloatIfAnyExists(material, new[] { "_UseFakeShadow", "_EnableFakeShadow", "_FakeShadow" }, 0f);
                return;
            }

            SetFloatIfAnyExists(material, new[] { "_UseFakeShadow", "_EnableFakeShadow", "_FakeShadow" }, 1f);

            var fakeShadowDirectionVector = new Vector4(fakeShadowDirection.x, fakeShadowDirection.y, fakeShadowDirection.z, fakeShadowOffset);
            SetVectorIfAnyExists(material, new[] { "_FakeShadowVector", "_FakeShadowDir", "_FakeShadowDirection" }, fakeShadowDirectionVector);

            SetFloatIfAnyExists(material, new[] { "_FakeShadowOffset", "_FakeShadowPositionOffset" }, fakeShadowOffset);
        }

        private static void SetVectorIfAnyExists(Material material, IReadOnlyList<string> propertyNames, Vector4 value)
        {
            if (material == null || propertyNames == null) return;

            for (var i = 0; i < propertyNames.Count; i++)
            {
                var propertyName = propertyNames[i];
                if (!material.HasProperty(propertyName)) continue;
                material.SetVector(propertyName, value);
            }
        }

        private static void SetFloatIfAnyExists(Material material, IReadOnlyList<string> propertyNames, float value)
        {
            if (material == null || propertyNames == null) return;

            for (var i = 0; i < propertyNames.Count; i++)
            {
                var propertyName = propertyNames[i];
                if (!material.HasProperty(propertyName)) continue;
                material.SetFloat(propertyName, value);
            }
        }

        private static void SetTextureIfAnyExists(Material material, IReadOnlyList<string> propertyNames, Texture texture)
        {
            if (material == null || propertyNames == null) return;

            for (var i = 0; i < propertyNames.Count; i++)
            {
                var propertyName = propertyNames[i];
                if (!material.HasProperty(propertyName)) continue;
                material.SetTexture(propertyName, texture);
            }
        }

        private static void ApplyFaceShadowMaskSettings(
            Material faceMaterial,
            Texture sdfTexture,
            MToonLilToonComponent.FaceShadowMaskType maskType,
            float shadowStrengthMaskLod)
        {
            if (faceMaterial == null) return;

            SetFloatIfAnyExists(faceMaterial, new[] { "_UseShadowMask", "_UseShadowStrengthMask" }, 1f);
            var shadowMaskTypeValue = maskType switch
            {
                MToonLilToonComponent.FaceShadowMaskType.Strength => 0f,
                MToonLilToonComponent.FaceShadowMaskType.Flat => 1f,
                MToonLilToonComponent.FaceShadowMaskType.Sdf => 2f,
                _ => 1f
            };
            SetFloatIfAnyExists(faceMaterial, new[] { "_ShadowMaskType" }, shadowMaskTypeValue);
            SetTextureIfAnyExists(faceMaterial, new[] { "_ShadowStrengthMask" }, sdfTexture);
            SetFloatIfAnyExists(faceMaterial, new[] { "_ShadowStrengthMaskLOD" }, Mathf.Clamp01(shadowStrengthMaskLod));
        }

        private static void ApplyFaceGlobalExclusionSettings(
            Material faceMaterial,
            bool disableShadowReceiveForFace,
            bool disableBacklightStrengthForFace)
        {
            if (faceMaterial == null) return;
            if (disableShadowReceiveForFace)
            {
                SetFloatIfAnyExists(faceMaterial, new[] { "_ShadowReceive" }, 0f);
            }

            if (disableBacklightStrengthForFace)
            {
                SetFloatIfAnyExists(faceMaterial, new[] { "_UseBacklight", "_BacklightMainStrength" }, 0f);
                if (faceMaterial.HasProperty("_BacklightColor"))
                {
                    var backlightColor = faceMaterial.GetColor("_BacklightColor");
                    backlightColor.a = 0f;
                    faceMaterial.SetColor("_BacklightColor", backlightColor);
                }
            }
        }

        private static void ApplyStencilSettings(Material material, float reference, float readMask, float writeMask, float compare, float pass, float fail, float zFail)
        {
            if (material == null) return;
            SetFloatIfAnyExists(material, new[] { "_StencilRef", "_Ref" }, reference);
            SetFloatIfAnyExists(material, new[] { "_StencilReadMask", "_ReadMask" }, readMask);
            SetFloatIfAnyExists(material, new[] { "_StencilWriteMask", "_WriteMask" }, writeMask);
            SetFloatIfAnyExists(material, new[] { "_StencilComp", "_Comp" }, compare);
            SetFloatIfAnyExists(material, new[] { "_StencilPass", "_Pass" }, pass);
            SetFloatIfAnyExists(material, new[] { "_StencilFail", "_Fail" }, fail);
            SetFloatIfAnyExists(material, new[] { "_StencilZFail", "_ZFail" }, zFail);

            // Cutout + Outline 構成では本体とアウトラインでステンシル値が分かれる場合がある。
            // 髪/顔で齟齬が出ないよう同値で揃える。
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilRef", "_OutlineRef" }, reference);
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilReadMask", "_OutlineReadMask" }, readMask);
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilWriteMask", "_OutlineWriteMask" }, writeMask);
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilComp", "_OutlineComp" }, compare);
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilPass", "_OutlinePass" }, pass);
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilFail", "_OutlineFail" }, fail);
            SetFloatIfAnyExists(material, new[] { "_OutlineStencilZFail", "_OutlineZFail" }, zFail);
        }

        private static void ApplyStencilSettingsForFace(Material faceMaterial)
        {
            if (faceMaterial == null) return;
            faceMaterial.renderQueue = 2450;
            ApplyStencilSettings(
                faceMaterial,
                reference: 51f,
                readMask: 63f,
                writeMask: 63f,
                compare: (float)CompareFunction.Always,
                pass: (float)StencilOp.Replace,
                fail: (float)StencilOp.Keep,
                zFail: (float)StencilOp.Keep);
        }

        private static void ApplyStencilSettingsForEyebrow(Material eyebrowMaterial)
        {
            if (eyebrowMaterial == null) return;
            eyebrowMaterial.renderQueue = 2451;
            ApplyStencilSettings(
                eyebrowMaterial,
                reference: 128f,
                readMask: 128f,
                writeMask: 191f,
                compare: (float)CompareFunction.Always,
                pass: (float)StencilOp.Replace,
                fail: (float)StencilOp.Keep,
                zFail: (float)StencilOp.Keep);
        }

        private static void ApplyStencilSettingsForFrontHair(Material hairMaterial)
        {
            if (hairMaterial == null) return;
            hairMaterial.renderQueue = 2452;
            ApplyStencilSettings(
                hairMaterial,
                reference: 128f,
                readMask: 128f,
                writeMask: 63f,
                compare: (float)CompareFunction.NotEqual,
                pass: (float)StencilOp.Replace,
                fail: (float)StencilOp.Keep,
                zFail: (float)StencilOp.Keep);
        }

        private static void ApplyStencilSettingsForFakeShadow(Material fakeShadowMaterial)
        {
            if (fakeShadowMaterial == null) return;
            ApplyStencilSettings(
                fakeShadowMaterial,
                reference: 51f,
                readMask: 63f,
                writeMask: 0f,
                compare: (float)CompareFunction.Equal,
                pass: (float)StencilOp.Keep,
                fail: (float)StencilOp.Keep,
                zFail: (float)StencilOp.Keep);
        }

        private static void ApplyEyebrowMaterialOverride(Material eyebrowMaterial)
        {
            if (eyebrowMaterial == null) return;

            if (eyebrowMaterial.shader != null
                && eyebrowMaterial.shader.name.IndexOf("Transparent", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var hasOutline = (eyebrowMaterial.HasProperty("_UseOutline") && eyebrowMaterial.GetFloat("_UseOutline") > 0.5f)
                    || (eyebrowMaterial.HasProperty("_OutlineEnable") && eyebrowMaterial.GetFloat("_OutlineEnable") > 0.5f);
                var cutoutShader = Shader.Find(hasOutline ? "Hidden/lilToonCutoutOutline" : "Hidden/lilToonCutout");
                if (cutoutShader != null)
                {
                    eyebrowMaterial.shader = cutoutShader;
                }
            }

            eyebrowMaterial.EnableKeyword("_ALPHATEST_ON");
            eyebrowMaterial.DisableKeyword("_ALPHABLEND_ON");
            eyebrowMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            eyebrowMaterial.SetOverrideTag("RenderType", "TransparentCutout");
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_UseClipping" }, 1f);
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_AlphaMode", "_TransparentMode", "_RenderingMode", "_RenderMode" }, 1f);
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_BlendMode", "_Surface" }, 1f);
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_Cutoff" }, 0.5f);
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_SrcBlend" }, (float)BlendMode.One);
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_DstBlend" }, (float)BlendMode.Zero);
            SetFloatIfAnyExists(eyebrowMaterial, new[] { "_ZWrite" }, 1f);
            eyebrowMaterial.renderQueue = 2451;
        }

        private static void SyncFakeShadowColor(Material faceMaterial, Material fakeShadowMaterial)
        {
            if (faceMaterial == null || fakeShadowMaterial == null) return;

            Color sourceColor;
            if (!TryGetColorFromAny(faceMaterial, new[] { "_ShadowColor", "_Shadow1stColor", "_ShadeColor", "_Color" }, out sourceColor))
            {
                return;
            }

            SetColorIfAnyExists(fakeShadowMaterial, new[] { "_Color", "_MainColor", "_BaseColor", "_ShadowColor" }, sourceColor);
        }

        private static bool TryGetColorFromAny(Material material, IReadOnlyList<string> propertyNames, out Color color)
        {
            color = Color.white;
            if (material == null || propertyNames == null) return false;

            for (var i = 0; i < propertyNames.Count; i++)
            {
                var propertyName = propertyNames[i];
                if (!material.HasProperty(propertyName)) continue;
                color = material.GetColor(propertyName);
                return true;
            }

            return false;
        }

        private static void SetColorIfAnyExists(Material material, IReadOnlyList<string> propertyNames, Color color)
        {
            if (material == null || propertyNames == null) return;

            for (var i = 0; i < propertyNames.Count; i++)
            {
                var propertyName = propertyNames[i];
                if (!material.HasProperty(propertyName)) continue;
                material.SetColor(propertyName, color);
            }
        }

        private static Texture2D[] PrepareBaseAtlasTextures(IReadOnlyList<Texture2D> sourceTextures, Texture2D fallback)
        {
            var prepared = sourceTextures.Select(t => t ?? fallback).ToArray();
            const float fixedScale = 0.99f;
            var resized = new Texture2D[prepared.Length];
            for (var i = 0; i < prepared.Length; i++)
            {
                var w = Mathf.Max(1, Mathf.RoundToInt(prepared[i].width * fixedScale));
                var h = Mathf.Max(1, Mathf.RoundToInt(prepared[i].height * fixedScale));
                resized[i] = ResizeTexture(prepared[i], w, h);
            }
            return resized;
        }

        private static void BakeOptionalAtlas(IReadOnlyList<string> destinationProperties, IReadOnlyList<Material> original, IReadOnlyList<int> mergedIndices, Material mergedMaterial, IReadOnlyList<string> sourceProperties, int atlasWidth, int atlasHeight, IReadOnlyList<Rect> rects)
        {
            var destinationProperty = destinationProperties.FirstOrDefault(mergedMaterial.HasProperty);
            if (string.IsNullOrEmpty(destinationProperty)) return;

            var textures = new List<Texture2D>();
            for (var i = 0; i < mergedIndices.Count; i++)
            {
                var source = original[mergedIndices[i]];
                Texture texture = null;
                Vector2 scale = Vector2.one;
                Vector2 offset = Vector2.zero;
                if (source != null)
                {
                    var sourceProperty = sourceProperties.FirstOrDefault(source.HasProperty);
                    if (!string.IsNullOrEmpty(sourceProperty))
                    {
                        texture = source.GetTexture(sourceProperty);
                        scale = source.GetTextureScale(sourceProperty);
                        offset = source.GetTextureOffset(sourceProperty);
                    }
                }
                textures.Add(ToReadableTextureWithTransform(texture, scale, offset));
            }

            if (textures.All(t => t == null)) return;
            var fallbackColor = ResolveAtlasFallbackColor(destinationProperty);
            var fallback = FirstNonNullTexture(textures) ?? NewSolidTexture(fallbackColor);
            var atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
            atlas.SetPixels(Enumerable.Repeat(new Color(0f, 0f, 0f, 0f), atlasWidth * atlasHeight).ToArray());
            for (var i = 0; i < textures.Count && i < rects.Count; i++)
            {
                var src = textures[i] ?? fallback;
                var rect = rects[i];
                var pixelWidth = Mathf.Max(1, Mathf.RoundToInt(rect.width * atlasWidth));
                var pixelHeight = Mathf.Max(1, Mathf.RoundToInt(rect.height * atlasHeight));
                var pixelX = Mathf.RoundToInt(rect.x * atlasWidth);
                var pixelY = Mathf.RoundToInt(rect.y * atlasHeight);
                var resized = ResizeTexture(src, pixelWidth, pixelHeight);
                atlas.SetPixels(pixelX, pixelY, pixelWidth, pixelHeight, resized.GetPixels());
            }
            atlas.Apply();
            BleedTransparentPixels(atlas, 2);
            mergedMaterial.SetTexture(destinationProperty, atlas);
        }

        private static Color ResolveAtlasFallbackColor(string destinationProperty)
        {
            if (string.IsNullOrEmpty(destinationProperty)) return Color.white;
            if (destinationProperty.IndexOf("Bump", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return new Color(0.5f, 0.5f, 1f, 1f);
            }
            if (destinationProperty.IndexOf("Emission", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return Color.black;
            }
            return Color.white;
        }

        private static int ResolveAtlasMaxSize(IReadOnlyList<Texture2D> textures)
        {
            return 4096;
        }

        private static Texture2D FirstNonNullTexture(IReadOnlyList<Texture2D> textures)
        {
            if (textures == null) return null;
            for (var i = 0; i < textures.Count; i++)
            {
                if (textures[i] != null) return textures[i];
            }
            return null;
        }

        private static void BleedTransparentPixels(Texture2D texture, int iterations)
        {
            if (texture == null || iterations <= 0) return;
            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels();
            var work = new Color[pixels.Length];

            for (var iteration = 0; iteration < iterations; iteration++)
            {
                System.Array.Copy(pixels, work, pixels.Length);
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var idx = y * width + x;
                        if (pixels[idx].a > 0.0001f) continue;

                        var sum = Color.clear;
                        var count = 0;
                        for (var oy = -1; oy <= 1; oy++)
                        {
                            var ny = y + oy;
                            if (ny < 0 || ny >= height) continue;
                            for (var ox = -1; ox <= 1; ox++)
                            {
                                var nx = x + ox;
                                if (nx < 0 || nx >= width) continue;
                                var nidx = ny * width + nx;
                                var neighbor = pixels[nidx];
                                if (neighbor.a <= 0.0001f) continue;
                                sum += neighbor;
                                count++;
                            }
                        }

                        if (count <= 0) continue;
                        var averaged = sum / count;
                        var maxNeighborAlpha = 0f;
                        for (var oy = -1; oy <= 1; oy++)
                        {
                            var ny = y + oy;
                            if (ny < 0 || ny >= height) continue;
                            for (var ox = -1; ox <= 1; ox++)
                            {
                                var nx = x + ox;
                                if (nx < 0 || nx >= width) continue;
                                var nidx = ny * width + nx;
                                var neighborAlpha = pixels[nidx].a;
                                if (neighborAlpha > maxNeighborAlpha) maxNeighborAlpha = neighborAlpha;
                            }
                        }
                        averaged.a = maxNeighborAlpha;
                        work[idx] = averaged;
                    }
                }
                var tmp = pixels;
                pixels = work;
                work = tmp;
            }

            texture.SetPixels(pixels);
            texture.Apply();
        }

        private static Texture2D ResizeTexture(Texture2D source, int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var current = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            var resized = new Texture2D(width, height, TextureFormat.RGBA32, false);
            resized.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            resized.Apply();
            RenderTexture.active = current;
            RenderTexture.ReleaseTemporary(rt);
            return resized;
        }

        private static Texture2D ToReadableTexture(Texture texture)
        {
            if (texture == null) return null;
            var width = texture.width;
            var height = texture.height;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            var current = RenderTexture.active;
            Graphics.Blit(texture, rt);
            RenderTexture.active = rt;
            var readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            readable.Apply();
            RenderTexture.active = current;
            RenderTexture.ReleaseTemporary(rt);
            return readable;
        }

        private static Texture2D ToReadableTextureWithTransform(Texture texture, Vector2 scale, Vector2 offset)
        {
            var readable = ToReadableTexture(texture);
            if (readable == null) return null;
            if ((scale - Vector2.one).sqrMagnitude < 0.000001f && offset.sqrMagnitude < 0.000001f) return readable;

            var width = readable.width;
            var height = readable.height;
            var transformed = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var colors = new Color[width * height];
            for (var y = 0; y < height; y++)
            {
                var v = (y + 0.5f) / height;
                for (var x = 0; x < width; x++)
                {
                    var u = (x + 0.5f) / width;
                    var tu = Mathf.Repeat(u * scale.x + offset.x, 1f);
                    var tv = Mathf.Repeat(v * scale.y + offset.y, 1f);
                    colors[y * width + x] = SampleRepeatPoint(readable, tu, tv);
                }
            }
            transformed.SetPixels(colors);
            transformed.Apply();
            return transformed;
        }

        private static Texture2D NewSolidTexture(Color color)
        {
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            var colors = Enumerable.Repeat(color, 16).ToArray();
            texture.SetPixels(colors);
            texture.Apply();
            return texture;
        }

        private static float WrapUv01(float value)
        {
            var wrapped = value - Mathf.Floor(value);
            if (Mathf.Abs(value - 1f) < 0.000001f) return 1f;
            return Mathf.Clamp01(wrapped);
        }

        private static Color SampleRepeatPoint(Texture2D texture, float u, float v)
        {
            var width = texture.width;
            var height = texture.height;
            var x = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(u, 1f) * width), 0, Mathf.Max(0, width - 1));
            var y = Mathf.Clamp(Mathf.FloorToInt(Mathf.Repeat(v, 1f) * height), 0, Mathf.Max(0, height - 1));
            return texture.GetPixel(x, y);
        }

        private static void ApplyMergedMaterialAndMesh(Renderer renderer, List<Material> materials, List<int> materialSourceIndices, IReadOnlyList<int> mergedIndices, int mergedRepresentativeSourceIndex, Material mergedMaterial, Material fakeShadowMaterial, IReadOnlyList<Rect> rects, ConversionReport report)
        {
            var mergedIndexSet = mergedIndices.ToHashSet();
            var newMaterials = new List<Material>();
            var newSourceIndices = new List<int>();

            var mesh = renderer switch
            {
                SkinnedMeshRenderer skinned => skinned.sharedMesh,
                MeshRenderer _ => renderer.GetComponent<MeshFilter>()?.sharedMesh,
                _ => null
            };
            if (mesh == null)
            {
                for (var i = 0; i < materials.Count; i++)
                {
                    if (mergedIndexSet.Contains(i)) continue;
                    newMaterials.Add(materials[i]);
                    if (i < materialSourceIndices.Count) newSourceIndices.Add(materialSourceIndices[i]);
                }
                newMaterials.Add(mergedMaterial);
                newSourceIndices.Add(mergedRepresentativeSourceIndex);
                if (fakeShadowMaterial != null)
                {
                    newMaterials.Add(fakeShadowMaterial);
                    newSourceIndices.Add(-1);
                }
                materials.Clear();
                materials.AddRange(newMaterials);
                materialSourceIndices.Clear();
                materialSourceIndices.AddRange(newSourceIndices);
                return;
            }

            var meshCopy = Object.Instantiate(mesh);
            var vertices = meshCopy.vertices.ToList();
            var uv = meshCopy.uv;
            if (uv == null || uv.Length == 0) uv = Enumerable.Repeat(Vector2.zero, vertices.Count).ToArray();
            if (uv.Length < vertices.Count)
            {
                var expandedUv = new Vector2[vertices.Count];
                for (var i = 0; i < uv.Length; i++) expandedUv[i] = uv[i];
                uv = expandedUv;
            }
            var uvList = uv.ToList();

            var normals = meshCopy.normals;
            var tangents = meshCopy.tangents;
            var colors = meshCopy.colors;
            var boneWeights = meshCopy.boneWeights;
            var normalList = normals != null && normals.Length == vertices.Count ? normals.ToList() : null;
            var tangentList = tangents != null && tangents.Length == vertices.Count ? tangents.ToList() : null;
            var colorList = colors != null && colors.Length == vertices.Count ? colors.ToList() : null;
            var boneWeightList = boneWeights != null && boneWeights.Length == vertices.Count ? boneWeights.ToList() : null;

            var rectBySubMesh = new Dictionary<int, Rect>();
            for (var i = 0; i < mergedIndices.Count && i < rects.Count; i++)
            {
                rectBySubMesh[mergedIndices[i]] = rects[i];
            }

            var atlasTexture = mergedMaterial != null ? mergedMaterial.GetTexture("_MainTex") : null;
            const float paddingPixels = 1f;
            var padU = atlasTexture != null && atlasTexture.width > 0 ? paddingPixels / atlasTexture.width : 0f;
            var padV = atlasTexture != null && atlasTexture.height > 0 ? paddingPixels / atlasTexture.height : 0f;

            var newSubMeshTriangles = new List<int[]>();
            var mergedTriangles = new List<int>();
            for (var i = 0; i < mesh.subMeshCount; i++)
            {
                if (mergedIndexSet.Contains(i))
                {
                    var triangles = meshCopy.GetTriangles(i);
                    if (!rectBySubMesh.TryGetValue(i, out var rect))
                    {
                        mergedTriangles.AddRange(triangles);
                        continue;
                    }

                    for (var t = 0; t < triangles.Length; t++)
                    {
                        var originalIndex = triangles[t];
                        var src = originalIndex < uvList.Count ? uvList[originalIndex] : Vector2.zero;
                        var wrappedU = WrapUv01(src.x);
                        var wrappedV = WrapUv01(src.y);
                        var minU = rect.xMin + padU;
                        var maxU = rect.xMax - padU;
                        var minV = rect.yMin + padV;
                        var maxV = rect.yMax - padV;
                        if (minU > maxU)
                        {
                            var midU = (rect.xMin + rect.xMax) * 0.5f;
                            minU = midU;
                            maxU = midU;
                        }
                        if (minV > maxV)
                        {
                            var midV = (rect.yMin + rect.yMax) * 0.5f;
                            minV = midV;
                            maxV = midV;
                        }
                        var remappedUv = new Vector2(
                            Mathf.Lerp(minU, maxU, wrappedU),
                            Mathf.Lerp(minV, maxV, wrappedV));

                        var newIndex = vertices.Count;
                        vertices.Add(vertices[originalIndex]);
                        uvList.Add(remappedUv);
                        if (normalList != null) normalList.Add(normalList[originalIndex]);
                        if (tangentList != null) tangentList.Add(tangentList[originalIndex]);
                        if (colorList != null) colorList.Add(colorList[originalIndex]);
                        if (boneWeightList != null) boneWeightList.Add(boneWeightList[originalIndex]);
                        mergedTriangles.Add(newIndex);
                    }
                    continue;
                }

                newSubMeshTriangles.Add(meshCopy.GetTriangles(i));
                if (i >= 0 && i < materials.Count)
                {
                    newMaterials.Add(materials[i]);
                    if (i < materialSourceIndices.Count) newSourceIndices.Add(materialSourceIndices[i]);
                }
            }

            meshCopy.SetVertices(vertices);
            meshCopy.SetUVs(0, uvList);
            if (normalList != null) meshCopy.SetNormals(normalList);
            if (tangentList != null) meshCopy.SetTangents(tangentList);
            if (colorList != null) meshCopy.SetColors(colorList);
            if (boneWeightList != null) meshCopy.boneWeights = boneWeightList.ToArray();
            if (vertices.Count > 65535) meshCopy.indexFormat = IndexFormat.UInt32;

            meshCopy.subMeshCount = newSubMeshTriangles.Count + 1;
            for (var i = 0; i < newSubMeshTriangles.Count; i++)
            {
                meshCopy.SetTriangles(newSubMeshTriangles[i], i, false);
            }
            var mergedSubMeshIndex = newSubMeshTriangles.Count;
            meshCopy.SetTriangles(mergedTriangles.ToArray(), mergedSubMeshIndex, false);

            newMaterials.Add(mergedMaterial);
            newSourceIndices.Add(mergedRepresentativeSourceIndex);
            if (fakeShadowMaterial != null)
            {
                // Unity はサブメッシュ数より多いマテリアルを設定すると、
                // 余剰マテリアルを最後のサブメッシュに重ねて描画する。
                // merged を最後のサブメッシュに置くことで FakeShadow を同じ髪面に重ねる。
                newMaterials.Add(fakeShadowMaterial);
                newSourceIndices.Add(-1);
            }

            switch (renderer)
            {
                case SkinnedMeshRenderer skinned:
                    skinned.sharedMesh = meshCopy;
                    break;
                case MeshRenderer meshRenderer:
                    var filter = meshRenderer.GetComponent<MeshFilter>();
                    if (filter != null) filter.sharedMesh = meshCopy;
                    break;
            }

            materials.Clear();
            materials.AddRange(newMaterials);
            materialSourceIndices.Clear();
            materialSourceIndices.AddRange(newSourceIndices);
            report.Warnings.Add(new ConversionWarning("hair materials merged with atlas"));
        }

        private static Dictionary<int, int> BuildTransparentRanks(IReadOnlyList<Material> original)
        {
            var ranked = new List<(int index, int queue)>();
            for (var i = 0; i < original.Count; i++)
            {
                var material = original[i];
                if (material == null) continue;
                if (RenderTypeResolver.ResolveFromMaterial(material) != RenderType.Transparent) continue;
                ranked.Add((i, material.renderQueue));
            }

            ranked = ranked.OrderBy(p => p.queue).ThenBy(p => p.index).ToList();
            var result = new Dictionary<int, int>(ranked.Count);
            for (var i = 0; i < ranked.Count; i++)
            {
                result[ranked[i].index] = i;
            }
            return result;
        }

        private static void ReindexTransparentQueues(IReadOnlyList<Material> materials, IReadOnlyList<int> sourceIndices, IReadOnlyDictionary<int, int> transparentRanks)
        {
            for (var i = 0; i < materials.Count; i++)
            {
                var material = materials[i];
                if (material == null) continue;
                if (i >= sourceIndices.Count) continue;
                var sourceIndex = sourceIndices[i];
                if (sourceIndex < 0) continue;
                if (!transparentRanks.TryGetValue(sourceIndex, out var rank)) continue;
                material.renderQueue = 2460 + rank;
            }
        }

        private static Material CreateFakeShadowMaterial(Material mergedMaterial, bool enableFakeShadow, Vector3 fakeShadowDirection, float fakeShadowOffset, ConversionReport report)
        {
            if (!enableFakeShadow || mergedMaterial == null) return null;

            var shader = ResolveFakeShadowShader();
            if (shader == null)
            {
                report?.Warnings.Add(new ConversionWarning("FakeShadow enabled but lilToonFakeShadow shader was not found"));
                return null;
            }

            var fakeShadowMaterial = new Material(shader)
            {
                name = $"{mergedMaterial.name}_FakeShadow",
            };

            if (fakeShadowMaterial.HasProperty("_MainTex")) fakeShadowMaterial.SetTexture("_MainTex", null);

            ApplyFakeShadowOverrides(fakeShadowMaterial, true, fakeShadowDirection, fakeShadowOffset);
            return fakeShadowMaterial;
        }

        private static Shader ResolveFakeShadowShader()
        {
            var candidateNames = new[]
            {
                "_lil/[Optional] lilToonFakeShadow",
                "_lil/[Optional]lilToonFakeShadow",
                "Hidden/lilToonFakeShadow",
            };

            for (var i = 0; i < candidateNames.Length; i++)
            {
                var resolved = Shader.Find(candidateNames[i]);
                if (resolved != null) return resolved;
            }

            var guids = AssetDatabase.FindAssets("lilToonFakeShadow t:Shader");
            return guids
                .Select(AssetDatabase.GUIDToAssetPath)
                .Select(AssetDatabase.LoadAssetAtPath<Shader>)
                .FirstOrDefault(shader => shader != null && shader.name.IndexOf("liltoonfakeshadow", System.StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
