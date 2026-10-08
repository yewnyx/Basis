using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    /// <summary>
    /// The validator must never throw and never report Internal for any input: Internal means a validator bug (or a
    /// failed sender self-check). Every accepted mutant must also be canonical-stable and admissible by its own claims.
    /// </summary>
    public class BasisGlbFuzzTests
    {
        private const int MutationsPerSeed = 500;
        private static readonly int[] Seeds = { 1, 2, 3, 4 };

        private sealed class Tally
        {
            public int Runs, Accepted;
            public readonly List<string> Problems = new List<string>();

            public void Check(string label, in BasisGlbValidationResult result, bool sender)
            {
                Runs++;
                if (result.ErrorKind == BasisGlbErrorKind.Internal || (!result.Ok && string.IsNullOrEmpty(result.Error)))
                {
                    if (Problems.Count < 20) Problems.Add(label + (sender ? " [send] " : " [receive] ") + result.Error);
                    return;
                }
                if (!result.Ok) return;
                Accepted++;
                string soundness = AcceptedIsSound(result);
                if (soundness != null && Problems.Count < 20) Problems.Add(label + (sender ? " [send] " : " [receive] ") + soundness);
            }
        }

        /// <summary>Accepted output re-validates as canonical with equal stats, and its own claims pass admission.</summary>
        private static string AcceptedIsSound(in BasisGlbValidationResult result)
        {
            BasisGlbValidationResult again = BasisGlbValidator.ValidateReceived(result.CleanGlb, BasisModelLimits.Desktop);
            if (!again.Ok) return "re-validation failed: " + again.Error;
            if (!again.InputWasCanonical) return "output is not canonical-stable";
            if (again.Stripped != BasisGlbStripped.None) return "output still has stripped content: " + again.Stripped;
            if (!BasisGlbStats.AreEqual(again.Stats, result.Stats)) return "stats changed on re-validation";
            BasisGlbClaims claims = BasisGlbClaims.FromStats(result.Stats);
            if (!claims.TryAdmit(result.CleanGlb.Length, BasisModelLimits.Desktop, out string admit)) return "own claims are not admissible: " + admit;
            if (!claims.Verify(again.Stats, out string verify)) return "own claims do not verify: " + verify;
            return null;
        }

        private static void Run(Tally tally, string label, byte[] data, BasisModelSourceFormat format)
        {
            if (format == BasisModelSourceFormat.Glb) tally.Check(label, BasisGlbTestRun.Receive(data), false);
            tally.Check(label, BasisGlbTestRun.Send(data, format), true);
        }

        private static byte[] Mutate(byte[] source, Random random)
        {
            int kind = random.Next(6);
            if (source.Length == 0) return new byte[] { (byte)random.Next(256) };
            int at = random.Next(source.Length);
            switch (kind)
            {
                case 0:
                {
                    var copy = (byte[])source.Clone();
                    copy[at] ^= (byte)(1 << random.Next(8));
                    return copy;
                }
                case 1:
                {
                    var copy = (byte[])source.Clone();
                    copy[at] = (byte)random.Next(256);
                    return copy;
                }
                case 2:
                {
                    var copy = new byte[source.Length + 1];
                    Buffer.BlockCopy(source, 0, copy, 0, at);
                    copy[at] = (byte)random.Next(256);
                    Buffer.BlockCopy(source, at, copy, at + 1, source.Length - at);
                    return copy;
                }
                case 3:
                {
                    var copy = new byte[source.Length - 1];
                    Buffer.BlockCopy(source, 0, copy, 0, at);
                    Buffer.BlockCopy(source, at + 1, copy, at, source.Length - at - 1);
                    return copy;
                }
                case 4:
                {
                    // Chunk-length and header tweaks.
                    var copy = (byte[])source.Clone();
                    if (copy.Length < 20) return copy;
                    int field = random.Next(3) == 0 ? 8 : 12;
                    if (random.Next(2) == 0 && copy.Length >= 28)
                    {
                        int jsonLength = BitConverter.ToInt32(copy, 12);
                        if (jsonLength > 0 && 20 + jsonLength + 4 <= copy.Length) field = 20 + jsonLength;
                    }
                    uint value = BitConverter.ToUInt32(copy, field);
                    uint[] deltas = { 1, 4, uint.MaxValue, uint.MaxValue - 3, 0x80000000 };
                    BitConverter.GetBytes(random.Next(4) == 0 ? (uint)random.Next() : value + deltas[random.Next(deltas.Length)]).CopyTo(copy, field);
                    return copy;
                }
                default:
                {
                    // Concentrate on the JSON chunk, where most structure lives.
                    var copy = (byte[])source.Clone();
                    int jsonLength = copy.Length >= 20 ? BitConverter.ToInt32(copy, 12) : 0;
                    if (jsonLength > 0 && 20 + jsonLength <= copy.Length)
                    {
                        int position = 20 + random.Next(jsonLength);
                        const string alphabet = "{}[],:\"0123456789-.eE+ tfnulrsa\\";
                        copy[position] = (byte)alphabet[random.Next(alphabet.Length)];
                    }
                    else
                    {
                        copy[at] = (byte)'{';
                    }
                    return copy;
                }
            }
        }

        [Test]
        public void ByteMutationsNeverThrowOrReportInternal()
        {
            var tally = new Tally();
            List<BasisGlbTestCorpus.Entry> corpus = BasisGlbTestCorpus.All();
            foreach (int seed in Seeds)
            {
                var random = new Random(seed);
                for (int i = 0; i < MutationsPerSeed; i++)
                {
                    foreach (BasisGlbTestCorpus.Entry entry in corpus)
                    {
                        Run(tally, entry.Name + " seed " + seed + " #" + i, Mutate(entry.Bytes, random), entry.Format);
                    }
                }
            }
            Assert.That(tally.Problems, Is.Empty, string.Join("\n", tally.Problems));
            Assert.That(tally.Runs, Is.GreaterThan(Seeds.Length * MutationsPerSeed * corpus.Count));
            // Roughly 9% of byte mutants are accepted (e.g. flips inside vertex data); each one is checked for soundness.
            Assert.That(tally.Accepted, Is.GreaterThan(1000), "accepted mutants: " + tally.Accepted + " of " + tally.Runs);
        }

        private static readonly string[] NumberReplacements = { "-1", "0", "2147483647", "4294967295", "1e39", "1.5", "-0", "3e38", "65536", "7" };
        private static readonly string[] ValueReplacements = { "\"x\"", "[]", "{}", "null", "true", "[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[[1]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]]" };
        private static readonly Regex NumberPattern = new Regex("-?[0-9]+(\\.[0-9]+)?([eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
        private static readonly Regex MemberPattern = new Regex("\"[A-Za-z_0-9]+\":(-?[0-9.eE+]+|\"[^\"]*\"|true|false)", RegexOptions.CultureInvariant);

        private static string MutateJson(string json, Random random)
        {
            switch (random.Next(4))
            {
                case 0:
                case 1:
                {
                    MatchCollection numbers = NumberPattern.Matches(json);
                    if (numbers.Count == 0) return json;
                    Match pick = numbers[random.Next(numbers.Count)];
                    string replacement = random.Next(3) == 0 ? ValueReplacements[random.Next(ValueReplacements.Length)] : NumberReplacements[random.Next(NumberReplacements.Length)];
                    return json.Substring(0, pick.Index) + replacement + json.Substring(pick.Index + pick.Length);
                }
                case 2:
                {
                    // Duplicate a member in place: {"a":1} → {"a":1,"a":1}.
                    MatchCollection members = MemberPattern.Matches(json);
                    if (members.Count == 0) return json;
                    Match pick = members[random.Next(members.Count)];
                    return json.Substring(0, pick.Index + pick.Length) + "," + pick.Value + json.Substring(pick.Index + pick.Length);
                }
                default:
                {
                    // Drop a member: {"a":1,"b":2} → {"a":1}.
                    MatchCollection members = MemberPattern.Matches(json);
                    if (members.Count == 0) return json;
                    Match pick = members[random.Next(members.Count)];
                    int start = pick.Index;
                    int end = pick.Index + pick.Length;
                    if (end < json.Length && json[end] == ',') end++;
                    else if (start > 0 && json[start - 1] == ',') start--;
                    return json.Substring(0, start) + json.Substring(end);
                }
            }
        }

        [Test]
        public void JsonMutationsNeverThrowOrReportInternal()
        {
            var tally = new Tally();
            var canonical = new List<KeyValuePair<string, byte[]>>();
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                if (entry.Format != BasisModelSourceFormat.Glb) continue;
                canonical.Add(new KeyValuePair<string, byte[]>(entry.Name + " (source)", entry.Bytes));
                BasisGlbValidationResult sent = BasisGlbTestRun.Send(entry.Bytes);
                BasisGlbTestRun.AssertOk(sent);
                canonical.Add(new KeyValuePair<string, byte[]>(entry.Name + " (canonical)", sent.CleanGlb));
            }
            foreach (int seed in Seeds)
            {
                var random = new Random(seed * 7919);
                for (int i = 0; i < MutationsPerSeed / 2; i++)
                {
                    foreach (KeyValuePair<string, byte[]> item in canonical)
                    {
                        string json = BasisGlbTestRun.JsonOf(item.Value);
                        string mutated = MutateJson(json, random);
                        if (random.Next(4) == 0) mutated = MutateJson(mutated, random);
                        byte[] glb = BasisGlbTestRun.WithJson(item.Value, mutated);
                        Run(tally, item.Key + " seed " + seed + " #" + i, glb, BasisModelSourceFormat.Glb);
                    }
                }
            }
            Assert.That(tally.Problems, Is.Empty, string.Join("\n", tally.Problems));
            Assert.That(tally.Accepted, Is.GreaterThan(1000), "benign value changes must be accepted, so soundness is exercised: "
                + tally.Accepted + " of " + tally.Runs);
        }

        [Test]
        public void AcceptedMutantsAreIdempotentAndWithinLimits()
        {
            // Benign mutations that should be accepted: every one must produce sound, canonical output.
            string[] benign =
            {
                "\"translation\":[1,2,3]", "\"scale\":[2,2,2]", "\"rotation\":[0,0,0.38268343,0.9238795]",
            };
            int accepted = 0;
            foreach (BasisGlbTestCorpus.Entry entry in BasisGlbTestCorpus.All())
            {
                if (entry.Format != BasisModelSourceFormat.Glb) continue;
                string json = BasisGlbTestRun.JsonOf(entry.Bytes);
                foreach (string member in benign)
                {
                    int node = json.IndexOf("\"nodes\":[{", StringComparison.Ordinal);
                    if (node < 0) continue;
                    int insert = node + "\"nodes\":[{".Length;
                    string mutated = json.Substring(0, insert) + member + "," + json.Substring(insert);
                    BasisGlbValidationResult result = BasisGlbTestRun.Receive(BasisGlbTestRun.WithJson(entry.Bytes, mutated));
                    if (!result.Ok)
                    {
                        Assert.That(result.ErrorKind, Is.Not.EqualTo(BasisGlbErrorKind.Internal), result.Error);
                        continue;
                    }
                    accepted++;
                    Assert.That(AcceptedIsSound(result), Is.Null, entry.Name + " + " + member);
                    BasisGlbStats stats = result.Stats;
                    BasisModelLimits limits = BasisModelLimits.Desktop;
                    Assert.That(stats.Vertices, Is.LessThanOrEqualTo(limits.MaxVertices));
                    Assert.That(stats.CanonicalBytes, Is.LessThanOrEqualTo(limits.MaxModelBytes));
                    Assert.That(stats.EstimatedDecodedBytes, Is.LessThanOrEqualTo(limits.MaxEstimatedDecodedBytes));
                }
            }
            Assert.That(accepted, Is.GreaterThan(10));
        }

        [Test]
        public void EveryTruncationOfTriangleIsRejectedCleanly()
        {
            foreach (byte[] full in new[] { BasisGlbTestBuilder.Triangle().BuildGlb(), BasisGlbTestBuilder.TexturedQuad(2, 2).BuildGlb() })
            {
                for (int length = 0; length < full.Length; length++)
                {
                    var truncated = new byte[length];
                    Buffer.BlockCopy(full, 0, truncated, 0, length);
                    BasisGlbValidationResult received = BasisGlbTestRun.Receive(truncated);
                    Assert.That(received.Ok, Is.False, "length " + length);
                    Assert.That(received.ErrorKind, Is.Not.EqualTo(BasisGlbErrorKind.Internal), "length " + length + ": " + received.Error);
                    BasisGlbValidationResult sent = BasisGlbTestRun.Send(truncated);
                    Assert.That(sent.Ok, Is.False, "length " + length);
                    Assert.That(sent.ErrorKind, Is.Not.EqualTo(BasisGlbErrorKind.Internal), "length " + length + ": " + sent.Error);
                }
                byte[] gltf = BasisGlbTestBuilder.Triangle().BuildGltfWithDataUris();
                for (int length = 0; length < gltf.Length; length += 3)
                {
                    var truncated = new byte[length];
                    Buffer.BlockCopy(gltf, 0, truncated, 0, length);
                    BasisGlbValidationResult sent = BasisGlbTestRun.Send(truncated, BasisModelSourceFormat.GltfJson);
                    Assert.That(sent.Ok, Is.False, "gltf length " + length);
                    Assert.That(sent.ErrorKind, Is.Not.EqualTo(BasisGlbErrorKind.Internal), "gltf length " + length + ": " + sent.Error);
                }
            }
        }

        [Test]
        public void ReceiverErrorsNeverContainInputBytes()
        {
            const string Marker = "ZZQ<b>";
            var cases = new List<KeyValuePair<string, BasisGlbTestBuilder>>();
            BasisGlbTestBuilder b;

            b = BasisGlbTestBuilder.Triangle();
            b.ExtraRootMembers = "\"" + Marker + "\":1,\"" + Marker + "\":2";
            cases.Add(Pair("duplicate key", b));

            b = BasisGlbTestBuilder.Triangle();
            b.ExtensionsUsed = "[\"" + Marker + "\"]";
            b.ExtensionsRequired = "[\"" + Marker + "\"]";
            cases.Add(Pair("required extension", b));

            b = BasisGlbTestBuilder.Triangle();
            b.ExtensionsRequired = "[\"" + Marker + "\"]";
            cases.Add(Pair("required but unused extension", b));

            b = BasisGlbTestBuilder.Triangle();
            b.BufferJson = "{\"byteLength\":36,\"uri\":\"" + Marker + "\"}";
            cases.Add(Pair("buffer uri", b));

            b = BasisGlbTestBuilder.TexturedQuad(2, 2);
            b.Images[0] = "{\"uri\":\"" + Marker + ".png\"}";
            cases.Add(Pair("image uri", b));

            b = BasisGlbTestBuilder.TexturedQuad(2, 2);
            b.Materials[0] = "{\"alphaMode\":\"" + Marker + "\"}";
            cases.Add(Pair("alpha mode", b));

            b = BasisGlbTestBuilder.TexturedQuad(2, 2);
            b.Images[0] = "{\"bufferView\":4,\"mimeType\":\"" + Marker + "\"}";
            cases.Add(Pair("mime type", b));

            b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"" + Marker + "\"}";
            cases.Add(Pair("accessor type", b));

            b = BasisGlbTestBuilder.Triangle();
            b.Accessors[0] = "{\"bufferView\":0,\"componentType\":5126,\"count\":\"" + Marker + "\",\"type\":\"VEC3\"}";
            cases.Add(Pair("wrong value type", b));

            b = BasisGlbTestBuilder.Triangle();
            b.Asset = "{\"version\":\"" + Marker + "\"}";
            cases.Add(Pair("asset version", b));

            b = BasisGlbTestBuilder.Triangle();
            b.Asset = "{\"version\":\"2.0\",\"minVersion\":\"" + Marker + "\"}";
            cases.Add(Pair("min version", b));

            b = BasisGlbTestBuilder.Triangle();
            b.ExtraRootMembers = "\"" + Marker + new string('k', 300) + "\":1";
            cases.Add(Pair("long key", b));

            b = BasisGlbTestBuilder.Triangle();
            b.ExtraRootMembers = "\"x\":\"" + Marker + "\u0001\"";
            cases.Add(Pair("control character in string", b));

            b = BasisGlbTestBuilder.Triangle();
            b.Nodes[0] = "{\"mesh\":0,\"" + Marker + "\":[1,]}";
            cases.Add(Pair("syntax error near marker", b));

            foreach (KeyValuePair<string, BasisGlbTestBuilder> c in cases)
            {
                BasisGlbValidationResult result = BasisGlbTestRun.Receive(c.Value);
                Assert.That(result.Ok, Is.False, c.Key);
                Assert.That(result.ErrorKind, Is.Not.EqualTo(BasisGlbErrorKind.Internal), c.Key);
                Assert.That(result.Error.IndexOf("ZZQ", StringComparison.Ordinal), Is.EqualTo(-1), c.Key + ": " + result.Error);
                Assert.That(result.Error.IndexOf('<'), Is.EqualTo(-1), c.Key + ": " + result.Error);
            }
        }

        private static KeyValuePair<string, BasisGlbTestBuilder> Pair(string name, BasisGlbTestBuilder builder)
        {
            return new KeyValuePair<string, BasisGlbTestBuilder>(name, builder);
        }
    }
}
