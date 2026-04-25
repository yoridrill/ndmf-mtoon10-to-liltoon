using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NdmfMToon10ToLilToon
{
    [CustomEditor(typeof(MToonLilToonComponent))]
    public sealed class MToonLilToonComponentEditor : Editor
    {
        private const float OverrideGroupSpacing = 4f;
        private const float SectionHeadingSpacing = 8f;
        private const float ToggleOnlyWidth = 18f;
        private const float ToggleWithSpacingWidth = 22f;
        private const float HairSelectionToggleColumnWidth = 26f;

        private List<Material> _cachedRendererMaterials;

        private enum Language
        {
            Japanese,
            English
        }

        private const string PrefKeyLanguage = "MToonLilToonComponentEditor.Language";
        private const string DefaultFaceShadowMaskTextureGuid = "68acac0df33c74d6ba68772c4685986f";
        private Language _language;

        private void OnEnable()
        {
            _language = (Language)EditorPrefs.GetInt(PrefKeyLanguage, 0);
            var component = (MToonLilToonComponent)target;
            _cachedRendererMaterials = GetRendererMaterials(component);
            if (ShouldAutoScanHairSelectionsOnEnable(component, _cachedRendererMaterials))
            {
                ScanMaterials(component);
                _cachedRendererMaterials = GetRendererMaterials(component);
                EditorUtility.SetDirty(component);
            }
            if (EnsureFaceMaterialsDetected(component))
            {
                EditorUtility.SetDirty(component);
            }
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var component = (MToonLilToonComponent)target;
            var previousPreviewing = MToonLilToonPreviewUtility.IsPreviewing(component);
            _cachedRendererMaterials ??= GetRendererMaterials(component);

            DrawPreviewButton(component);
            EditorGUILayout.Space(4f);
            var sharedFaceMaterialChanged = DrawSharedFaceMaterialSelector(component);
            var globalOverridesChanged = DrawLilToonUserSettings();
            DrawSpecificPartAdjustmentsHeading();

            EditorGUI.BeginChangeCheck();
            var directValueChanged = sharedFaceMaterialChanged | DrawHairMergeToggle(component, out var requestHairScan);
            var hairSettingsChanged = EditorGUI.EndChangeCheck();

            directValueChanged |= DrawHairSelections(component);

            EditorGUI.BeginChangeCheck();
            directValueChanged |= DrawFaceShadowTuningSection(component);
            var faceShadowSettingsChanged = EditorGUI.EndChangeCheck();

            EditorGUI.BeginChangeCheck();
            directValueChanged |= DrawAdvancedSection(component);
            var advancedSettingsChanged = EditorGUI.EndChangeCheck();

            var serializedChanged = serializedObject.ApplyModifiedProperties();
            if (requestHairScan)
            {
                ScanMaterials(component);
                _cachedRendererMaterials = GetRendererMaterials(component);
                EditorUtility.SetDirty(component);
                directValueChanged = true;
            }
            if (directValueChanged)
            {
                EditorUtility.SetDirty(component);
            }

            var onlyGlobalOverridesChanged = previousPreviewing
                && serializedChanged
                && !directValueChanged
                && globalOverridesChanged
                && !hairSettingsChanged
                && !faceShadowSettingsChanged
                && !advancedSettingsChanged;

            if (onlyGlobalOverridesChanged)
            {
                MToonLilToonPreviewUtility.ApplyGlobalOverridesIfActive(component);
            }
            else if ((serializedChanged || directValueChanged) && previousPreviewing)
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
            var progressMessage = MToonLilToonPreviewUtility.IsProcessingPreview()
                ? "Processing..."
                : MToonLilToonPreviewUtility.GetPreviewProgressMessage();
            if (!string.IsNullOrEmpty(progressMessage))
            {
                EditorGUILayout.LabelField(progressMessage, EditorStyles.miniLabel);
            }
            GUILayout.FlexibleSpace();
            EditorGUI.BeginChangeCheck();
            var nextLanguage = (Language)EditorGUILayout.EnumPopup(_language, GUILayout.Width(90f));
            if (EditorGUI.EndChangeCheck())
            {
                _language = nextLanguage;
                EditorPrefs.SetInt(PrefKeyLanguage, (int)_language);
            }
        }

        private bool DrawLilToonUserSettings()
        {
            EditorGUI.BeginChangeCheck();
            var overridesProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.globalOverrides));
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("lilToon固有機能の一括設定", "Bulk Settings for lilToon-specific Features"), EditorStyles.boldLabel);
            DrawOverrideGroup(
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.enableShadowReceive)),
                T("影を受け取る", "Receive Shadow"),
                T("強度", "Strength"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.shadowReceive)),
                T("顔だけ除外する", "Exclude Face Only"),
                serializedObject.FindProperty(nameof(MToonLilToonComponent.disableShadowReceiveForFace)));
            DrawOverrideGroup(
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.enableShadowBorder)),
                T("影の境界", "Shadow Border"),
                T("色", "Color"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.shadowBorderColor)),
                T("幅", "Width"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.shadowBorderStrength)));
            DrawOverrideGroupWithThirdRow(
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.enableBacklight)),
                T("逆光ライト", "Backlight"),
                T("色", "Color"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.backlightColor)),
                T("メインカラーの強度", "Main Color Strength"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.backlightMainStrength)),
                T("顔だけ除外する", "Exclude Face Only"),
                serializedObject.FindProperty(nameof(MToonLilToonComponent.disableBacklightStrengthForFace)));
            DrawOverrideGroup(
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.enableDistanceFade)),
                T("距離フェード", "Distance Fade"),
                T("色", "Color"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.distanceFadeColor)),
                T("強度", "Strength"),
                overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.distanceFadeStrength)));
            EditorGUILayout.PropertyField(overridesProp.FindPropertyRelative(nameof(LilToonGlobalOverrides.outlineZBias)),
                new GUIContent(T("輪郭線のZ Bias", "Outline Z Bias")));
            return EditorGUI.EndChangeCheck();
        }

        private void DrawSpecificPartAdjustmentsHeading()
        {
            EditorGUILayout.Space(SectionHeadingSpacing);
            EditorGUILayout.LabelField(T("特定部位への調整", "Adjustments for Specific Parts"), EditorStyles.boldLabel);
            EditorGUILayout.Space(2f);
        }

        private void DrawOverrideGroup(
            SerializedProperty enabledProp,
            string groupLabel,
            string firstLabel,
            SerializedProperty firstValueProp,
            string secondLabel,
            SerializedProperty secondValueProp)
        {
            DrawOverrideGroup(enabledProp, groupLabel, firstLabel, firstValueProp, secondLabel, secondValueProp, addBottomSpacing: true);
        }

        private void DrawOverrideGroupWithThirdRow(
            SerializedProperty enabledProp,
            string groupLabel,
            string firstLabel,
            SerializedProperty firstValueProp,
            string secondLabel,
            SerializedProperty secondValueProp,
            string thirdLabel,
            SerializedProperty thirdValueProp)
        {
            DrawOverrideGroup(enabledProp, groupLabel, firstLabel, firstValueProp, secondLabel, secondValueProp, addBottomSpacing: false);

            var thirdRowRect = EditorGUILayout.GetControlRect();
            GetOverrideColumnRects(thirdRowRect, out var thirdCategoryRect, out var thirdItemLabelRect, out var thirdValueRect);
            DrawCategoryColumn(thirdCategoryRect, enabledProp, string.Empty, showToggle: false);
            using (new EditorGUI.DisabledScope(!enabledProp.boolValue))
            {
                DrawTwoColumnPropertyRow(thirdItemLabelRect, thirdValueRect, thirdLabel, thirdValueProp);
            }

            EditorGUILayout.Space(OverrideGroupSpacing);
        }

        private void DrawOverrideGroup(
            SerializedProperty enabledProp,
            string groupLabel,
            string firstLabel,
            SerializedProperty firstValueProp,
            string secondLabel,
            SerializedProperty secondValueProp,
            bool addBottomSpacing)
        {
            var firstRowRect = EditorGUILayout.GetControlRect();
            GetOverrideColumnRects(firstRowRect, out var firstCategoryRect, out var firstItemLabelRect, out var firstValueRect);
            DrawCategoryColumn(firstCategoryRect, enabledProp, groupLabel, showToggle: true);
            using (new EditorGUI.DisabledScope(!enabledProp.boolValue))
            {
                DrawTwoColumnPropertyRow(firstItemLabelRect, firstValueRect, firstLabel, firstValueProp);
            }

            var secondRowRect = EditorGUILayout.GetControlRect();
            GetOverrideColumnRects(secondRowRect, out var secondCategoryRect, out var secondItemLabelRect, out var secondValueRect);
            DrawCategoryColumn(secondCategoryRect, enabledProp, string.Empty, showToggle: false);
            using (new EditorGUI.DisabledScope(!enabledProp.boolValue))
            {
                DrawTwoColumnPropertyRow(secondItemLabelRect, secondValueRect, secondLabel, secondValueProp);
            }

            if (addBottomSpacing)
            {
                EditorGUILayout.Space(OverrideGroupSpacing);
            }
        }

        private static void DrawCategoryColumn(Rect categoryRect, SerializedProperty enabledProp, string label, bool showToggle)
        {
            var toggleRect = new Rect(categoryRect.x, categoryRect.y, ToggleOnlyWidth, categoryRect.height);
            var labelRect = new Rect(
                categoryRect.x + ToggleWithSpacingWidth,
                categoryRect.y,
                Mathf.Max(0f, categoryRect.width - ToggleWithSpacingWidth),
                categoryRect.height);

            if (showToggle)
            {
                enabledProp.boolValue = EditorGUI.Toggle(toggleRect, enabledProp.boolValue);
            }
            EditorGUI.LabelField(labelRect, label);
        }

        private static void DrawTwoColumnPropertyRow(Rect itemLabelRect, Rect valueRect, string label, SerializedProperty valueProp)
        {
            EditorGUI.LabelField(itemLabelRect, label);
            EditorGUI.PropertyField(valueRect, valueProp, GUIContent.none);
        }

        private static void GetOverrideColumnRects(
            Rect rowRect,
            out Rect categoryRect,
            out Rect itemLabelRect,
            out Rect valueRect)
        {
            var unit = rowRect.width / 7f;
            categoryRect = new Rect(rowRect.x, rowRect.y, unit * 2f, rowRect.height);
            itemLabelRect = new Rect(categoryRect.xMax, rowRect.y, unit * 2f, rowRect.height);
            valueRect = new Rect(itemLabelRect.xMax, rowRect.y, unit * 3f, rowRect.height);
        }

        private bool DrawHairMergeToggle(MToonLilToonComponent component, out bool requestHairScan)
        {
            requestHairScan = false;
            var changed = false;
            var enableHairMergeProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableHairMerge));
            EditorGUI.BeginChangeCheck();
            DrawLeftToggle(enableHairMergeProp, T("髪周りのルック調整", "Hair Look Adjustments"));
            var mergeToggleChanged = EditorGUI.EndChangeCheck();
            if (mergeToggleChanged)
            {
                changed = true;
                if (enableHairMergeProp.boolValue)
                {
                    requestHairScan = true;
                }
                else
                {
                    component.hairSelections = new List<HairMaterialSelection>();
                }
                EditorUtility.SetDirty(component);
            }

            if (!enableHairMergeProp.boolValue) return changed;

            using (new EditorGUI.IndentLevelScope())
            {
                var enableEyebrowStencilProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableEyebrowStencil));
                EditorGUILayout.PropertyField(enableEyebrowStencilProp, new GUIContent(T("眉ステンシル", "Eyebrow Stencil")));
                if (enableEyebrowStencilProp.boolValue)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        changed |= DrawEyebrowStencilMaterialSelector(component);
                    }
                }

                var enableFakeShadowProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableFakeShadow));
                EditorGUILayout.PropertyField(enableFakeShadowProp, new GUIContent("FakeShadow"));
                if (enableFakeShadowProp.boolValue)
                {
                    using (new EditorGUI.IndentLevelScope())
                    {
                        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MToonLilToonComponent.fakeShadowDirection)),
                            new GUIContent(T("向き", "Direction")));
                        EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(MToonLilToonComponent.fakeShadowOffset)),
                            new GUIContent(T("オフセット", "Offset")));
                    }
                }

            }

            return changed;
        }

        private bool DrawFaceShadowTuningSection(MToonLilToonComponent component)
        {
            var changed = false;
            EditorGUILayout.Space();
            var enableFaceShadowTuningProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.enableFaceShadowTuning));
            DrawLeftToggle(enableFaceShadowTuningProp, T("顔の影を整える", "Tune Face Shadow"));
            if (!enableFaceShadowTuningProp.boolValue) return changed;

            using (new EditorGUI.IndentLevelScope())
            {
                DrawFaceShadowMaskSettings(component);
            }

            return changed;
        }

        private void DrawFaceShadowMaskSettings(MToonLilToonComponent component)
        {
            if (component.faceShadowSdfTexture == null)
            {
                component.faceShadowSdfTexture = LoadDefaultFaceShadowMaskTexture();
            }

            DrawFaceShadowMaskTypePopup();

            var textureProperty = serializedObject.FindProperty(nameof(MToonLilToonComponent.faceShadowSdfTexture));
            EditorGUILayout.PropertyField(textureProperty, new GUIContent(T("マスク", "Mask")));
            var lodProperty = serializedObject.FindProperty(nameof(MToonLilToonComponent.shadowStrengthMaskLod));
            lodProperty.floatValue = EditorGUILayout.Slider(new GUIContent(T("LOD", "LOD")), lodProperty.floatValue, 0f, 1f);
        }

        private void DrawFaceShadowMaskTypePopup()
        {
            var maskTypeProperty = serializedObject.FindProperty(nameof(MToonLilToonComponent.faceShadowMaskType));
            if (maskTypeProperty == null) return;

            var options = new[]
            {
                T("強度", "Strength"),
                T("平面化", "Flat"),
                "SDF"
            };

            var currentType = (MToonLilToonComponent.FaceShadowMaskType)maskTypeProperty.intValue;
            var currentIndex = currentType switch
            {
                MToonLilToonComponent.FaceShadowMaskType.Strength => 0,
                MToonLilToonComponent.FaceShadowMaskType.Flat => 1,
                MToonLilToonComponent.FaceShadowMaskType.Sdf => 2,
                _ => 1
            };

            var nextIndex = EditorGUILayout.Popup(T("マスクタイプ", "Mask Type"), currentIndex, options);
            var nextType = nextIndex switch
            {
                0 => MToonLilToonComponent.FaceShadowMaskType.Strength,
                1 => MToonLilToonComponent.FaceShadowMaskType.Flat,
                2 => MToonLilToonComponent.FaceShadowMaskType.Sdf,
                _ => MToonLilToonComponent.FaceShadowMaskType.Flat
            };

            maskTypeProperty.intValue = (int)nextType;
        }

        private bool DrawEyebrowStencilMaterialSelector(MToonLilToonComponent component)
        {
            var candidates = _cachedRendererMaterials ?? GetRendererMaterials(component);
            if (candidates.Count == 0) return false;

            if (component.eyebrowStencilMaterial == null || !candidates.Contains(component.eyebrowStencilMaterial))
            {
                component.eyebrowStencilMaterial = DetectDefaultEyebrowMaterial(candidates);
            }

            var labels = new[] { T("未設定", "None") }.Concat(candidates.Select(m => m != null ? m.name : "(null)")).ToArray();
            var currentIndex = component.eyebrowStencilMaterial != null
                ? candidates.IndexOf(component.eyebrowStencilMaterial) + 1
                : 0;

            var nextIndex = EditorGUILayout.Popup(T("眉マテリアル", "Eyebrow Material"), currentIndex, labels);
            var nextMaterial = nextIndex <= 0 ? null : candidates[nextIndex - 1];
            if (nextMaterial == component.eyebrowStencilMaterial) return false;

            component.eyebrowStencilMaterial = nextMaterial;
            return true;
        }

        private bool DrawHairSelections(MToonLilToonComponent component)
        {
            if (!component.enableHairMerge) return false;

            var hairSelectionsProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.hairSelections));
            var changed = false;
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.HelpBox(
                    T(
                        "この機能が有効な場合は髪マテリアルを結合します。\n結合されたくないマテリアルは対象から外してください。",
                        "When this feature is enabled, hair materials are merged.\nExclude any materials you do not want to merge."),
                    MessageType.Info);
                component.showHairMaterials = EditorGUILayout.Foldout(
                    component.showHairMaterials,
                    T("対象マテリアル", "Target Materials"),
                    true);
                if (!component.showHairMaterials) return false;

                if (hairSelectionsProp == null || hairSelectionsProp.arraySize == 0)
                {
                    EditorGUILayout.HelpBox(T("まだスキャンされていません。", "No materials scanned yet."), MessageType.Info);
                    return false;
                }

                for (var i = 0; i < hairSelectionsProp.arraySize; i++)
                {
                    var entryProp = hairSelectionsProp.GetArrayElementAtIndex(i);
                    if (entryProp == null) continue;
                    var materialProp = entryProp.FindPropertyRelative(nameof(HairMaterialSelection.material));
                    var selectedProp = entryProp.FindPropertyRelative(nameof(HairMaterialSelection.selected));
                    if (selectedProp == null || materialProp == null) continue;

                    var rowRect = EditorGUILayout.GetControlRect();
                    var toggleRect = new Rect(rowRect.x, rowRect.y, HairSelectionToggleColumnWidth, rowRect.height);
                    var materialRect = new Rect(
                        rowRect.x + HairSelectionToggleColumnWidth,
                        rowRect.y,
                        Mathf.Max(0f, rowRect.width - HairSelectionToggleColumnWidth),
                        rowRect.height);

                    var nextSelected = EditorGUI.Toggle(toggleRect, selectedProp.boolValue);
                    if (nextSelected != selectedProp.boolValue)
                    {
                        selectedProp.boolValue = nextSelected;
                        changed = true;
                    }

                    EditorGUI.ObjectField(materialRect, materialProp, typeof(Material), GUIContent.none);
                }
            }

            return changed;
        }

        private bool DrawAdvancedSection(MToonLilToonComponent component)
        {
            var changed = false;
            EditorGUILayout.Space();

            var showAdvancedProp = serializedObject.FindProperty(nameof(MToonLilToonComponent.showAdvanced));
            showAdvancedProp.boolValue = EditorGUILayout.Foldout(showAdvancedProp.boolValue, "Advanced", true);
            if (!showAdvancedProp.boolValue) return changed;

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.PropertyField(
                    serializedObject.FindProperty(nameof(MToonLilToonComponent.verboseLog)),
                    new GUIContent("Verbose Log"));
                EditorGUILayout.Space(4f);

                var rawButtonRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                var buttonRect = EditorGUI.IndentedRect(rawButtonRect);
                if (GUI.Button(buttonRect, "Reset Preview"))
                {
                    MToonLilToonPreviewUtility.ResetSavedPreviewState(component);
                    EditorUtility.SetDirty(component);
                    changed = true;
                }

                EditorGUILayout.HelpBox(
                    T(
                        "モデルが重複したり、見えない場合に押してください。\nPreview オブジェクトを削除し、Renderer を再表示します。",
                        "Use this if the avatar stays hidden, frozen, or stuck after Preview.\nThis removes temporary Preview objects and re-enables renderers."),
                    MessageType.Warning);
            }

            return changed;
        }

        private static bool HasExternalHairSelectionReference(MToonLilToonComponent component, IReadOnlyCollection<Material> scannedMaterials)
        {
            if (component == null || component.hairSelections == null || component.hairSelections.Count == 0) return false;
            if (scannedMaterials == null || scannedMaterials.Count == 0) return true;

            for (var i = 0; i < component.hairSelections.Count; i++)
            {
                var selection = component.hairSelections[i];
                if (selection == null || selection.material == null) continue;
                if (!scannedMaterials.Contains(selection.material)) return true;
            }

            return false;
        }

        private static bool ShouldAutoScanHairSelectionsOnEnable(MToonLilToonComponent component, IReadOnlyCollection<Material> scannedMaterials)
        {
            if (component == null || !component.enableHairMerge) return false;
            if (component.hairSelections == null || component.hairSelections.Count == 0) return true;
            return HasExternalHairSelectionReference(component, scannedMaterials);
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

            EnsureFaceMaterialsDetected(component, scannedMaterials);

            if (component.faceShadowSdfTexture == null)
            {
                component.faceShadowSdfTexture = LoadDefaultFaceShadowMaskTexture();
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

        private static Texture2D LoadDefaultFaceShadowMaskTexture()
        {
            var texturePath = AssetDatabase.GUIDToAssetPath(DefaultFaceShadowMaskTextureGuid);
            var texture = !string.IsNullOrEmpty(texturePath)
                ? AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath)
                : null;
            if (texture != null) return texture;

            var guids = AssetDatabase.FindAssets("VRoidFaceShadowFlat t:Texture2D");
            for (var i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                if (texture != null) return texture;
            }

            return null;
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

        private bool DrawSharedFaceMaterialSelector(MToonLilToonComponent component)
        {
            var candidates = _cachedRendererMaterials ?? GetRendererMaterials(component);
            if (candidates.Count == 0) return false;

            var labels = new[] { T("未設定", "None") }.Concat(candidates.Select(m => m != null ? m.name : "(null)")).ToArray();
            var currentFaceMaterial = component.faceShadowFaceMaterial;
            var currentIndex = currentFaceMaterial != null ? candidates.IndexOf(currentFaceMaterial) + 1 : 0;

            EditorGUI.BeginChangeCheck();
            var nextIndex = EditorGUILayout.Popup(T("顔マテリアル", "Face Material"), currentIndex, labels);
            if (!EditorGUI.EndChangeCheck()) return false;

            var nextMaterial = nextIndex <= 0 ? null : candidates[nextIndex - 1];
            component.faceShadowFaceMaterial = nextMaterial;
            component.fakeShadowFaceMaterial = nextMaterial;
            EditorUtility.SetDirty(component);
            return true;
        }

        private static bool EnsureFaceMaterialsDetected(MToonLilToonComponent component)
        {
            return EnsureFaceMaterialsDetected(component, GetRendererMaterials(component));
        }

        private static bool EnsureFaceMaterialsDetected(MToonLilToonComponent component, IReadOnlyList<Material> scannedMaterials)
        {
            if (component == null || scannedMaterials == null || scannedMaterials.Count == 0) return false;

            var changed = false;
            var defaultFaceMaterial = DetectDefaultFaceMaterial(scannedMaterials);
            if (component.faceShadowFaceMaterial == null || !scannedMaterials.Contains(component.faceShadowFaceMaterial))
            {
                component.faceShadowFaceMaterial = defaultFaceMaterial;
                changed = true;
            }

            if (component.fakeShadowFaceMaterial == null || !scannedMaterials.Contains(component.fakeShadowFaceMaterial))
            {
                component.fakeShadowFaceMaterial = component.faceShadowFaceMaterial;
                changed = true;
            }

            return changed;
        }

        private static void DrawLeftToggle(SerializedProperty boolProperty, string label)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                boolProperty.boolValue = EditorGUILayout.Toggle(boolProperty.boolValue, GUILayout.Width(18f));
                EditorGUILayout.LabelField(label);
            }
        }
    }
}
