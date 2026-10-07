using System;

namespace Basis.ModelPickup.Validation
{
    public enum BasisModelSourceFormat : byte
    {
        Unknown = 0,
        Glb = 1,
        GltfJson = 2,
    }

    public enum BasisModelImageFormat : byte
    {
        Png = 1,
        Jpeg = 2,
    }

    public enum BasisGlbErrorKind : byte
    {
        None = 0,
        Malformed = 1,
        Unsupported = 2,
        OverLimit = 3,
        ImageRejected = 4,
        Cancelled = 5,
        /// <summary>A validator bug: an exception, invalid limits, or a failed sender self-check.</summary>
        Internal = 6,
    }

    /// <summary>Valid glTF content that v1 does not render or that glTFast cannot use, dropped from the canonical output.</summary>
    [Flags]
    public enum BasisGlbStripped : uint
    {
        None = 0,
        Animations = 1u << 0,
        MorphTargets = 1u << 1,
        Cameras = 1u << 2,
        Lights = 1u << 3,
        Instancing = 1u << 4,
        MaterialVariants = 1u << 5,
        MaterialExtensions = 1u << 6,
        OtherExtensions = 1u << 7,
        NonTrianglePrimitives = 1u << 8,
        ExtraScenes = 1u << 9,
        UnreferencedContent = 1u << 10,
        ExtraUvSets = 1u << 11,
        /// <summary>COLOR_1+, JOINTS_1+/WEIGHTS_1+, custom "_X" attributes, and JOINTS/WEIGHTS on meshes no skinned node uses.</summary>
        ExtraVertexStreams = 1u << 12,
        Extras = 1u << 13,
        /// <summary>Draco, meshopt, basisu, webp, avif or dds was present but optional; the core fallback was used.</summary>
        CompressionFallbackUsed = 1u << 14,
        UnusedSkins = 1u << 15,
    }

    public struct BasisGlbPrepareResult
    {
        public bool Ok;
        public string Error;
        public BasisGlbErrorKind ErrorKind;
        public BasisGlbPreparedModel Model;
    }

    public struct BasisGlbValidationResult
    {
        public bool Ok;
        /// <summary>English, with numbers; null when Ok. Receiver-mode text never contains bytes from the input.</summary>
        public string Error;
        public BasisGlbErrorKind ErrorKind;
        /// <summary>Canonical GLB; null when not Ok. The only bytes that may be handed to glTFast.</summary>
        public byte[] CleanGlb;
        public BasisGlbStats Stats;
        public BasisGlbStripped Stripped;
        /// <summary>Receiver: CleanGlb is byte-identical to the input (and is the same array). Diagnostic only.</summary>
        public bool InputWasCanonical;

        public static BasisGlbValidationResult Fail(BasisGlbErrorKind kind, string error)
        {
            return new BasisGlbValidationResult { Ok = false, ErrorKind = kind, Error = error };
        }
    }
}
