using System;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup.Tests
{
    /// <summary>Shared fixtures for the engine-free model Core tests.</summary>
    internal static class BasisModelCoreTestData
    {
        internal static readonly Guid SampleId = new Guid("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");
        internal const ushort SampleOwnerId = 7;

        /// <summary>A model every tier admits: one triangle in a 1 m × 1 m × 0.5 m box.</summary>
        internal static BasisGlbClaims SmallClaims()
        {
            return new BasisGlbClaims
            {
                FormatVersion = BasisGlbClaims.CurrentFormatVersion,
                Primitives = 1,
                Nodes = 1,
                Vertices = 3,
                Triangles = 1,
                DrawCalls = 1,
                MeshInstances = 1,
                EstimatedDecodedBytes = 264,
                RenderedTriangles = 1,
                Bounds = new BasisGlbAabb { MaxX = 1f, MaxY = 1f, MaxZ = 0.5f },
            };
        }

        /// <summary>Distinct value in every field (well formed, not admissible), for byte-layout tests.</summary>
        internal static BasisGlbClaims DistinctClaims()
        {
            return new BasisGlbClaims
            {
                FormatVersion = 1,
                Flags = BasisGlbClaims.FlagHasSkins | BasisGlbClaims.FlagUsesUnlit,
                Primitives = 0x0102,
                Nodes = 0x0304,
                Materials = 0x0506,
                Images = 0x0708,
                Joints = 0x090A,
                Vertices = 0x0B0C0D0E,
                Triangles = 0x0F101112,
                DrawCalls = 0x13141516,
                TexturePixels = 0x1718191A1B1C1D1EL,
                EstimatedDecodedBytes = 0x1F20212223242526L,
                Bounds = new BasisGlbAabb { MinX = -1f, MinY = 0f, MinZ = 0.5f, MaxX = 2f, MaxY = 3f, MaxZ = 4f },
                RenderedTriangles = 0x2728292A2B2C2D2EL,
                SkinnedVertexInstances = 0x2F30313233343536L,
                MeshInstances = 0x3738,
                Textures = 0x393A,
                Skins = 0x3B3C,
                MaxTextureDimension = 0x3D3E,
            };
        }

        internal static BasisModelSpawnTail SmallTail()
        {
            return new BasisModelSpawnTail { SizeMode = BasisModelSizeMode.Fit, BaseScale = 0.5f, Claims = SmallClaims() };
        }

        internal static BasisModelPose SamplePose()
        {
            return new BasisModelPose(new BasisModelVec3(1.5f, -2.25f, 3f), new BasisModelQuat(0f, 0.6f, 0f, 0.8f));
        }

        internal static byte[] TailBytes(in BasisModelSpawnTail tail, int length)
        {
            var bytes = new byte[length];
            BasisModelWire.WriteTail(bytes, tail);
            return bytes;
        }

        /// <summary>A spawn whose prefix fields and tail bytes are exactly what the caller says, valid or not.</summary>
        internal static byte[] RawSpawn(int fieldA, int fieldB, byte[] tail, int totalBytes = 1000, string ownerName = "Alice")
        {
            return BasisModelShareWire.EncodeSpawn(BasisModelShareWire.OpSpawn, SampleId, SampleOwnerId, ownerName, fieldA, fieldB,
                totalBytes, BasisModelShareWire.ExpectedChunkCount(totalBytes, BasisModelWire.ChunkPayloadBytes), SamplePose(), tail);
        }

        internal static BasisModelSpawnHeader ReadHeader(byte[] message)
        {
            if (!BasisModelShareWire.TryReadSpawn(message, out BasisModelSpawnHeader header, out BasisModelShareWireError error))
                throw new InvalidOperationException("Fixture spawn did not parse: " + error);
            return header;
        }

        /// <summary>A spawn admission input that the Desktop tier accepts.</summary>
        internal static BasisModelAdmissionInput AcceptableInput()
        {
            return new BasisModelAdmissionInput
            {
                ReceiveEnabled = true,
                HeaderOk = true,
                Tail = SmallTail(),
                TotalBytes = 1000,
                TotalChunks = 1,
                PoseValid = true,
            };
        }

        internal static byte[] ClaimsBytes(in BasisGlbClaims claims)
        {
            var bytes = new byte[BasisGlbClaims.EncodedSize];
            BasisGlbClaims copy = claims;
            copy.TryWrite(bytes);
            return bytes;
        }
    }
}
