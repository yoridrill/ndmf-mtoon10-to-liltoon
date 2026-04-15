using nadena.dev.ndmf;
using UnityEngine;

[assembly: ExportsPlugin(typeof(NdmfMToon10ToLilToon.MToonLilToonNdmfPlugin))]

namespace NdmfMToon10ToLilToon
{
    public sealed class MToonLilToonNdmfPlugin : Plugin
    {
        public override string QualifiedName => "jp.yoridrill.ndmf-mtoon10-to-liltoon";
        public override string DisplayName => "NDMF MToon1.0 to lilToon";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .Run("Convert MToon1.0 Materials to lilToon", Execute);
        }

        private static void Execute(BuildContext context)
        {
            var root = ResolveAvatarRoot(context);
            if (root == null) return;

            var components = root.GetComponentsInChildren<MToonLilToonComponent>(true);
            foreach (var component in components)
            {
                ApplyOnBuild(component);
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
    }
}
