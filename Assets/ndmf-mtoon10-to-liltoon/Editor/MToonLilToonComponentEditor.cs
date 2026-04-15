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
            DrawMainSettings();
            DrawLilToonUserSettings();

            if (GUILayout.Button(T("マテリアルをスキャン", "Scan Materials")))
            {
                ScanMaterials(component);
                EditorUtility.SetDirty(component);
            }

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

        private void DrawMainSettings()
        {
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty(nameof(MToonLilToonComponent.lilToonShader)),
                new GUIContent(T("lilToon Shader", "lilToon Shader")));
            EditorGUILayout.PropertyField(
                serializedObject.FindProperty(nameof(MToonLilToonComponent.enableHairMerge)),
                new GUIContent(T("Hair Merge", "Hair Merge")));
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

        private void DrawHairSelections(MToonLilToonComponent component)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(T("Hair Merge Targets", "Hair Merge Targets"), EditorStyles.boldLabel);

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
            var renderer = component.GetComponent<Renderer>();
            if (renderer == null)
            {
                component.hairSelections = new List<HairMaterialSelection>();
                return;
            }

            component.hairSelections = HairMaterialSelector.BuildDefaultSelections(renderer.sharedMaterials.Where(m => m != null));
        }

        private string T(string ja, string en)
        {
            return _language == Language.Japanese ? ja : en;
        }
    }
}
