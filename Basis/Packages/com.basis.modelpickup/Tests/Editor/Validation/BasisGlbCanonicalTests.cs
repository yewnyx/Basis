using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisGlbCanonicalTests
    {
        [Test]
        public void IsIdempotentForCorpus()
        {
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                BasisGlbValidationResult first = BasisGlbTestRun.Send(entry.Bytes, entry.Format);
                Assert.That(first.Ok, Is.True, entry.Name + ": " + first.Error);
                BasisGlbValidationResult again = BasisGlbTestRun.Send(first.CleanGlb, BasisModelSourceFormat.Glb);
                Assert.That(again.Ok, Is.True, entry.Name + " (again): " + again.Error);
                CollectionAssert.AreEqual(first.CleanGlb, again.CleanGlb, entry.Name);
                Assert.That(again.Stripped, Is.EqualTo(BasisGlbStripped.None), entry.Name);
            }
        }

        [Test]
        public void ReceiverReCanonicalisesSenderOutputByteIdentically()
        {
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                BasisGlbValidationResult sent = BasisGlbTestRun.Send(entry.Bytes, entry.Format);
                Assert.That(sent.Ok, Is.True, entry.Name + ": " + sent.Error);
                BasisGlbValidationResult received = BasisGlbTestRun.Receive(sent.CleanGlb);
                Assert.That(received.Ok, Is.True, entry.Name + ": " + received.Error);
                Assert.That(received.InputWasCanonical, Is.True, entry.Name);
                Assert.That(ReferenceEquals(received.CleanGlb, sent.CleanGlb), Is.True, entry.Name + ": canonical input is returned as is");
                Assert.That(received.Stripped, Is.EqualTo(BasisGlbStripped.None), entry.Name);
                Assert.That(BasisGlbStats.AreEqual(received.Stats, sent.Stats), Is.True, entry.Name);
                Assert.That(BasisGlbClaims.FromStats(sent.Stats).Verify(received.Stats, out string error), Is.True, entry.Name + ": " + error);
            }
        }

        [Test]
        public void ReceiverAndSenderAgreeOnASourceGlb()
        {
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                if (entry.Format != BasisModelSourceFormat.Glb) continue;
                BasisGlbValidationResult sent = BasisGlbTestRun.Send(entry.Bytes);
                BasisGlbValidationResult received = BasisGlbTestRun.Receive(entry.Bytes);
                BasisGlbTestRun.AssertOk(sent);
                BasisGlbTestRun.AssertOk(received);
                CollectionAssert.AreEqual(sent.CleanGlb, received.CleanGlb, entry.Name);
                Assert.That(received.Stripped, Is.EqualTo(sent.Stripped), entry.Name);
            }
        }

        [Test]
        public void TriangleJsonMatchesGolden()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.Triangle());
            BasisGlbTestRun.AssertOk(result);
            const string golden = "{\"asset\":{\"version\":\"2.0\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],"
                + "\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":0}}]}],"
                + "\"accessors\":[{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,0]}],"
                + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":36,\"target\":34962}],\"buffers\":[{\"byteLength\":36}]}";
            Assert.That(BasisGlbTestRun.JsonOf(result.CleanGlb), Is.EqualTo(golden));
        }

        [Test]
        public void FloatsRoundTripBitExactly()
        {
            var random = new Random(1234);
            var values = new List<float> { 0f, -0f, float.Epsilon, -float.Epsilon, float.MaxValue, float.MinValue, 1e-38f, 1.17549435e-38f, 0.1f, 1f / 3f };
            var bytes = new byte[4];
            while (values.Count < 10000)
            {
                random.NextBytes(bytes);
                float value = BitConverter.ToSingle(bytes, 0);
                if (float.IsNaN(value) || float.IsInfinity(value)) continue;
                values.Add(value);
            }
            var json = new StringBuilder("[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0) json.Append(',');
                json.Append((values[i] == 0f ? 0f : values[i]).ToString("G9", CultureInfo.InvariantCulture));
            }
            json.Append(']');
            byte[] data = Encoding.ASCII.GetBytes(json.ToString());
            var limits = new BasisJsonReaderLimits { MaxDepth = 4, MaxTokens = 20000, MaxKeyBytes = 64, MaxObjectKeys = 8, MaxNumberChars = 64 };
            var reader = new BasisJsonReader(data, 0, data.Length, limits, false);
            Assert.That(reader.Read(), Is.True);
            for (int i = 0; i < values.Count; i++)
            {
                Assert.That(reader.Read(), Is.True, reader.Error);
                Assert.That(reader.TryGetSingle(out float parsed), Is.True, "value " + i);
                float expected = values[i] == 0f ? 0f : values[i];
                Assert.That(BitConverter.SingleToInt32Bits(parsed), Is.EqualTo(BitConverter.SingleToInt32Bits(expected)), "value " + i);
            }
        }

        [Test]
        public void BinHoldsOnlyAccessorDataAndImages()
        {
            const byte Marker = 0xA5;
            // Marker bytes in the gap before the first element (accessor byteOffset 4), in the stride padding (16-byte
            // stride for 12-byte elements), in the view's tail, and in an unreferenced view.
            var exact = new byte[4 + 16 * 3 + 12];
            for (int i = 0; i < exact.Length; i++) exact[i] = Marker;
            for (int i = 0; i < 9; i++) BitConverter.GetBytes(BasisGlbTestBuilder.TrianglePositions[i]).CopyTo(exact, 4 + 16 * (i / 3) + 4 * (i % 3));
            var b = new BasisGlbTestBuilder();
            int viewIndex = b.AddView(exact, 16);
            int position = b.AddAccessor(viewIndex, BasisGltfComponent.Float, 3, "VEC3", false, 4);
            b.AddView(new byte[] { Marker, Marker, Marker, Marker }); // an unreferenced view
            int mesh = b.AddMesh(position);
            b.AddNode("{\"mesh\":" + mesh + "}");

            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            byte[] bin = BasisGlbTestRun.BinOf(result.CleanGlb);
            Assert.That(bin.Length, Is.EqualTo(36));
            CollectionAssert.DoesNotContain(bin, Marker);
            Assert.That(result.Stripped & BasisGlbStripped.UnreferencedContent, Is.EqualTo(BasisGlbStripped.UnreferencedContent));
        }

        [Test]
        public void HeaderAndPaddingAreExact()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.Quantized());
            BasisGlbTestRun.AssertOk(result);
            byte[] glb = result.CleanGlb;
            Assert.That(BitConverter.ToUInt32(glb, 0), Is.EqualTo(0x46546C67u));
            Assert.That(BitConverter.ToUInt32(glb, 4), Is.EqualTo(2u));
            Assert.That(BitConverter.ToUInt32(glb, 8), Is.EqualTo((uint)glb.Length));
            int jsonLength = BitConverter.ToInt32(glb, 12);
            Assert.That(jsonLength % 4, Is.EqualTo(0));
            Assert.That(BitConverter.ToUInt32(glb, 16), Is.EqualTo(0x4E4F534Au));
            string json = Encoding.ASCII.GetString(glb, 20, jsonLength);
            int end = json.LastIndexOf('}');
            for (int i = end + 1; i < json.Length; i++) Assert.That(json[i], Is.EqualTo(' '));
            int binHeader = 20 + jsonLength;
            int binLength = BitConverter.ToInt32(glb, binHeader);
            Assert.That(binLength % 4, Is.EqualTo(0));
            Assert.That(BitConverter.ToUInt32(glb, binHeader + 4), Is.EqualTo(0x004E4942u));
            Assert.That(binHeader + 8 + binLength, Is.EqualTo(glb.Length));
            Assert.That(result.Stats.CanonicalBytes, Is.EqualTo(glb.Length));
            Assert.That(result.Stats.JsonBytes, Is.EqualTo(jsonLength));
            Assert.That(result.Stats.BinBytes, Is.EqualTo(binLength));
        }

        [Test]
        public void PaddedVertexViewsDeclareTheirStride()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.Quantized());
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            // u16 VEC3 positions (6 B → 8), i8 VEC3 normals (3 B → 4), u8 VEC2 UVs (2 B → 4).
            StringAssert.Contains("{\"buffer\":0,\"byteLength\":24,\"byteStride\":8,\"target\":34962}", json);
            StringAssert.Contains("\"byteLength\":12,\"byteStride\":4,\"target\":34962}", json);
            StringAssert.Contains("\"extensionsUsed\":[\"KHR_mesh_quantization\"],\"extensionsRequired\":[\"KHR_mesh_quantization\"]", json);

            BasisGlbValidationResult skinned = BasisGlbTestRun.Send(BasisGlbTestBuilder.SkinnedStrip(2));
            BasisGlbTestRun.AssertOk(skinned);
            string skinnedJson = BasisGlbTestRun.JsonOf(skinned.CleanGlb);
            Assert.That(skinnedJson, Does.Not.Contain("byteStride"), "float, u8×4 joints, u16 index and IBM views are never strided");
            BasisGlbValidationResult textured = BasisGlbTestRun.Send(BasisGlbTestBuilder.TexturedQuad(4, 4));
            BasisGlbTestRun.AssertOk(textured);
            Assert.That(BasisGlbTestRun.JsonOf(textured.CleanGlb), Does.Not.Contain("byteStride"));
        }

        [Test]
        public void QuantizedVec3IsPaddedToStrideFour()
        {
            BasisGlbValidationResult result = BasisGlbTestRun.Send(BasisGlbTestBuilder.Quantized());
            BasisGlbTestRun.AssertOk(result);
            byte[] bin = BasisGlbTestRun.BinOf(result.CleanGlb);
            // Positions: three u16 VEC3 elements at stride 8, each followed by two zero padding bytes.
            CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0xE8, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0xE8, 0x03, 0, 0, 0, 0 },
                new ArraySegment<byte>(bin, 0, 24));
            // Normals: i8 VEC3 at stride 4.
            CollectionAssert.AreEqual(new byte[] { 0, 0, 127, 0, 0, 0, 127, 0, 0, 0, 127, 0 }, new ArraySegment<byte>(bin, 24, 12));
            Assert.That(result.Stats.UsesQuantization, Is.True);
        }

        [Test]
        public void UnreferencedContentIsDroppedAndFlagged()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.Triangle();
            int extraPosition = b.AddFloats("VEC3", BasisGlbTestBuilder.TrianglePositions);
            int extraMesh = b.AddMesh(extraPosition);
            b.AddNode("{\"mesh\":" + extraMesh + "}", false);
            b.AddMaterial("{}");
            b.Samplers.Add("{\"magFilter\":9728}");
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            Assert.That(result.Stripped & BasisGlbStripped.UnreferencedContent, Is.EqualTo(BasisGlbStripped.UnreferencedContent));
            Assert.That(result.Stats.Nodes, Is.EqualTo(1));
            Assert.That(result.Stats.Meshes, Is.EqualTo(1));
            Assert.That(result.Stats.Accessors, Is.EqualTo(1));
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("materials"));
            Assert.That(json, Does.Not.Contain("samplers"));
        }

        [Test]
        public void NamesGeneratorAndCopyrightAreNeverEmitted()
        {
            BasisGlbTestBuilder b = BasisGlbTestBuilder.TexturedQuad(4, 4);
            b.Asset = "{\"version\":\"2.0\",\"generator\":\"Evil\\u202Egen\",\"copyright\":\"(c) ZZQ\"}";
            b.Nodes[0] = "{\"mesh\":0,\"name\":\"node\\u0000\\u202E<b>bold</b>\"}";
            b.Meshes[0] = b.Meshes[0].Insert(1, "\"name\":\"" + new string('m', 200) + "\",");
            b.Materials[0] = b.Materials[0].Insert(1, "\"name\":\"mat\",");
            b.Images[0] = b.Images[0].Insert(1, "\"name\":\"img\",");
            b.Textures[0] = b.Textures[0].Insert(1, "\"name\":\"tex\",");
            BasisGlbValidationResult result = BasisGlbTestRun.Send(b);
            BasisGlbTestRun.AssertOk(result);
            string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
            Assert.That(json, Does.Not.Contain("name"));
            Assert.That(json, Does.Not.Contain("generator"));
            Assert.That(json, Does.Not.Contain("copyright"));
            StringAssert.StartsWith("{\"asset\":{\"version\":\"2.0\"}", json);
        }

        [Test]
        public void OutputContainsNoForbiddenKeys()
        {
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                BasisGlbValidationResult result = BasisGlbTestRun.Send(entry.Bytes, entry.Format);
                BasisGlbTestRun.AssertOk(result);
                string json = BasisGlbTestRun.JsonOf(result.CleanGlb);
                foreach (string forbidden in new[] { "\"uri\"", "\"extras\"", "\"animations\"", "\"cameras\"", "\"camera\"", "\"matrix\"",
                             "\"sparse\"", "\"targets\"", "\"weights\"", "\"mode\"", "\"minVersion\"", "\"byteOffset\":0", "\"name\"" })
                {
                    Assert.That(json, Does.Not.Contain(forbidden), entry.Name);
                }
                foreach (char c in json) Assert.That(c < 0x7F && c >= 0x20, Is.True, entry.Name + ": canonical JSON is printable ASCII");
            }
        }
    }
}
