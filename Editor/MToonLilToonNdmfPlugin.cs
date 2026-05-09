using System.Linq;
using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(NdmfMToon10ToLilToon.MToonLilToonNdmfPlugin))]

namespace NdmfMToon10ToLilToon
{
    public sealed class MToonLilToonNdmfPlugin : Plugin<MToonLilToonNdmfPlugin>
    {
        public override string QualifiedName => "jp.yoridrill.ndmf-mtoon10-to-liltoon";
        public override string DisplayName => "NDMF MToon10 to lilToon";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("jp.yoridrill.ndmf-vroid-arm-patch")
                .AfterPlugin("jp.yoridrill.ndmf-vroid-mesh-trimmer")
                .BeforePlugin("com.github.kurotu.vrc-quest-tools")
                .Run("Convert MToon10 Materials to lilToon", Execute);
        }

        private static void Execute(BuildContext context)
        {
            var root = ResolveAvatarRoot(context);
            if (root == null) return;

            var components = root.GetComponentsInChildren<MToonLilToonComponent>(true);
            try
            {
                if (components.Any(c => c != null && c.isPreviewing))
                {
                    MToonLilToonPreviewUtility.StopPreview();
                    foreach (var component in components.Where(c => c != null))
                    {
                        component.isPreviewing = false;
                    }
                }

                foreach (var component in components)
                {
                    ApplyOnBuild(component);
                }
            }
            finally
            {
                RemoveComponents(components);
            }
        }

        private static GameObject ResolveAvatarRoot(BuildContext context)
        {
            var contextType = context.GetType();

            var avatarRootObject = contextType.GetProperty("AvatarRootObject")?.GetValue(context) as GameObject;
            if (avatarRootObject != null) return avatarRootObject;

            var avatarRootTransform = contextType.GetProperty("AvatarRootTransform")?.GetValue(context) as Transform;
            return avatarRootTransform != null ? avatarRootTransform.gameObject : null;
        }

        private static void ApplyOnBuild(MToonLilToonComponent component)
        {
            MToonLilToonProcessor.ApplyOnBuild(component);
        }

        private static void RemoveComponents(MToonLilToonComponent[] components)
        {
            if (components == null) return;
            for (var i = 0; i < components.Length; i++)
            {
                if (components[i] == null) continue;
                Object.DestroyImmediate(components[i]);
            }
        }
    }
}
