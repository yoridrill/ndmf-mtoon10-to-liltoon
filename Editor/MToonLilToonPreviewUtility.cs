using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NdmfMToon10ToLilToon
{
    [InitializeOnLoad]
    internal static class MToonLilToonPreviewUtility
    {
        private const string PreviewRootName = "__MToonLilToonPreviewRoot";
        private const string PreviewAvatarName = "__MToonLilToonPreviewAvatar";

        private static GameObject _sourceAvatarRoot;
        private static GameObject _previewRoot;
        private static GameObject _previewAvatar;
        private static readonly List<RendererState> HiddenRenderers = new();

        private struct RendererState
        {
            public Renderer renderer;
            public bool wasEnabled;
        }

        static MToonLilToonPreviewUtility()
        {
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.quitting += StopPreview;
            CleanupOrphanPreviewObjects();
        }

        internal static void TogglePreview(MToonLilToonComponent component)
        {
            var avatarRoot = FindAvatarRoot(component.gameObject);
            if (avatarRoot == null) return;

            if (IsPreviewing(avatarRoot))
            {
                StopPreview();
                return;
            }

            StartPreview(avatarRoot);
        }

        internal static void RestartPreviewIfActive(MToonLilToonComponent component)
        {
            var avatarRoot = FindAvatarRoot(component.gameObject);
            if (avatarRoot == null || !IsPreviewing(avatarRoot)) return;

            StartPreview(avatarRoot);
        }

        internal static bool IsPreviewing(MToonLilToonComponent component)
        {
            var avatarRoot = FindAvatarRoot(component.gameObject);
            return avatarRoot != null && IsPreviewing(avatarRoot);
        }

        private static bool IsPreviewing(GameObject avatarRoot)
        {
            return _sourceAvatarRoot != null && _sourceAvatarRoot == avatarRoot && _previewAvatar != null;
        }

        private static void StartPreview(GameObject avatarRoot)
        {
            StopPreview();
            _sourceAvatarRoot = avatarRoot;

            _previewRoot = new GameObject(PreviewRootName)
            {
                hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor,
            };
            _previewAvatar = Object.Instantiate(avatarRoot, _previewRoot.transform);
            _previewAvatar.name = PreviewAvatarName;
            _previewAvatar.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;

            foreach (var component in _previewAvatar.GetComponentsInChildren<MToonLilToonComponent>(true))
            {
                MToonLilToonProcessor.ApplyOnBuild(component);
                component.isPreviewing = true;
            }

            HideSourceRenderers(avatarRoot);
            SyncSourcePreviewFlag(avatarRoot, true);
            SceneView.RepaintAll();
        }

        internal static void StopPreview()
        {
            RestoreSourceRenderers();

            if (_previewRoot != null)
            {
                Object.DestroyImmediate(_previewRoot);
            }

            if (_sourceAvatarRoot != null)
            {
                SyncSourcePreviewFlag(_sourceAvatarRoot, false);
            }

            _previewRoot = null;
            _previewAvatar = null;
            _sourceAvatarRoot = null;
            CleanupOrphanPreviewObjects();
            SceneView.RepaintAll();
        }

        private static void HideSourceRenderers(GameObject avatarRoot)
        {
            RestoreSourceRenderers();
            foreach (var renderer in avatarRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null) continue;
                HiddenRenderers.Add(new RendererState
                {
                    renderer = renderer,
                    wasEnabled = renderer.enabled,
                });
                renderer.enabled = false;
            }
        }

        private static void RestoreSourceRenderers()
        {
            foreach (var state in HiddenRenderers.Where(state => state.renderer != null))
            {
                state.renderer.enabled = state.wasEnabled;
            }

            HiddenRenderers.Clear();
        }

        private static void SyncSourcePreviewFlag(GameObject avatarRoot, bool previewing)
        {
            foreach (var component in avatarRoot.GetComponentsInChildren<MToonLilToonComponent>(true))
            {
                component.isPreviewing = previewing;
                EditorUtility.SetDirty(component);
            }
        }

        private static void CleanupOrphanPreviewObjects()
        {
            foreach (var go in Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (go == null) continue;
                if (go.name != PreviewRootName && go.name != PreviewAvatarName) continue;
                Object.DestroyImmediate(go);
            }
        }

        private static GameObject FindAvatarRoot(GameObject from)
        {
            var animator = from.GetComponentsInParent<Animator>(true)
                .FirstOrDefault(a => a.avatar != null && a.avatar.isHuman);
            if (animator != null) return animator.gameObject;

            var transform = from.transform;
            while (transform.parent != null)
            {
                transform = transform.parent;
            }

            return transform.gameObject;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.ExitingPlayMode)
            {
                StopPreview();
            }
        }
    }
}
