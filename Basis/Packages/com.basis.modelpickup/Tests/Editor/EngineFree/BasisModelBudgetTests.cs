using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelBudgetTests
    {
        private static BasisGlbClaims Claims(long decodedBytes)
        {
            return new BasisGlbClaims
            {
                FormatVersion = 1,
                Nodes = 4,
                Vertices = 300,
                TexturePixels = 4096,
                EstimatedDecodedBytes = decodedBytes,
                DrawCalls = 2,
                RenderedTriangles = 150,
                SkinnedVertexInstances = 40,
            };
        }

        [Test]
        public void ResidentTotalsIncludeRetainedGlbBytes()
        {
            var total = new BasisModelAggregate();
            total.Add(Claims(1000), 500);
            Assert.That(total.ResidentBytes, Is.EqualTo(1500));
            Assert.That(total.Bytes, Is.EqualTo(500));
            Assert.That(total.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADroppedGlbIsNotCountedAsResident()
        {
            var total = new BasisModelAggregate();
            total.Add(Claims(1000), 500, retainsGlb: false);
            Assert.That(total.ResidentBytes, Is.EqualTo(1000));
            Assert.That(total.Bytes, Is.EqualTo(500), "wire bytes still count toward the sender's download budget");
        }

        [Test]
        public void AddSumsEveryField()
        {
            var total = new BasisModelAggregate();
            total.Add(Claims(1000), 500);
            total.Add(Claims(2000), 700);
            Assert.That(total.Count, Is.EqualTo(2));
            Assert.That(total.Bytes, Is.EqualTo(1200));
            Assert.That(total.Vertices, Is.EqualTo(600));
            Assert.That(total.TexturePixels, Is.EqualTo(8192));
            Assert.That(total.ResidentBytes, Is.EqualTo(4200));
            Assert.That(total.DrawCalls, Is.EqualTo(4));
            Assert.That(total.RenderedTriangles, Is.EqualTo(300));
            Assert.That(total.SkinnedVertexInstances, Is.EqualTo(80));
            Assert.That(total.Nodes, Is.EqualTo(8));
        }

        [Test]
        public void AddSaturatesInsteadOfWrapping()
        {
            var total = new BasisModelAggregate
            {
                Count = int.MaxValue,
                ResidentBytes = long.MaxValue - 10,
                TexturePixels = long.MaxValue,
            };
            BasisGlbClaims claims = Claims(long.MaxValue);
            claims.TexturePixels = long.MaxValue;
            total.Add(claims, int.MaxValue);
            Assert.That(total.Count, Is.EqualTo(int.MaxValue));
            Assert.That(total.ResidentBytes, Is.EqualTo(long.MaxValue));
            Assert.That(total.TexturePixels, Is.EqualTo(long.MaxValue));

            BasisModelSenderLimits sender = BasisModelTierLimits.Create(BasisModelTier.Desktop).Sender;
            Assert.That(BasisModelBudget.IsWithinSenderLimits(total, sender, out string reason), Is.False);
            StringAssert.DoesNotContain("invalid", reason);
        }

        [Test]
        public void NegativeInputsAddNothing()
        {
            var total = new BasisModelAggregate();
            BasisGlbClaims claims = Claims(-1000);
            claims.Vertices = -5;
            claims.RenderedTriangles = -7;
            total.Add(claims, -500);
            Assert.That(total.Count, Is.EqualTo(1));
            Assert.That(total.Bytes, Is.EqualTo(0));
            Assert.That(total.ResidentBytes, Is.EqualTo(0));
            Assert.That(total.Vertices, Is.EqualTo(0));
            Assert.That(total.RenderedTriangles, Is.EqualTo(0));
        }

        [TestCase("Count", "per-sender model count limit of 8")]
        [TestCase("Bytes", "per-sender download limit of 128 MiB (134,217,728 bytes)")]
        [TestCase("Vertices", "per-sender vertex limit of 2,000,000")]
        [TestCase("TexturePixels", "per-sender texture pixel limit of 67,108,864")]
        [TestCase("ResidentBytes", "per-sender memory limit of 768 MiB (805,306,368 bytes)")]
        [TestCase("DrawCalls", "per-sender draw call limit of 1,024")]
        [TestCase("RenderedTriangles", "per-sender rendered triangle limit of 2,000,000")]
        [TestCase("SkinnedVertexInstances", "per-sender skinned vertex limit of 1,000,000")]
        [TestCase("Nodes", "per-sender node limit of 4,096")]
        public void SenderLimitsAcceptTheBoundaryAndRejectOneOver(string field, string expectedReason)
        {
            BasisModelSenderLimits limits = BasisModelTierLimits.Create(BasisModelTier.Desktop).Sender;
            BasisModelAggregate atLimit = SenderAtLimit(limits);
            Assert.That(BasisModelBudget.IsWithinSenderLimits(atLimit, limits, out string reason), Is.True, reason);
            Assert.That(reason, Is.Null);

            BasisModelAggregate over = Bump(atLimit, field, 1);
            Assert.That(BasisModelBudget.IsWithinSenderLimits(over, limits, out reason), Is.False);
            Assert.That(reason, Is.EqualTo(expectedReason));
        }

        [TestCase("Count", "resident model count limit of 64")]
        [TestCase("ResidentBytes", "resident memory limit of 2048 MiB (2,147,483,648 bytes)")]
        [TestCase("DrawCalls", "resident draw call limit of 4,096")]
        [TestCase("RenderedTriangles", "resident rendered triangle limit of 8,000,000")]
        [TestCase("SkinnedVertexInstances", "resident skinned vertex limit of 4,000,000")]
        [TestCase("Nodes", "resident node limit of 16,384")]
        public void ResidentLimitsAcceptTheBoundaryAndRejectOneOver(string field, string expectedReason)
        {
            BasisModelResidentLimits limits = BasisModelTierLimits.Create(BasisModelTier.Desktop).Resident;
            var atLimit = new BasisModelAggregate
            {
                Count = limits.MaxModels,
                ResidentBytes = limits.MaxResidentBytes,
                DrawCalls = limits.MaxDrawCalls,
                RenderedTriangles = limits.MaxRenderedTriangles,
                SkinnedVertexInstances = limits.MaxSkinnedVertexInstances,
                Nodes = limits.MaxNodes,
                // Not resident budgets: no amount of these fails the resident check.
                Bytes = long.MaxValue,
                Vertices = long.MaxValue,
                TexturePixels = long.MaxValue,
            };
            Assert.That(BasisModelBudget.IsWithinResidentLimits(atLimit, limits, out string reason), Is.True, reason);

            BasisModelAggregate over = Bump(atLimit, field, 1);
            Assert.That(BasisModelBudget.IsWithinResidentLimits(over, limits, out reason), Is.False);
            Assert.That(reason, Is.EqualTo(expectedReason));
        }

        [TestCase("Count")]
        [TestCase("Bytes")]
        [TestCase("Vertices")]
        [TestCase("ResidentBytes")]
        [TestCase("Nodes")]
        public void PredicatesRejectNegativeTotals(string field)
        {
            BasisModelTierLimits limits = BasisModelTierLimits.Create(BasisModelTier.Desktop);
            BasisModelAggregate negative = Bump(new BasisModelAggregate(), field, -1);
            Assert.That(BasisModelBudget.IsWithinSenderLimits(negative, limits.Sender, out string reason), Is.False);
            StringAssert.EndsWith("total is invalid", reason);
        }

        [Test]
        public void AnEmptyAggregateFitsEveryTier()
        {
            foreach (BasisModelTier tier in new[] { BasisModelTier.Mobile, BasisModelTier.Mid, BasisModelTier.Desktop })
            {
                BasisModelTierLimits limits = BasisModelTierLimits.Create(tier);
                Assert.That(BasisModelBudget.IsWithinSenderLimits(new BasisModelAggregate(), limits.Sender, out _), Is.True);
                Assert.That(BasisModelBudget.IsWithinResidentLimits(new BasisModelAggregate(), limits.Resident, out _), Is.True);
            }
        }

        [TestCase(0, 0, 8, 8)]
        [TestCase(3, 2, 8, 3)]
        [TestCase(8, 0, 8, 0)]
        [TestCase(0, 8, 8, 0)]
        [TestCase(6, 5, 8, 0)]
        [TestCase(-3, -1, 8, 8)]
        [TestCase(0, 0, 0, 0)]
        [TestCase(0, 0, -1, 0)]
        [TestCase(int.MaxValue, int.MaxValue, 8, 0)]
        public void LocalSlotsCountOwnedModelsAndEveryInFlightJob(int owned, int inFlight, int max, int expected)
        {
            Assert.That(BasisModelBudget.AvailableLocalSlots(owned, inFlight, max), Is.EqualTo(expected));
        }

        private static BasisModelAggregate SenderAtLimit(in BasisModelSenderLimits limits)
        {
            return new BasisModelAggregate
            {
                Count = limits.MaxModels,
                Bytes = limits.MaxBytes,
                Vertices = limits.MaxVertices,
                TexturePixels = limits.MaxTexturePixels,
                ResidentBytes = limits.MaxResidentBytes,
                DrawCalls = limits.MaxDrawCalls,
                RenderedTriangles = limits.MaxRenderedTriangles,
                SkinnedVertexInstances = limits.MaxSkinnedVertexInstances,
                Nodes = limits.MaxNodes,
            };
        }

        private static BasisModelAggregate Bump(BasisModelAggregate total, string field, int delta)
        {
            switch (field)
            {
                case "Count": total.Count += delta; break;
                case "Bytes": total.Bytes += delta; break;
                case "Vertices": total.Vertices += delta; break;
                case "TexturePixels": total.TexturePixels += delta; break;
                case "ResidentBytes": total.ResidentBytes += delta; break;
                case "DrawCalls": total.DrawCalls += delta; break;
                case "RenderedTriangles": total.RenderedTriangles += delta; break;
                case "SkinnedVertexInstances": total.SkinnedVertexInstances += delta; break;
                case "Nodes": total.Nodes += delta; break;
                default: Assert.Fail("unknown field " + field); break;
            }
            return total;
        }
    }
}
