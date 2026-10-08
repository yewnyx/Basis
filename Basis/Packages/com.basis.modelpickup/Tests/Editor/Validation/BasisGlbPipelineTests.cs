using System;
using System.Threading;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGlbPipelineTests
    {
        /// <summary>
        /// Runs the async pipeline to completion without a synchronisation context, so continuations never wait on a
        /// UnitySynchronizationContext that the blocked test thread would have to pump.
        /// </summary>
        private static BasisGlbValidationResult RunLocal(byte[] source, IBasisModelImageSanitizer sanitizer, CancellationToken cancellation = default,
            BasisModelSourceFormat format = BasisModelSourceFormat.Glb, BasisModelLimits? limits = null)
        {
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                return BasisGlbPipeline.ValidateLocalAsync(source, format, limits ?? BasisModelLimits.Desktop, sanitizer, cancellation)
                    .GetAwaiter().GetResult();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        /// <summary>Two images; materials reference images[1] first, so canonical order is the reverse of source order.</summary>
        private static BasisGlbTestBuilder TwoImagesReversed()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.IndexedQuad16();
            b.AddImage(BasisGlbTestPng.Create(2, 2));
            b.AddImage(BasisGlbTestPng.CreateJpegHeader(6, 6), "image/jpeg");
            b.AddTexture(1);
            b.AddTexture(0);
            b.AddMaterial("{\"pbrMetallicRoughness\":{\"baseColorTexture\":{\"index\":0}},\"emissiveTexture\":{\"index\":1}}");
            b.Meshes[0] = b.Meshes[0].Replace("\"indices\":3", "\"indices\":3,\"material\":0");
            return b;
        }

        [Test]
        public void SanitizesEachImageOnceInCanonicalOrder()
        {
            byte[] glb = TwoImagesReversed().BuildGlb();
            var sanitizer = new BasisFakeImageSanitizer();
            BasisGlbValidationResult result = RunLocal(glb, sanitizer);
            BasisGlbTestRun.AssertOk(result);
            CollectionAssert.AreEqual(new[] { BasisModelImageFormat.Jpeg, BasisModelImageFormat.Png }, sanitizer.Formats);

            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(glb, BasisModelSourceFormat.Glb, BasisModelLimits.Desktop);
            Assert.That(prepared.Ok, Is.True, prepared.Error);
            Assert.That(prepared.Model.ImageCount, Is.EqualTo(2));
            Assert.That(prepared.Model.GetSourceImageIndex(0), Is.EqualTo(1));
            Assert.That(prepared.Model.GetSourceImageIndex(1), Is.EqualTo(0));
            Assert.That(prepared.Model.GetImageFormat(0), Is.EqualTo(BasisModelImageFormat.Jpeg));
            byte[] copy = prepared.Model.CopyImageSource(1);
            CollectionAssert.AreEqual(BasisGlbTestPng.Create(2, 2), copy);
            copy[0] = 0;
            Assert.That(prepared.Model.CopyImageSource(1)[0], Is.EqualTo(0x89), "each copy is fresh");
            Assert.Throws<ArgumentOutOfRangeException>(() => prepared.Model.CopyImageSource(2));
        }

        [Test]
        public void FailsWhenSanitizerFailsOrThrows()
        {
            byte[] glb = TwoImagesReversed().BuildGlb();
            BasisGlbValidationResult failed = RunLocal(glb, new BasisFakeImageSanitizer { Behaviour = BasisFakeImageSanitizer.Mode.Fail });
            BasisGlbTestRun.AssertFails(failed, BasisGlbErrorKind.ImageRejected, "images[1]: sanitiser refused it");
            BasisGlbValidationResult threw = RunLocal(glb, new BasisFakeImageSanitizer { Behaviour = BasisFakeImageSanitizer.Mode.Throw });
            BasisGlbTestRun.AssertFails(threw, BasisGlbErrorKind.ImageRejected, "InvalidOperationException");
            BasisGlbTestRun.AssertFails(RunLocal(glb, null), BasisGlbErrorKind.Internal, "No image sanitiser");
        }

        [TestCase("NotPng", BasisGlbErrorKind.ImageRejected, "Not a PNG")]
        [TestCase("SixteenBit", BasisGlbErrorKind.ImageRejected, "16-bit")]
        [TestCase("HugeDimensions", BasisGlbErrorKind.OverLimit, "4,096×4,096")]
        [TestCase("OverEightMegabytes", BasisGlbErrorKind.OverLimit, "The maximum is 8 MiB")]
        public void RejectsNonCompliantSanitizerOutput(string mode, BasisGlbErrorKind kind, string message)
        {
            var sanitizer = new BasisFakeImageSanitizer { Behaviour = (BasisFakeImageSanitizer.Mode)Enum.Parse(typeof(BasisFakeImageSanitizer.Mode), mode) };
            BasisGlbValidationResult result = RunLocal(BasisGlbTestBuilder.TexturedQuad(4, 4).BuildGlb(), sanitizer);
            BasisGlbTestRun.AssertFails(result, kind, message);
            StringAssert.Contains("after sanitising", result.Error);
        }

        [Test]
        public void TextureStatsUseSanitizedDimensions()
        {
            var sanitizer = new BasisFakeImageSanitizer { Behaviour = BasisFakeImageSanitizer.Mode.Generate, Width = 16, Height = 8 };
            BasisGlbValidationResult result = RunLocal(BasisGlbTestBuilder.TexturedQuad(4, 4).BuildGlb(), sanitizer);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stats.MaxTextureDimension, Is.EqualTo(16));
            Assert.That(result.Stats.TexturePixels, Is.EqualTo(128));
        }

        [Test]
        public void JpegBecomesPng()
        {
            BasisGlbValidationResult result = RunLocal(TwoImagesReversed().BuildGlb(), new BasisFakeImageSanitizer { Width = 4, Height = 4 });
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("image/jpeg"));
            BasisGlbValidationResult received = BasisGlbTestRun.Receive(result.CleanGlb);
            BasisGlbTestRun.AssertOk(received);
            Assert.That(received.InputWasCanonical, Is.True);
        }

        [Test]
        public void CancellationReportsCancelled()
        {
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                BasisGlbTestRun.AssertFails(RunLocal(BasisGlbTestBuilder.Triangle().BuildGlb(), new BasisFakeImageSanitizer(), cancelled.Token),
                    BasisGlbErrorKind.Cancelled, "Validation was cancelled.");
                BasisGlbTestRun.AssertFails(BasisGlbValidator.ValidateReceived(BasisGlbTestBuilder.Triangle().BuildGlb(), BasisModelLimits.Desktop, cancelled.Token),
                    BasisGlbErrorKind.Cancelled);
            }
            using (var midway = new CancellationTokenSource())
            {
                var sanitizer = new BasisFakeImageSanitizer { CancelAfterCall = midway };
                BasisGlbValidationResult result = RunLocal(TwoImagesReversed().BuildGlb(), sanitizer, midway.Token);
                BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.Cancelled);
                Assert.That(sanitizer.Calls, Is.EqualTo(1));
            }
        }

        [Test]
        public void SanitizedGrowthOverModelBytesIsOverLimit()
        {
            var limits = BasisModelLimits.Desktop;
            limits.MaxModelBytes = 4096;
            limits.MaxJsonBytes = 4096;
            limits.MaxImageBytes = 4096;
            // A 56×16 PNG (~3.7 KB) fits MaxImageBytes on its own, but the whole canonical model then exceeds 4 KiB.
            var sanitizer = new BasisFakeImageSanitizer { Behaviour = BasisFakeImageSanitizer.Mode.Generate, Width = 56, Height = 16 };
            BasisGlbValidationResult result = RunLocal(BasisGlbTestBuilder.TexturedQuad(2, 2).BuildGlb(), sanitizer, default, BasisModelSourceFormat.Glb, limits);
            BasisGlbTestRun.AssertFails(result, BasisGlbErrorKind.OverLimit, "Canonical model");
        }

        [Test]
        public void FinishTwiceIsInternal()
        {
            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(BasisGlbTestBuilder.Triangle().BuildGlb(), BasisModelSourceFormat.Glb, BasisModelLimits.Desktop);
            Assert.That(prepared.Ok, Is.True, prepared.Error);
            BasisGlbTestRun.AssertOk(BasisGlbValidator.Finish(prepared.Model));
            BasisGlbTestRun.AssertFails(BasisGlbValidator.Finish(prepared.Model), BasisGlbErrorKind.Internal, "already finished");
            BasisGlbTestRun.AssertFails(BasisGlbValidator.Finish(null), BasisGlbErrorKind.Internal);
        }

        [Test]
        public void FinishWithoutSanitizedImagesIsInternal()
        {
            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(BasisGlbTestBuilder.TexturedQuad(4, 4).BuildGlb(), BasisModelSourceFormat.Glb, BasisModelLimits.Desktop);
            Assert.That(prepared.Ok, Is.True, prepared.Error);
            BasisGlbTestRun.AssertFails(BasisGlbValidator.Finish(prepared.Model), BasisGlbErrorKind.Internal, "has no sanitised PNG");
        }

        [Test]
        public void PreparedBoundsMatchFinalStats()
        {
            BasisGlbPrepareResult prepared = BasisGlbValidator.Prepare(BasisGlbTestBuilder.Hierarchy(3).BuildGlb(), BasisModelSourceFormat.Glb, BasisModelLimits.Desktop);
            Assert.That(prepared.Ok, Is.True, prepared.Error);
            BasisGlbAabb bounds = prepared.Model.Bounds;
            BasisGlbValidationResult result = BasisGlbValidator.Finish(prepared.Model);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(bounds.MinX, Is.EqualTo(result.Stats.Bounds.MinX));
            Assert.That(bounds.MaxY, Is.EqualTo(result.Stats.Bounds.MaxY));
            Assert.That(bounds.MinX, Is.EqualTo(3f));
            Assert.That(prepared.Model.Stripped, Is.EqualTo(BasisGlbStripped.None));
        }

        [Test]
        public void ReceivedAsyncMatchesSynchronous()
        {
            byte[] glb = BasisGlbTestBuilder.SkinnedStrip(2).BuildGlb();
            SynchronizationContext previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                BasisGlbValidationResult async = BasisGlbPipeline.ValidateReceivedAsync(glb, BasisModelLimits.Mobile, CancellationToken.None).GetAwaiter().GetResult();
                BasisGlbValidationResult sync = BasisGlbValidator.ValidateReceived(glb, BasisModelLimits.Mobile);
                BasisGlbTestRun.AssertOk(async);
                CollectionAssert.AreEqual(sync.CleanGlb, async.CleanGlb);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }

        [Test]
        public void ConcurrentValidationsAreIndependent()
        {
            var inputs = new System.Collections.Generic.List<byte[]>();
            var expected = new System.Collections.Generic.List<byte[]>();
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                if (entry.Format != BasisModelSourceFormat.Glb) continue;
                inputs.Add(entry.Bytes);
                expected.Add(BasisGlbTestRun.Receive(entry.Bytes).CleanGlb);
            }
            var outputs = new byte[inputs.Count * 8][];
            System.Threading.Tasks.Parallel.For(0, outputs.Length, i =>
            {
                outputs[i] = BasisGlbValidator.ValidateReceived(inputs[i % inputs.Count], BasisModelLimits.Desktop).CleanGlb;
            });
            for (int i = 0; i < outputs.Length; i++) CollectionAssert.AreEqual(expected[i % inputs.Count], outputs[i], "run " + i);
        }

        [Test]
        public void LargeMeshIsAcceptedAndStable()
        {
            // A 256×256 vertex grid (65,536 vertices, 130,050 triangles) with normals, UVs and u32 indices.
            const int side = 256;
            var positions = new float[side * side * 3];
            var normals = new float[side * side * 3];
            var uvs = new float[side * side * 2];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int v = y * side + x;
                    positions[v * 3] = x * 0.01f;
                    positions[v * 3 + 2] = y * 0.01f;
                    normals[v * 3 + 1] = 1f;
                    uvs[v * 2] = x / (float)(side - 1);
                    uvs[v * 2 + 1] = y / (float)(side - 1);
                }
            }
            var indices = new int[(side - 1) * (side - 1) * 6];
            int o = 0;
            for (int y = 0; y < side - 1; y++)
            {
                for (int x = 0; x < side - 1; x++)
                {
                    int a = y * side + x;
                    indices[o++] = a;
                    indices[o++] = a + side;
                    indices[o++] = a + 1;
                    indices[o++] = a + 1;
                    indices[o++] = a + side;
                    indices[o++] = a + side + 1;
                }
            }
            var b = new BasisGlbTestBuilder();
            int position = b.AddFloats("VEC3", positions);
            int normal = b.AddFloats("VEC3", normals);
            int uv = b.AddFloats("VEC2", uvs);
            int index = b.AddIndices32(indices);
            b.AddNode("{\"mesh\":" + b.AddMesh(position, index, "\"NORMAL\":" + normal + ",\"TEXCOORD_0\":" + uv) + "}");
            BasisGlbValidationResult sent = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(sent);
            Assert.That(sent.Stats.Vertices, Is.EqualTo(side * side));
            Assert.That(sent.Stats.Triangles, Is.EqualTo((side - 1) * (side - 1) * 2));
            Assert.That(sent.Stats.Bounds.MaxX, Is.EqualTo(2.55f).Within(1e-5f));
            BasisGlbValidationResult received = BasisGlbTestRun.Receive(sent.CleanGlb, BasisModelLimits.Mobile);
            BasisGlbTestRun.AssertOk(received);
            Assert.That(received.InputWasCanonical, Is.True);
        }

        [Test]
        public void SelfCheckRequiresByteIdentityOutsideReleasePlayers()
        {
            Assert.That(BasisGlbValidator.RequireByteIdenticalSelfCheck, Is.True, "dotnet and the editor always check byte identity");
        }

        [Test]
        public void SenderRejectsOversizeSourceAndUnknownFormat()
        {
            var limits = BasisModelLimits.Desktop;
            limits.MaxSourceBytes = 100;
            BasisGlbPrepareResult big = BasisGlbValidator.Prepare(BasisGlbTestBuilder.Triangle().BuildGlb(), BasisModelSourceFormat.Glb, limits);
            Assert.That(big.ErrorKind, Is.EqualTo(BasisGlbErrorKind.OverLimit));
            StringAssert.Contains("Model file is", big.Error);
            BasisGlbPrepareResult unknown = BasisGlbValidator.Prepare(new byte[] { 1, 2, 3, 4 }, BasisModelSourceFormat.Unknown, BasisModelLimits.Desktop);
            Assert.That(unknown.ErrorKind, Is.EqualTo(BasisGlbErrorKind.Unsupported));
            BasisGlbPrepareResult sniffed = BasisGlbValidator.Prepare(BasisGlbTestBuilder.Triangle().BuildGlb(), BasisModelSourceFormat.Unknown, BasisModelLimits.Desktop);
            Assert.That(sniffed.Ok, Is.True, sniffed.Error);
            BasisGlbPrepareResult empty = BasisGlbValidator.Prepare(new byte[0], BasisModelSourceFormat.Glb, BasisModelLimits.Desktop);
            Assert.That(empty.ErrorKind, Is.EqualTo(BasisGlbErrorKind.Malformed));
            Assert.That(empty.Error, Is.EqualTo("No data."));
        }
    }
}
