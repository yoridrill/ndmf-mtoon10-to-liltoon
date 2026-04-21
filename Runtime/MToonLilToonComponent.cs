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
        public bool enableEyebrowStencil;
        public Material eyebrowStencilMaterial;
        public Material fakeShadowFaceMaterial;
        public bool enableFakeShadow;
        public Vector3 fakeShadowDirection = new Vector3(0.5f, 1f, 0f);
        public float fakeShadowOffset = 0.003f;
        public bool enableFaceShadowTuning;
        public Material faceShadowFaceMaterial;
        public Texture2D faceShadowSdfTexture;
        public LilToonGlobalOverrides globalOverrides = new();
        public bool verboseLog;
        [HideInInspector] public bool showAdvanced;
        public bool isPreviewing;

        [HideInInspector] public int scannedMaterialCount;
        [HideInInspector] public int convertedMaterialCount;
        [HideInInspector] public int skippedMaterialCount;
        [HideInInspector] public List<string> warnings = new();
        [HideInInspector] public List<string> unsupportedProperties = new();
    }
}
