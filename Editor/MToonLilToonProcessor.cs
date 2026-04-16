using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NdmfMToon10ToLilToon
{
    internal static class MToonLilToonProcessor
    {
        internal static void ApplyOnBuild(MToonLilToonComponent component)
        {
            if (component == null) return;

            var report = new ConversionReport();
            var lilToonShader = ResolveLilToonShader(component);
            if (lilToonShader == null)
            {
                component.warnings = new List<string> { "lilToon shader was not found in this project. Conversion skipped." };
                component.scannedMaterialCount = 0;
                component.convertedMaterialCount = 0;
                component.skippedMaterialCount = 0;
                component.unsupportedProperties = new List<string>();
                return;
            }

            var selectedForMerge = component.enableHairMerge
                ? component.hairSelections.Where(s => s.selected && s.material != null).Select(s => s.material).ToHashSet()
                : new HashSet<Material>();
            foreach (var renderer in component.GetComponentsInChildren<Renderer>(true))
            {
                ProcessRenderer(renderer, selectedForMerge, lilToonShader, component.globalOverrides, report);
            }

            component.scannedMaterialCount = report.ScannedMaterialCount;
            component.convertedMaterialCount = report.ConvertedMaterialCount;
            component.skippedMaterialCount = report.SkippedMaterialCount;
            component.warnings = report.Warnings.Select(w => w.Message).ToList();
            component.unsupportedProperties = report.UnsupportedPropertySummary.Select(kv => $"{kv.Key}:{kv.Value}").ToList();
        }

        private static void ProcessRenderer(Renderer renderer, HashSet<Material> selectedForMerge, Shader lilToonShader, LilToonGlobalOverrides globalOverrides, ConversionReport report)
        {
            if (renderer == null) return;

            var original = renderer.sharedMaterials;
            var result = new List<Material>(original.Length);
            var resultSourceIndices = new List<int>(original.Length);
            var transparentRanks = BuildTransparentRanks(original);
            report.ScannedMaterialCount += original.Length;

            RenderType? mergeType = selectedForMerge.Count > 0
                ? RenderTypeResolver.ResolveMergeType(selectedForMerge)
                : null;
            var mergedMaterialCreated = false;
            Material mergedMaterial = null;
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

                var mergeExcluded = mergeType.HasValue
                    && selectedForMerge.Contains(source)
                    && RenderTypeResolver.ResolveFromMaterial(source) != mergeType.Value;
                if (mergeExcluded)
                {
                    report.Warnings.Add(new ConversionWarning($"{source.name}: skipped hair merge due to render type mismatch"));
                }

                var canMerge = !mergeExcluded && mergeType.HasValue && selectedForMerge.Contains(source);
                if (MToonToLilToonMapper.TryConvert(source, lilToonShader, globalOverrides, out var converted, report))
                {
                    result.Add(converted);
                    report.ConvertedMaterialCount++;
                    if (canMerge)
                    {
                        mergedIndices.Add(i);
                    }
                    resultSourceIndices.Add(i);
                }
                else
                {
                    result.Add(source);
                    report.SkippedMaterialCount++;
                    report.Warnings.Add(new ConversionWarning($"{source.name}: skipped (not convertible)"));
                    resultSourceIndices.Add(i);
                }
            }

            if (mergedIndices.Count >= 2
                && TryMergeHairMaterials(original, mergedIndices, lilToonShader, globalOverrides, report, out mergedMaterial, out mergedRects))
            {
                mergedMaterialCreated = true;
                var mergedRepresentativeIndex = mergedIndices
                    .OrderBy(i => transparentRanks.TryGetValue(i, out var rank) ? rank : int.MaxValue)
                    .ThenBy(i => i)
                    .First();
                ApplyMergedMaterialAndMesh(renderer, result, resultSourceIndices, mergedIndices, mergedRepresentativeIndex, mergedMaterial, mergedRects, report);
            }

            if (!mergedMaterialCreated && mergedIndices.Count >= 2)
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
            Shader lilToonShader,
            LilToonGlobalOverrides overrides,
            ConversionReport report,
            out Material mergedMaterial,
            out List<Rect> atlasRects)
        {
            mergedMaterial = null;
            atlasRects = null;

            if (!MToonToLilToonMapper.TryConvert(original[mergedIndices[0]], lilToonShader, overrides, out mergedMaterial, report))
            {
                return false;
            }

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

            var fallback = atlasTextures.FirstOrDefault(t => t != null) ?? NewSolidTexture(Color.white);
            var atlasMaxSize = ResolveAtlasMaxSize(atlasTextures, mergedIndices.Count);
            var packTextures = PrepareBaseAtlasTextures(atlasTextures, fallback, atlasMaxSize, mergedIndices.Count);
            var atlas = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            atlasRects = atlas.PackTextures(packTextures, 2, atlasMaxSize, false).ToList();
            mergedMaterial.SetTexture("_MainTex", atlas);
            if (mergedMaterial.HasProperty("_MainTex"))
            {
                mergedMaterial.SetTextureScale("_MainTex", Vector2.one);
                mergedMaterial.SetTextureOffset("_MainTex", Vector2.zero);
            }

            BakeOptionalAtlas(new[] { "_ShadowColorTex", "_Shadow1stColorTex" }, original, mergedIndices, mergedMaterial, new[] { "_ShadeMap", "_ShadeMultiplyTexture" }, atlas.width, atlas.height, atlasRects);
            BakeOptionalAtlas(new[] { "_EmissionMap" }, original, mergedIndices, mergedMaterial, new[] { "_EmissiveMap", "_EmissionMap" }, atlas.width, atlas.height, atlasRects);
            BakeOptionalAtlas(new[] { "_BumpMap" }, original, mergedIndices, mergedMaterial, new[] { "_NormalMap", "_BumpMap" }, atlas.width, atlas.height, atlasRects);
            BakeOptionalAtlas(new[] { "_OutlineMask", "_OutlineTex" }, original, mergedIndices, mergedMaterial, new[] { "_OutlineWidthMultiplyTexture", "_OutlineMask" }, atlas.width, atlas.height, atlasRects);

            return true;
        }

        private static Texture2D[] PrepareBaseAtlasTextures(IReadOnlyList<Texture2D> sourceTextures, Texture2D fallback, int atlasSize, int textureCount)
        {
            var prepared = sourceTextures.Select(t => t ?? fallback).ToArray();

            var maxWidth = prepared.Max(t => t.width);
            var maxHeight = prepared.Max(t => t.height);
            const int padding = 2;
            var columns = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(textureCount)));
            var rows = Mathf.Max(1, Mathf.CeilToInt(textureCount / (float)columns));

            var scaleX = (atlasSize - (columns - 1) * padding) / (float)(columns * maxWidth);
            var scaleY = (atlasSize - (rows - 1) * padding) / (float)(rows * maxHeight);
            var scale = Mathf.Min(scaleX, scaleY, 1f);
            if (scale >= 0.999f) return prepared;

            var resized = new Texture2D[prepared.Length];
            for (var i = 0; i < prepared.Length; i++)
            {
                resized[i] = ResizeTexture(prepared[i], Mathf.Max(1, Mathf.RoundToInt(prepared[i].width * scale)), Mathf.Max(1, Mathf.RoundToInt(prepared[i].height * scale)));
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
            var fallback = textures.FirstOrDefault(t => t != null) ?? NewSolidTexture(fallbackColor);
            var atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
            if (textureCount <= 8)
            {
                required = Mathf.Min(required, 2048);
            }
            atlas.SetPixels(Enumerable.Repeat(fallbackColor, atlasWidth * atlasHeight).ToArray());
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

        private static int ResolveAtlasMaxSize(IReadOnlyList<Texture2D> textures, int textureCount)
        {
            if (textureCount >= 22) return 4096;

            var maxWidth = 1;
            var maxHeight = 1;
            for (var i = 0; i < textures.Count; i++)
            {
                var texture = textures[i];
                if (texture == null) continue;
                if (texture.width > maxWidth) maxWidth = texture.width;
                if (texture.height > maxHeight) maxHeight = texture.height;
            }

            var width = maxWidth * 7;
            var height = maxHeight * 3;
            var required = Mathf.NextPowerOfTwo(Mathf.Max(width, height));
            return Mathf.Clamp(required, 1024, 16384);
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
                    colors[y * width + x] = readable.GetPixelBilinear(tu, tv);
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

        private static void ApplyMergedMaterialAndMesh(Renderer renderer, List<Material> materials, List<int> materialSourceIndices, IReadOnlyList<int> mergedIndices, int mergedRepresentativeSourceIndex, Material mergedMaterial, IReadOnlyList<Rect> rects, ConversionReport report)
        {
            var mergedIndexSet = mergedIndices.ToHashSet();
            var newMaterials = new List<Material> { mergedMaterial };
            var newSourceIndices = new List<int> { mergedRepresentativeSourceIndex };

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
            const float paddingPixels = 2f;
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
                        var wrappedU = Mathf.Repeat(src.x, 1f);
                        var wrappedV = Mathf.Repeat(src.y, 1f);
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

            var firstOutputIndex = 0;
            meshCopy.subMeshCount = newSubMeshTriangles.Count + 1;
            meshCopy.SetTriangles(mergedTriangles.ToArray(), firstOutputIndex, false);
            for (var i = 0; i < newSubMeshTriangles.Count; i++)
            {
                meshCopy.SetTriangles(newSubMeshTriangles[i], i + 1, false);
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
                if (!transparentRanks.TryGetValue(sourceIndex, out var rank)) continue;
                material.renderQueue = 2460 + rank;
            }
        }
    }
}
