using System.Collections.Generic;
using UnityEngine;

namespace NdmfMToon10ToLilToon
{
    [DisallowMultipleComponent]
    [AddComponentMenu("NDMF/NDMF MToon10 to lilToon")]
    public sealed class MToonLilToonComponent : MonoBehaviour
    {
        public enum FaceShadowMaskType
        {
            Strength = 1,
            Flat = 0,
            Sdf = 2
        }

        public Shader lilToonShader;
        public bool enableHairMerge;
        public bool enableHairOutlineCorrection;
        [Range(0f, 1f)] public float hairTipOutlineWidth = 0.2f;
        [Range(0f, 1f)] public float hairTipRange = 0.3f;
        public List<HairMaterialSelection> hairSelections = new();
        public bool enableEyebrowStencil;
        public Material eyebrowStencilMaterial;
        public Material fakeShadowFaceMaterial;
        public bool enableFakeShadow;
        public Vector3 fakeShadowDirection = new Vector3(1f, 4f, 2f);
        public float fakeShadowOffset = 0.005f;
        public bool enableFaceShadowTuning;
        public Material faceShadowFaceMaterial;
        public Texture2D faceShadowSdfTexture;
        public FaceShadowMaskType faceShadowMaskType = FaceShadowMaskType.Flat;
        public float shadowStrengthMaskLod;
        public bool disableShadowReceiveForFace;
        public bool disableBacklightStrengthForFace;
        public LilToonGlobalOverrides globalOverrides = new();
        public bool verboseLog;
        [HideInInspector] public bool showAdvanced;
        [HideInInspector] public bool showHairMaterials;
        public bool isPreviewing;

        [HideInInspector] public int scannedMaterialCount;
        [HideInInspector] public int convertedMaterialCount;
        [HideInInspector] public int skippedMaterialCount;
        [HideInInspector] public List<string> warnings = new();
        [HideInInspector] public List<string> unsupportedProperties = new();
    }
}
