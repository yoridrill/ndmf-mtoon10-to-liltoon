using System.Collections.Generic;
using UnityEngine;

namespace NdmfMToon10ToLilToon
{
    [DisallowMultipleComponent]
    [AddComponentMenu("NDMF/NDMF MToon10 to lilToon")]
    public sealed class MToonLilToonComponent : MonoBehaviour
    {
        public Shader lilToonShader;
        public bool enableHairMerge;
        public List<HairMaterialSelection> hairSelections = new();
        public bool enableFakeShadow;
        public Vector3 fakeShadowDirection = new Vector3(0f, -1f, 0f);
        public Vector2 fakeShadowOffset = Vector2.zero;
        public LilToonGlobalOverrides globalOverrides = new();
        public bool isPreviewing;

        [HideInInspector] public int scannedMaterialCount;
        [HideInInspector] public int convertedMaterialCount;
        [HideInInspector] public int skippedMaterialCount;
        [HideInInspector] public List<string> warnings = new();
        [HideInInspector] public List<string> unsupportedProperties = new();
    }
}
