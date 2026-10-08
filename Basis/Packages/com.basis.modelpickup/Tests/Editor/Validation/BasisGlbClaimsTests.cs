using System;
using System.Reflection;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGlbClaimsTests
    {
        private static BasisGlbClaims Sample()
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

        /// <summary>A model every tier admits.</summary>
        private static BasisGlbClaims Admissible()
        {
            return new BasisGlbClaims
            {
                FormatVersion = 1,
                Primitives = 1,
                Nodes = 1,
                Vertices = 3,
                Triangles = 1,
                DrawCalls = 1,
                MeshInstances = 1,
                EstimatedDecodedBytes = 264,
                RenderedTriangles = 1,
                Bounds = new BasisGlbAabb { MaxX = 1f, MaxY = 1f },
            };
        }

        [Test]
        public void ClaimsV1LayoutIsPinnedByteForByte()
        {
            byte[] expected =
            {
                0x01, 0x05,
                0x02, 0x01, 0x04, 0x03, 0x06, 0x05, 0x08, 0x07, 0x0A, 0x09,
                0x0E, 0x0D, 0x0C, 0x0B, 0x12, 0x11, 0x10, 0x0F, 0x16, 0x15, 0x14, 0x13,
                0x1E, 0x1D, 0x1C, 0x1B, 0x1A, 0x19, 0x18, 0x17,
                0x26, 0x25, 0x24, 0x23, 0x22, 0x21, 0x20, 0x1F,
                0x00, 0x00, 0x80, 0xBF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x3F,
                0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x40, 0x40, 0x00, 0x00, 0x80, 0x40,
                0x2E, 0x2D, 0x2C, 0x2B, 0x2A, 0x29, 0x28, 0x27,
                0x36, 0x35, 0x34, 0x33, 0x32, 0x31, 0x30, 0x2F,
                0x38, 0x37, 0x3A, 0x39, 0x3C, 0x3B, 0x3E, 0x3D,
            };
            Assert.That(expected.Length, Is.EqualTo(BasisGlbClaims.EncodedSize));
            Assert.That(BasisGlbClaims.EncodedSize, Is.EqualTo(88));
            var written = new byte[88];
            Assert.That(Sample().TryWrite(written), Is.True);
            CollectionAssert.AreEqual(expected, written);
            Assert.That(Sample().TryWrite(new byte[87]), Is.False);
        }

        [Test]
        public void ClaimsRoundTrip88Bytes()
        {
            var buffer = new byte[96];
            Assert.That(Sample().TryWrite(buffer), Is.True);
            Assert.That(BasisGlbClaims.TryRead(buffer, out BasisGlbClaims read, out string error), Is.True, error);
            var again = new byte[96];
            Assert.That(read.TryWrite(again), Is.True);
            CollectionAssert.AreEqual(buffer, again);
            Assert.That(read.HasSkins && read.UsesUnlit && !read.UsesQuantization, Is.True);

            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.SkinnedStrip(2));
            BasisGlbTestRun.AssertOk(result);
            BasisGlbClaims claims = BasisGlbClaims.FromStats(result.Stats);
            Assert.That(claims.TryWrite(buffer), Is.True);
            Assert.That(BasisGlbClaims.TryRead(buffer, out BasisGlbClaims back, out error), Is.True, error);
            Assert.That(back.Vertices, Is.EqualTo(result.Stats.Vertices));
            Assert.That(back.Joints, Is.EqualTo(result.Stats.Joints));
            Assert.That(back.SkinnedVertexInstances, Is.EqualTo(result.Stats.SkinnedVertexInstances));
            Assert.That(back.MaxTextureDimension, Is.EqualTo(result.Stats.MaxTextureDimension));
            Assert.That(back.Verify(result.Stats, out error), Is.True, error);
            Assert.That(back.TryAdmit(result.CleanGlb.Length, BasisModelLimits.Mobile, out error), Is.True, error);
        }

        [Test]
        public void ClaimsRejectUnknownVersionFlagsNonFinite()
        {
            var buffer = new byte[88];
            BasisGlbClaims claims = Sample();
            claims.FormatVersion = 2;
            claims.TryWrite(buffer);
            Assert.That(BasisGlbClaims.TryRead(buffer, out _, out string error), Is.False);
            StringAssert.Contains("version 2", error);

            claims = Sample();
            claims.Flags = 0x08;
            claims.TryWrite(buffer);
            Assert.That(BasisGlbClaims.TryRead(buffer, out _, out error), Is.False);
            StringAssert.Contains("flag", error);

            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                claims = Sample();
                claims.Bounds.MaxY = bad;
                claims.TryWrite(buffer);
                Assert.That(BasisGlbClaims.TryRead(buffer, out _, out error), Is.False, bad.ToString());
            }

            claims = Sample();
            claims.Bounds.MinZ = 5f;
            claims.TryWrite(buffer);
            Assert.That(BasisGlbClaims.TryRead(buffer, out _, out error), Is.False);
            StringAssert.Contains("inverted", error);

            claims = Sample();
            claims.Vertices = -1;
            claims.TryWrite(buffer);
            Assert.That(BasisGlbClaims.TryRead(buffer, out _, out error), Is.False);
            StringAssert.Contains("negative", error);

            Assert.That(BasisGlbClaims.TryRead(new byte[87], out _, out error), Is.False);
            StringAssert.Contains("88", error);
        }

        [Test]
        public void AdmitRejectsClaimsAboveLimits()
        {
            BasisModelLimits mobile = BasisModelLimits.Mobile;
            Assert.That(Admissible().TryAdmit(1000, mobile, out string error), Is.True, error);
            Assert.That(Admissible().TryAdmit(0, mobile, out error), Is.False);
            Assert.That(Admissible().TryAdmit((int)mobile.MaxModelBytes + 1, mobile, out error), Is.False);
            StringAssert.Contains("The maximum is 16 MiB", error);

            AssertRejected(c => { c.Primitives = (ushort)(mobile.MaxPrimitives + 1); return c; }, "Primitive count");
            AssertRejected(c => { c.Nodes = (ushort)(mobile.MaxNodes + 1); return c; }, "Node count");
            AssertRejected(c => { c.Materials = (ushort)(mobile.MaxMaterials + 1); return c; }, "Material count");
            AssertRejected(c => { c.Images = (ushort)(mobile.MaxImages + 1); return c; }, "Image count");
            AssertRejected(c => { c.Vertices = mobile.MaxVertices + 1; return c; }, "Vertex count");
            AssertRejected(c => { c.Triangles = mobile.MaxTriangles + 1; return c; }, "Triangle count");
            AssertRejected(c => { c.DrawCalls = mobile.MaxDrawCalls + 1; return c; }, "Draw call count");
            AssertRejected(c => { c.TexturePixels = mobile.MaxTotalTexturePixels + 1; return c; }, "Texture pixel count");
            AssertRejected(c => { c.EstimatedDecodedBytes = mobile.MaxEstimatedDecodedBytes + 1; return c; }, "Estimated decoded memory");
            AssertRejected(c => { c.RenderedTriangles = mobile.MaxRenderedTriangles + 1; return c; }, "Rendered triangle count");
            AssertRejected(c => { c.SkinnedVertexInstances = mobile.MaxSkinnedVertexInstances + 1; return c; }, "Skinned vertex instance count");
            AssertRejected(c => { c.MeshInstances = (ushort)(mobile.MaxMeshInstances + 1); return c; }, "Mesh instance count");
            AssertRejected(c => { c.Textures = (ushort)(mobile.MaxTextures + 1); return c; }, "Texture count");
            AssertRejected(c => { c.MaxTextureDimension = (ushort)(mobile.MaxTextureDimension + 1); return c; }, "Texture dimension");
            AssertRejected(c =>
            {
                c.Flags = BasisGlbClaims.FlagHasSkins;
                c.Skins = (ushort)(mobile.MaxSkins + 1);
                c.Joints = (ushort)(c.Skins * 2);
                return c;
            }, "Skin count");
            AssertRejected(c =>
            {
                c.Flags = BasisGlbClaims.FlagHasSkins;
                c.Skins = 1;
                c.Joints = (ushort)(mobile.MaxJointsPerSkin + 1);
                return c;
            }, "inconsistent skins");
            AssertRejected(c => { c.Skins = 1; c.Joints = 1; return c; }, "inconsistent skins");
            AssertRejected(c => { c.Triangles = 0; return c; }, "nothing to display");
            AssertRejected(c => { c.Vertices = 2; return c; }, "nothing to display");
        }

        private static void AssertRejected(Func<BasisGlbClaims, BasisGlbClaims> change, string message)
        {
            BasisGlbClaims claims = change(Admissible());
            Assert.That(claims.TryAdmit(1000, BasisModelLimits.Mobile, out string error), Is.False, message);
            StringAssert.Contains(message, error);
        }

        [Test]
        public void AdmitRejectsBoundsFarFromTheOrigin()
        {
            BasisModelLimits limits = BasisModelLimits.Desktop;
            BasisGlbClaims claims = Admissible();
            claims.Bounds = new BasisGlbAabb { MinX = 99999f, MaxX = 100000f, MaxY = 1f };
            Assert.That(claims.TryAdmit(1000, limits, out string error), Is.True, error);
            claims.Bounds = new BasisGlbAabb { MinX = 200000f, MaxX = 200001f, MaxY = 1f };
            Assert.That(claims.TryAdmit(1000, limits, out error), Is.False);
            StringAssert.Contains("from its origin", error);
            claims.Bounds = new BasisGlbAabb { MinZ = -150000f, MaxZ = -149999f, MaxY = 1f };
            Assert.That(claims.TryAdmit(1000, limits, out error), Is.False);
        }

        [Test]
        public void AdmitAcceptsFlatModelsButNotEmptyOrHugeOnes()
        {
            BasisModelLimits limits = BasisModelLimits.Desktop;
            BasisGlbClaims claims = Admissible();
            claims.Bounds = new BasisGlbAabb { MaxX = 1f, MaxZ = 1f }; // SizeY = 0
            Assert.That(claims.TryAdmit(1000, limits, out string error), Is.True, error);
            claims.Bounds = new BasisGlbAabb { MaxX = 0.00005f };
            Assert.That(claims.TryAdmit(1000, limits, out error), Is.False);
            StringAssert.Contains("empty", error);
            claims.Bounds = new BasisGlbAabb { MinX = -6000f, MaxX = 6000f };
            Assert.That(claims.TryAdmit(1000, limits, out error), Is.False);
            StringAssert.Contains("across", error);
            claims.Bounds = new BasisGlbAabb { MaxX = float.NaN, MaxY = 1f };
            Assert.That(claims.TryAdmit(1000, limits, out error), Is.False, "NaN never passes a limit");
        }

        [Test]
        public void VerifyRejectsStatsAboveClaimsAndBoundsMismatch()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestCorpus.TwoMaterialsTwoSamplers());
            BasisGlbTestRun.AssertOk(result);
            BasisGlbStats stats = result.Stats;
            BasisGlbClaims claims = BasisGlbClaims.FromStats(stats);
            Assert.That(claims.Verify(stats, out string error), Is.True, error);

            BasisGlbClaims generous = claims;
            generous.Vertices += 100;
            generous.TexturePixels += 100;
            Assert.That(generous.Verify(stats, out error), Is.True, "claims may be generous");

            BasisGlbClaims low = claims;
            low.Vertices -= 1;
            Assert.That(low.Verify(stats, out error), Is.False);
            StringAssert.Contains("Vertex count is 4 but the sender claimed 3", error);

            low = claims;
            low.Textures -= 1;
            Assert.That(low.Verify(stats, out error), Is.False);

            BasisGlbClaims flags = claims;
            flags.Flags |= BasisGlbClaims.FlagUsesQuantization;
            Assert.That(flags.Verify(stats, out error), Is.False);
            StringAssert.Contains("flags", error);

            BasisGlbClaims moved = claims;
            moved.Bounds.MaxX += 0.01f;
            Assert.That(moved.Verify(stats, out error), Is.False);
            StringAssert.Contains("bounds", error);

            BasisGlbClaims nudged = claims;
            nudged.Bounds.MaxX += 1e-6f;
            Assert.That(nudged.Verify(stats, out error), Is.True, "within ApproximatelyEquals tolerance");
        }

        [Test]
        public void MobileIsNoLooserThanDesktop()
        {
            BasisModelLimits desktop = BasisModelLimits.Desktop;
            BasisModelLimits mobile = BasisModelLimits.Mobile;
            int checkedFields = 0;
            foreach (FieldInfo field in typeof(BasisModelLimits).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                double d = Convert.ToDouble(field.GetValue(desktop));
                double m = Convert.ToDouble(field.GetValue(mobile));
                if (field.Name.StartsWith("Min", StringComparison.Ordinal))
                {
                    Assert.That(m, Is.GreaterThanOrEqualTo(d), field.Name);
                }
                else
                {
                    Assert.That(m, Is.LessThanOrEqualTo(d), field.Name);
                }
                checkedFields++;
            }
            Assert.That(checkedFields, Is.GreaterThan(40));
            Assert.That(mobile.MaxModelBytes, Is.LessThan(desktop.MaxModelBytes));
        }

        [Test]
        public void LimitsSelfCheckRejectsBadValues()
        {
            Assert.That(BasisModelLimits.Desktop.TryValidate(out string error), Is.True, error);
            Assert.That(BasisModelLimits.Mobile.TryValidate(out error), Is.True, error);
            Assert.That(BasisModelLimits.Desktop.MinBoundsExtentMeters, Is.EqualTo(1e-4f));
            Assert.That(BasisModelLimits.Desktop.MaxBoundsExtentMeters, Is.EqualTo(1e4f));
            Assert.That(BasisModelLimits.Desktop.MaxBoundsAbsCoordinateMeters, Is.EqualTo(1e5f));
            Assert.That(BasisModelLimits.Mobile.MaxBoundsAbsCoordinateMeters, Is.EqualTo(1e5f));

            AssertInvalid(l => { l.MaxVertices = 0; return l; });
            AssertInvalid(l => { l.MaxNodes = 70000; return l; });
            AssertInvalid(l => { l.MaxSkins = 300; return l; });
            AssertInvalid(l => { l.MaxJsonBytes = (int)(l.MaxModelBytes + 1); return l; });
            AssertInvalid(l => { l.MaxModelBytes = int.MaxValue; return l; });
            AssertInvalid(l => { l.MaxTextureDimension = 16385; return l; });
            AssertInvalid(l => { l.MaxBoundsAbsCoordinateMeters = 0f; return l; });
            AssertInvalid(l => { l.MaxWorldMatrixAbs = float.NaN; return l; });
            AssertInvalid(l => { l.MaxNodeScale = float.PositiveInfinity; return l; });
            AssertInvalid(l => { l.MinBoundsExtentMeters = 2e4f; return l; });
            AssertInvalid(l => { l.MaxJsonDepth = 1000; return l; });
        }

        private static void AssertInvalid(Func<BasisModelLimits, BasisModelLimits> change)
        {
            BasisModelLimits limits = change(BasisModelLimits.Desktop);
            Assert.That(limits.TryValidate(out string error), Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            BasisGlbValidationResult received = BasisGlbValidator.ValidateReceived(BasisGlbTestBuilder.Triangle().BuildGlb(), limits);
            Assert.That(received.ErrorKind, Is.EqualTo(BasisGlbErrorKind.Internal), "invalid limits are a caller bug");
            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(BasisGlbTestBuilder.Triangle().BuildGlb(), BasisModelSourceFormat.Glb, limits);
            Assert.That(prepared.ErrorKind, Is.EqualTo(BasisGlbErrorKind.Internal));
        }

        [TestCase(true, 16384, true)]
        [TestCase(false, 4096, true)]
        [TestCase(false, 1, true)]
        [TestCase(false, 0, false)]
        [TestCase(false, 4097, false)]
        [TestCase(false, 32768, false)]
        public void ForDeviceFollowsTheTierRule(bool mobileGpu, int memoryMegabytes, bool expectMobile)
        {
            BasisModelLimits limits = BasisModelLimits.ForDevice(mobileGpu, memoryMegabytes);
            Assert.That(limits.MaxModelBytes, Is.EqualTo(expectMobile ? BasisModelLimits.Mobile.MaxModelBytes : BasisModelLimits.Desktop.MaxModelBytes));
            Assert.That(limits.MaxVertices, Is.EqualTo(expectMobile ? BasisModelLimits.Mobile.MaxVertices : BasisModelLimits.Desktop.MaxVertices));
        }

        [Test]
        public void ApproximatelyEqualsIsNaNSafeAndSymmetric()
        {
            var a = new BasisGlbAabb { MaxX = 1f, MaxY = 1f, MaxZ = 1f };
            BasisGlbAabb b = a;
            Assert.That(a.ApproximatelyEquals(b), Is.True);
            b.MaxX = float.NaN;
            Assert.That(a.ApproximatelyEquals(b) || b.ApproximatelyEquals(a), Is.False);
            var big = new BasisGlbAabb { MaxX = 1000f, MaxY = 1f, MaxZ = 1f };
            BasisGlbAabb close = big;
            close.MaxX = 1000.005f;
            Assert.That(big.ApproximatelyEquals(close) && close.ApproximatelyEquals(big), Is.True);
            BasisGlbAabb unity = big.ToUnitySpace();
            Assert.That(unity.MinX, Is.EqualTo(-1000f));
            Assert.That(unity.MaxX, Is.EqualTo(0f));
        }
    }
}
