using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NdmfMToon10ToLilToon
{
    [CustomEditor(typeof(MToonLilToonComponent))]
    public sealed class MToonLilToonComponentEditor : Editor
    {
        private enum Language
        {
            Japanese,
            English
        }

        private const string PrefKeyLanguage = "MToonLilToonComponentEditor.Language";
        private Language _language;

        private void OnEnable()
        {
            _language = (Language)EditorPrefs.GetInt(PrefKeyLanguage, 0);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (MToonLilToonComponent)target;
            var previousPreviewing = MToonLilToonPreviewUtility.IsPreviewing(component);
            EditorGUI.BeginChangeCheck();

            DrawPreviewButton(component);
            DrawLilToonUserSettings();
            DrawHairMergeToggle(component);

            DrawHairSelections(component);
            DrawReport(component);

            var changed = EditorGUI.EndChangeCheck();
            serializedObject.ApplyModifiedProperties();

            if (changed && previousPreviewing)
            {
                MToonLilToonPreviewUtility.RestartPreviewIfActive(component);
            }
        }

        private void DrawPreviewButton(MToonLilToonComponent component)
        {
            using var horizontal = new EditorGUILayout.HorizontalScope();
            var previous = GUI.backgroundColor;
            var previewing = MToonLilToonPreviewUtility.IsPreviewing(component);
            GUI.backgroundColor = previewing ? new Color(0.4f, 0.85f, 0.4f) : previous;

            if (GUILayout.Button("Preview", GUILayout.Width(90f), GUILayout.Height(20f)))
            {
                MToonLilToonPreviewUtility.TogglePreview(component);
                EditorUtility.SetDirty(component);
            }

            GUI.backgroundColor = previous;
            GUILayout.FlexibleSpace();
            EditorGUI.BeginChangeCheck();
            var nextLanguage = (Language)EditorGUILayout.EnumPopup(_language, GUILayout.Width(90f));
            if (EditorGUI.EndChangeCheck())
            {
                _language = nextLanguage;
                EditorPrefs.SetInt(PrefKeyLanguage, (int)_language);
            }
        }

        private void DrawLilToonUserSettings()
        {
            var overridesProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.globalOverrides));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("lilToon ユーザー設定", "lilToon User Settings"), EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.shadowBorderColor)),
                new GUIContent(T("境界の色", "Shadow Border Color")));
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.shadowBorderStrength)),
                new GUIContent(T("境界の幅", "Shadow Border Strength")));
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.distanceFadeColor)),
                new GUIContent(T("距離フェード（色）", "Distance Fade Color")));
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.distanceFadeStrength)),
                new GUIContent(T("距離フェード（強さ）", "Distance Fade Strength")));
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.backlightColor)),
                new GUIContent(T("逆光ライト（色）", "Backlight Color")));
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.backlightStrength)),
                new GUIContent(T("逆光ライト（強さ）", "Backlight Strength")));
        }

        private void DrawHairMergeToggle(MToonLilToonComponent component)
        {
            var enableHairMergeProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableHairMerge));
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(enableHairMergeProp, new GUIContent(T("髪周りのルック調整", "Hair Look Adjustments")));
            var mergeToggleChanged = EditorGUI.EndChangeCheck();
            if (mergeToggleChanged)
            {
                if (enableHairMergeProp.boolValue)
                {
                    ScanMaterials(component);
                }
                else
                {
                    component.hairSelections = new List<HairMaterialSelection>();
                }
                EditorUtility.SetDirty(component);
            }

            if (!enableHairMergeProp.boolValue) return;

            using (new EditorGUI.IndentLevelScope())
            {
                var enableEyebrowStencilProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableEyebrowStencil));
                EditorGUILayout.PropertyField(enableEyebrowStencilProp, new GUIContent(T("眉ステンシル", "Eyebrow Stencil")));
                if (enableEyebrowStencilProp.boolValue)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        DrawEyebrowStencilMaterialSelector(component);
                    }
                }

                var enableFakeShadowProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableFakeShadow));
                EditorGUILayout.PropertyField(enableFakeShadowProp, new GUIContent("FakeShadow"));
                if (enableFakeShadowProp.boolValue)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        DrawFakeShadowFaceMaterialSelector(component);
                        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MToonLilToonComponent.fakeShadowDirection)),
                            new GUIContent(T("向き", "Direction")));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MToonLilToonComponent.fakeShadowOffset)),
                            new GUIContent(T("オフセット", "Offset")));
                    }
                }
            }
        }

        private void DrawFakeShadowFaceMaterialSelector(MToonLilToonComponent component)
        {
            var candidates = GetRendererMaterials(component);
            if (candidates.Count == 0) return;

            if (component.fakeShadowFaceMaterial == null || !candidates.Contains(component.fakeShadowFaceMaterial))
            {
                component.fakeShadowFaceMaterial = DetectDefaultFaceMaterial(candidates);
            }

            var labels = new[] { T("未設定", "None") }.Concat(candidates.Select(m => m != null ? m.name : "(null)")).ToArray();
            var currentIndex = component.fakeShadowFaceMaterial != null
                ? candidates.IndexOf(component.fakeShadowFaceMaterial) + 1
                : 0;

            var nextIndex = EditorGUILayout.Popup(T("顔マテリアル", "Face Material"), currentIndex, labels);
            component.fakeShadowFaceMaterial = nextIndex <= 0 ? null : candidates[nextIndex - 1];
        }

        private void DrawEyebrowStencilMaterialSelector(MToonLilToonComponent component)
        {
            var candidates = GetRendererMaterials(component);
            if (candidates.Count == 0) return;

            if (component.eyebrowStencilMaterial == null || !candidates.Contains(component.eyebrowStencilMaterial))
            {
                component.eyebrowStencilMaterial = DetectDefaultEyebrowMaterial(candidates);
            }

            var labels = new[] { T("未設定", "None") }.Concat(candidates.Select(m => m != null ? m.name : "(null)")).ToArray();
            var currentIndex = component.eyebrowStencilMaterial != null
                ? candidates.IndexOf(component.eyebrowStencilMaterial) + 1
                : 0;

            var nextIndex = EditorGUILayout.Popup(T("対象マテリアル", "Target Material"), currentIndex, labels);
            component.eyebrowStencilMaterial = nextIndex <= 0 ? null : candidates[nextIndex - 1];
        }

        private void DrawHairSelections(MToonLilToonComponent component)
        {
            if (!component.enableHairMerge) return;

            if (component.hairSelections == null || component.hairSelections.Count == 0)
            {
                ScanMaterials(component);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("結合対象マテリアル", "Materials to Merge"), EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(T("チェックを入れたマテリアルは結合されます。", "Checked materials will be merged."), MessageType.Info);

            if (component.hairSelections == null || component.hairSelections.Count == 0)
            {
                EditorGUILayout.HelpBox(T("まだスキャンされていません。", "No materials scanned yet."), MessageType.Info);
                return;
            }

            foreach (var entry in component.hairSelections)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    entry.selected = EditorGUILayout.Toggle(entry.selected, GUILayout.Width(20));
                    EditorGUILayout.ObjectField(entry.material, typeof(Material), false);
                }
            }
        }

        private void DrawReport(MToonLilToonComponent component)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("レポート", "Last Report"), EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"{T("scanned material count", "scanned material count")}: {component.scannedMaterialCount}");
            EditorGUILayout.LabelField($"{T("converted material count", "converted material count")}: {component.convertedMaterialCount}");
            EditorGUILayout.LabelField($"{T("skipped material count", "skipped material count")}: {component.skippedMaterialCount}");

            if (component.warnings.Count > 0)
            {
                EditorGUILayout.HelpBox(string.Join("\n", component.warnings), MessageType.Warning);
            }

            if (component.unsupportedProperties.Count > 0)
            {
                EditorGUILayout.HelpBox($"{T("unsupported property summary", "unsupported property summary")}: {string.Join(", ", component.unsupportedProperties)}", MessageType.Info);
            }
        }

        private static void ScanMaterials(MToonLilToonComponent component)
        {
            var scannedMaterials = GetRendererMaterials(component);
            if (scannedMaterials.Count == 0)
            {
                component.hairSelections = new List<HairMaterialSelection>();
                return;
            }

            component.hairSelections = HairMaterialSelector.BuildDefaultSelections(
                scannedMaterials.Where(m => m != null && MToonDetector.IsMToonLike(m)));

            if (component.fakeShadowFaceMaterial == null || !scannedMaterials.Contains(component.fakeShadowFaceMaterial))
            {
                component.fakeShadowFaceMaterial = DetectDefaultFaceMaterial(scannedMaterials);
            }

            if (component.eyebrowStencilMaterial == null || !scannedMaterials.Contains(component.eyebrowStencilMaterial))
            {
                component.eyebrowStencilMaterial = DetectDefaultEyebrowMaterial(scannedMaterials);
            }
        }

        private static List<Material> GetRendererMaterials(MToonLilToonComponent component)
        {
            return component.GetComponentsInChildren<Renderer>(true)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null)
                .Distinct()
                .ToList();
        }

        private static Material DetectDefaultFaceMaterial(IReadOnlyList<Material> materials)
        {
            if (materials == null || materials.Count == 0) return null;

            var face = materials.FirstOrDefault(m => m != null
                && m.name.IndexOf("FACE", System.StringComparison.OrdinalIgnoreCase) >= 0
                && m.name.IndexOf("SKIN", System.StringComparison.OrdinalIgnoreCase) >= 0);
            if (face != null) return face;

            face = materials.FirstOrDefault(m => m != null
                && (m.name.IndexOf("FACE", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || m.name.IndexOf("顔", System.StringComparison.OrdinalIgnoreCase) >= 0));
            if (face != null) return face;

            return materials.FirstOrDefault();
        }

        private static Material DetectDefaultEyebrowMaterial(IReadOnlyList<Material> materials)
        {
            if (materials == null || materials.Count == 0) return null;

            var eyebrow = materials.FirstOrDefault(m => m != null
                && (m.name.IndexOf("EYEBROW", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || m.name.IndexOf("BROW", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || m.name.IndexOf("眉", System.StringComparison.OrdinalIgnoreCase) >= 0));
            if (eyebrow != null) return eyebrow;

            return materials.FirstOrDefault();
        }

        private string T(string ja, string en)
        {
            return _language == Language.Japanese ? ja : en;
        }
    }
}
