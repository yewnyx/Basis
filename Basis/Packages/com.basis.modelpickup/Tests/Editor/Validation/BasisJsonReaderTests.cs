using System.Collections.Generic;
using System.Text;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests.Validation
{
    public class BasisJsonReaderTests
    {
        private static readonly BasisJsonReaderLimits Limits = new BasisJsonReaderLimits
        {
            MaxDepth = 32,
            MaxTokens = 1000,
            MaxKeyBytes = 16,
            MaxObjectKeys = 8,
            MaxNumberChars = 24,
        };

        private static BasisJsonReader Reader(string json, bool glbMode = false)
        {
            byte[] data = Encoding.UTF8.GetBytes(json);
            return new BasisJsonReader(data, 0, data.Length, Limits, glbMode);
        }

        private static BasisJsonReader Reader(byte[] data, bool glbMode = false)
        {
            return new BasisJsonReader(data, 0, data.Length, Limits, glbMode);
        }

        /// <summary>Reads to the end; returns null on success, else the error.</summary>
        private static string ReadAll(BasisJsonReader reader)
        {
            while (true)
            {
                if (!reader.Read()) return reader.Error;
                if (reader.Kind == BasisJsonTokenKind.End) return null;
            }
        }

        [Test]
        public void ParsesNestedValuesInDocumentOrder()
        {
            BasisJsonReader r = Reader("{\"a\":[1,true,null,\"s\\n\"],\"b\":{\"c\":-2.5e3},\"d\":false}");
            var kinds = new List<BasisJsonTokenKind>();
            while (r.Read() && r.Kind != BasisJsonTokenKind.End) kinds.Add(r.Kind);
            Assert.That(r.Error, Is.Null);
            CollectionAssert.AreEqual(new[]
            {
                BasisJsonTokenKind.BeginObject, BasisJsonTokenKind.PropertyName, BasisJsonTokenKind.BeginArray, BasisJsonTokenKind.Number,
                BasisJsonTokenKind.True, BasisJsonTokenKind.Null, BasisJsonTokenKind.String, BasisJsonTokenKind.EndArray,
                BasisJsonTokenKind.PropertyName, BasisJsonTokenKind.BeginObject, BasisJsonTokenKind.PropertyName, BasisJsonTokenKind.Number,
                BasisJsonTokenKind.EndObject, BasisJsonTokenKind.PropertyName, BasisJsonTokenKind.False, BasisJsonTokenKind.EndObject,
            }, kinds);

            r = Reader("{\"b\":{\"c\":-2.5e3},\"s\":\"x\\u00e9\\ud83d\\ude00\"}");
            Assert.That(r.Read() && r.Read() && r.PropertyNameIs("b"), Is.True);
            Assert.That(r.Read() && r.Read() && r.PropertyNameIs("c") && r.Read(), Is.True);
            Assert.That(r.TryGetSingle(out float value) && value == -2500f, Is.True);
            Assert.That(r.Read() && r.Kind == BasisJsonTokenKind.EndObject, Is.True);
            Assert.That(r.Read() && r.PropertyNameIs("s") && r.Read(), Is.True);
            Assert.That(r.TryGetString(64, out string text), Is.True);
            Assert.That(text, Is.EqualTo("x\u00e9\U0001F600"));
        }

        [Test]
        public void RejectsDuplicateKeysIncludingEscapedSpellings()
        {
            StringAssert.Contains("repeats a key", ReadAll(Reader("{\"a\":1,\"a\":2}")));
            StringAssert.Contains("repeats a key", ReadAll(Reader("{\"a\":1,\"\\u0061\":2}")));
            Assert.That(ReadAll(Reader("{\"a\":1,\"b\":{\"a\":2}}")), Is.Null, "the same key in different objects is fine");
        }

        [Test]
        public void RejectsDuplicateKeysInsideSkippedExtras()
        {
            BasisJsonReader r = Reader("{\"extras\":{\"deep\":[{\"k\":1,\"k\":2}]}}");
            Assert.That(r.Read() && r.Read() && r.PropertyNameIs("extras") && r.Read(), Is.True);
            Assert.That(r.TrySkipValue(), Is.False);
            StringAssert.Contains("repeats a key", r.Error);
        }

        [TestCase("{\"a\":1 /* c */}")]
        [TestCase("{'a':1}")]
        [TestCase("{a:1}")]
        [TestCase("[1,]")]
        [TestCase("{\"a\":1,}")]
        public void RejectsNonStandardSyntax(string json)
        {
            Assert.That(ReadAll(Reader(json)), Is.Not.Null);
        }

        [TestCase("NaN")]
        [TestCase("Infinity")]
        [TestCase("01")]
        [TestCase("0x1F")]
        [TestCase("+1")]
        [TestCase(".5")]
        [TestCase("1.")]
        public void RejectsNonStandardNumbers(string number)
        {
            Assert.That(ReadAll(Reader("[" + number + "]")), Is.Not.Null);
        }

        [TestCase(new byte[] { 0x22, 0xC0, 0xAF, 0x22 })]
        [TestCase(new byte[] { 0x22, 0xED, 0xA0, 0x80, 0x22 })]
        [TestCase(new byte[] { 0x22, 0xF4, 0x90, 0x80, 0x80, 0x22 })]
        [TestCase(new byte[] { 0x22, 0xE2, 0x82 })]
        [TestCase(new byte[] { 0x22, 0x80, 0x22 })]
        public void RejectsInvalidUtf8(byte[] data)
        {
            Assert.That(BasisJsonReader.ValidateUtf8(data, 0, data.Length, out int offset), Is.False);
            Assert.That(offset, Is.GreaterThanOrEqualTo(0));
            byte[] valid = Encoding.UTF8.GetBytes("\"é€😀\"");
            Assert.That(BasisJsonReader.ValidateUtf8(valid, 0, valid.Length, out _), Is.True);
        }

        [Test]
        public void RejectsUnpairedSurrogateEscapes()
        {
            StringAssert.Contains("surrogate", ReadAll(Reader("[\"\\ud83d\"]")));
            StringAssert.Contains("surrogate", ReadAll(Reader("[\"\\ude00\"]")));
            StringAssert.Contains("surrogate", ReadAll(Reader("[\"\\ud83d\\u0041\"]")));
            Assert.That(ReadAll(Reader("[\"\\ud83d\\ude00\"]")), Is.Null);
        }

        [Test]
        public void RejectsRawControlCharactersInStrings()
        {
            StringAssert.Contains("control character", ReadAll(Reader("[\"a\tb\"]")));
            StringAssert.Contains("control character", ReadAll(Reader(new byte[] { 0x5B, 0x22, 0x01, 0x22, 0x5D })));
            Assert.That(ReadAll(Reader("[\"a\\tb\"]")), Is.Null);
        }

        [Test]
        public void DepthBombIsRejectedWithoutRecursion()
        {
            var data = new byte[100000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)'[';
            BasisJsonReader r = Reader(data);
            string error = ReadAll(r);
            StringAssert.Contains("32", error);
            Assert.That(r.ErrorIsLimit, Is.True);
        }

        [Test]
        public void RejectsTokenCountBeyondLimit()
        {
            var json = new StringBuilder("[");
            for (int i = 0; i < 1200; i++) json.Append(i == 0 ? "0" : ",0");
            json.Append(']');
            BasisJsonReader r = Reader(json.ToString());
            StringAssert.Contains("tokens", ReadAll(r));
            Assert.That(r.ErrorIsLimit, Is.True);
        }

        [Test]
        public void RejectsOverlongKeysNumbersAndTooManyObjectKeys()
        {
            StringAssert.Contains("longer than 16 bytes", ReadAll(Reader("{\"" + new string('k', 17) + "\":1}")));
            Assert.That(ReadAll(Reader("{\"" + new string('k', 16) + "\":1}")), Is.Null);
            StringAssert.Contains("longer than 24 characters", ReadAll(Reader("[" + new string('1', 25) + "]")));
            var keys = new StringBuilder("{");
            for (int i = 0; i < 9; i++) keys.Append(i == 0 ? "" : ",").Append("\"k").Append(i).Append("\":0");
            keys.Append('}');
            StringAssert.Contains("more than 8 keys", ReadAll(Reader(keys.ToString())));
        }

        [Test]
        public void TrailingSpacesAndUpToThreeNulsAreAcceptedOnlyInGlbMode()
        {
            Assert.That(ReadAll(Reader("{}   ")), Is.Null);
            byte[] nul3 = { (byte)'{', (byte)'}', (byte)' ', 0, 0, 0 };
            byte[] nul4 = { (byte)'{', (byte)'}', 0, 0, 0, 0 };
            byte[] nulThenSpace = { (byte)'{', (byte)'}', 0, (byte)' ' };
            Assert.That(ReadAll(Reader(nul3, true)), Is.Null);
            Assert.That(ReadAll(Reader(nul3, false)), Is.Not.Null);
            Assert.That(ReadAll(Reader(nul4, true)), Is.Not.Null);
            Assert.That(ReadAll(Reader(nulThenSpace, true)), Is.Not.Null);
            Assert.That(ReadAll(Reader("{} x")), Is.Not.Null);
        }

        [TestCase("1.0")]
        [TestCase("1e2")]
        [TestCase("-1")]
        [TestCase("2147483648")]
        public void IntegerGrammarRejectsFractionsExponentsAndNegatives(string number)
        {
            BasisJsonReader r = Reader("[" + number + "]");
            Assert.That(r.Read() && r.Read(), Is.True);
            Assert.That(r.TryGetNonNegativeInt32(out _), Is.False);
        }

        [Test]
        public void IntegerGrammarAcceptsTheFullRange()
        {
            BasisJsonReader r = Reader("[0,2147483647,4294967295]");
            Assert.That(r.Read() && r.Read() && r.TryGetNonNegativeInt32(out int zero) && zero == 0, Is.True);
            Assert.That(r.Read() && r.TryGetNonNegativeInt32(out int max) && max == int.MaxValue, Is.True);
            Assert.That(r.Read() && !r.TryGetNonNegativeInt32(out _) && r.TryGetUInt32(out uint umax) && umax == uint.MaxValue, Is.True);
        }

        [TestCase("1e39")]
        [TestCase("1e400")]
        [TestCase("-3.5e38")]
        public void SingleRejectsFloatOverflow(string number)
        {
            BasisJsonReader r = Reader("[" + number + "]");
            Assert.That(r.Read() && r.Read(), Is.True);
            Assert.That(r.TryGetSingle(out _), Is.False);
        }

        [Test]
        public void SingleNormalisesNegativeZero()
        {
            BasisJsonReader r = Reader("[-0.0]");
            Assert.That(r.Read() && r.Read(), Is.True);
            Assert.That(r.TryGetSingle(out float value), Is.True);
            Assert.That(System.BitConverter.SingleToInt32Bits(value), Is.EqualTo(0));
        }

        [Test]
        public void SkipValueSkipsBoundedSubtrees()
        {
            BasisJsonReader r = Reader("{\"skip\":{\"a\":[1,{\"b\":[[]]}],\"c\":\"d\"},\"keep\":7}");
            Assert.That(r.Read() && r.Read() && r.PropertyNameIs("skip") && r.Read(), Is.True);
            Assert.That(r.TrySkipValue(), Is.True, r.Error);
            Assert.That(r.Depth, Is.EqualTo(1));
            Assert.That(r.Read() && r.PropertyNameIs("keep") && r.Read(), Is.True);
            Assert.That(r.TryGetNonNegativeInt32(out int keep), Is.True);
            Assert.That(keep, Is.EqualTo(7));
            Assert.That(r.Read() && r.Kind == BasisJsonTokenKind.EndObject && r.Read() && r.Kind == BasisJsonTokenKind.End, Is.True);
        }

        [Test]
        public void ErrorsCarryOffsetsButNeverInputText()
        {
            string error = ReadAll(Reader("{\"ZZQ<b>\":1,\"ZZQ<b>\":2}"));
            StringAssert.Contains("byte", error);
            Assert.That(error, Does.Not.Contain("ZZQ"));
        }
    }
}
