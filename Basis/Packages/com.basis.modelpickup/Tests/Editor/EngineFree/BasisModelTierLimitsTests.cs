using System;
using System.Reflection;
using Basis.ModelPickup.Validation;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelTierLimitsTests
    {
        private const long MiB = 1024L * 1024L;
        private const long GiB = 1024L * MiB;

        // Tiers are passed by name: the enum is internal and NUnit needs public test methods.
        [TestCase(0, false, "Mid")]
        [TestCase(-1, false, "Mid")]
        [TestCase(1, false, "Mobile")]
        [TestCase(4096, false, "Mobile")]
        [TestCase(4097, false, "Mid")]
        [TestCase(8192, false, "Mid")]
        [TestCase(8193, false, "Desktop")]
        [TestCase(65536, false, "Desktop")]
        [TestCase(65536, true, "Mobile")]
        [TestCase(0, true, "Mobile")]
        public void TierResolution(int systemMemoryMegabytes, bool mobileGpu, string expectedTier)
        {
            BasisModelTier expected = Tier(expectedTier);
            Assert.That(BasisModelTierLimits.ResolveTier(systemMemoryMegabytes, mobileGpu), Is.EqualTo(expected));
            Assert.That(BasisModelTierLimits.ForDevice(systemMemoryMegabytes, mobileGpu).Tier, Is.EqualTo(expected));
        }

        [Test]
        public void UnknownMemoryGetsMidTierButDesktopPerModelLimits()
        {
            BasisModelTierLimits limits = BasisModelTierLimits.ForDevice(0, false);
            Assert.That(limits.Tier, Is.EqualTo(BasisModelTier.Mid));
            AssertSameLimits(BasisModelLimits.Desktop, limits.Validation);
        }

        [TestCase(-1, false)]
        [TestCase(0, false)]
        [TestCase(2048, false)]
        [TestCase(4096, false)]
        [TestCase(4097, false)]
        [TestCase(8192, false)]
        [TestCase(16384, false)]
        [TestCase(0, true)]
        [TestCase(16384, true)]
        public void TierValidationAgreesWithTheValidatorsDeviceRule(int systemMemoryMegabytes, bool mobileGpu)
        {
            AssertSameLimits(BasisModelLimits.ForDevice(mobileGpu, systemMemoryMegabytes),
                BasisModelTierLimits.ForDevice(systemMemoryMegabytes, mobileGpu).Validation);
        }

        [Test]
        public void DesktopTableIsPinned()
        {
            BasisModelTierLimits l = BasisModelTierLimits.Create(BasisModelTier.Desktop);
            Assert.That(l.Tier, Is.EqualTo(BasisModelTier.Desktop));
            AssertSameLimits(BasisModelLimits.Desktop, l.Validation);
            AssertSender(l.Sender, 8, 128 * MiB, 2000000, 64 * MiB, 768 * MiB, 1024, 2000000, 1000000, 4096);
            AssertResident(l.Resident, 64, 2 * GiB, 4096, 8000000, 4000000, 16384);
            Assert.That(l.InboundReservationBytes, Is.EqualTo(256 * MiB));
            Assert.That(l.WorkingSetBytes, Is.EqualTo(1 * GiB));
            Assert.That(l.MaxConcurrentWorkerJobs, Is.EqualTo(2));
            Assert.That(l.MaxConcurrentMainThreadJobs, Is.EqualTo(1));
            Assert.That(l.DeferBudgetMilliseconds, Is.EqualTo(3f));
            Assert.That(l.CastShadows, Is.True);
            Assert.That(l.RetainReceivedGlb, Is.True);
            Assert.That(l.MinSecondsBetweenImportsPerSender, Is.EqualTo(0f));
        }

        [Test]
        public void MidTableIsPinned()
        {
            BasisModelTierLimits l = BasisModelTierLimits.Create(BasisModelTier.Mid);
            Assert.That(l.Tier, Is.EqualTo(BasisModelTier.Mid));
            AssertSameLimits(BasisModelLimits.Desktop, l.Validation);
            AssertSender(l.Sender, 8, 96 * MiB, 1200000, 48 * MiB, 480 * MiB, 1024, 2000000, 1000000, 4096);
            AssertResident(l.Resident, 48, 1 * GiB, 4096, 8000000, 4000000, 16384);
            Assert.That(l.InboundReservationBytes, Is.EqualTo(192 * MiB));
            Assert.That(l.WorkingSetBytes, Is.EqualTo(512 * MiB));
            Assert.That(l.MaxConcurrentWorkerJobs, Is.EqualTo(2));
            Assert.That(l.MaxConcurrentMainThreadJobs, Is.EqualTo(1));
            Assert.That(l.DeferBudgetMilliseconds, Is.EqualTo(3f));
            Assert.That(l.CastShadows, Is.True);
            Assert.That(l.RetainReceivedGlb, Is.True);
            Assert.That(l.MinSecondsBetweenImportsPerSender, Is.EqualTo(0f));
        }

        [Test]
        public void MobileTableIsPinned()
        {
            BasisModelTierLimits l = BasisModelTierLimits.Create(BasisModelTier.Mobile);
            Assert.That(l.Tier, Is.EqualTo(BasisModelTier.Mobile));
            AssertSameLimits(BasisModelLimits.Mobile, l.Validation);
            AssertSender(l.Sender, 8, 48 * MiB, 450000, 24 * MiB, 192 * MiB, 256, 300000, 300000, 2048);
            AssertResident(l.Resident, 24, 384 * MiB, 512, 1000000, 500000, 4096);
            Assert.That(l.InboundReservationBytes, Is.EqualTo(96 * MiB));
            Assert.That(l.WorkingSetBytes, Is.EqualTo(256 * MiB));
            Assert.That(l.MaxConcurrentWorkerJobs, Is.EqualTo(1));
            Assert.That(l.MaxConcurrentMainThreadJobs, Is.EqualTo(1));
            Assert.That(l.DeferBudgetMilliseconds, Is.EqualTo(2f));
            Assert.That(l.CastShadows, Is.False);
            Assert.That(l.RetainReceivedGlb, Is.False);
            Assert.That(l.MinSecondsBetweenImportsPerSender, Is.EqualTo(5f));
        }

        [TestCase("Mobile")]
        [TestCase("Mid")]
        [TestCase("Desktop")]
        public void SenderRenderBudgetsAreAQuarterOfTheResidentOnesButNeverBelowOneModel(string tier)
        {
            BasisModelTierLimits l = BasisModelTierLimits.Create(Tier(tier));
            BasisModelLimits v = l.Validation;
            Assert.That(l.Sender.MaxDrawCalls, Is.EqualTo(Math.Max(l.Resident.MaxDrawCalls / 4, v.MaxDrawCalls)));
            Assert.That(l.Sender.MaxRenderedTriangles, Is.EqualTo(Math.Max(l.Resident.MaxRenderedTriangles / 4, v.MaxRenderedTriangles)));
            Assert.That(l.Sender.MaxSkinnedVertexInstances, Is.EqualTo(Math.Max(l.Resident.MaxSkinnedVertexInstances / 4, v.MaxSkinnedVertexInstances)));
            Assert.That(l.Sender.MaxNodes, Is.EqualTo(Math.Max(l.Resident.MaxNodes / 4, v.MaxNodes)));
        }

        [TestCase("Mobile")]
        [TestCase("Mid")]
        [TestCase("Desktop")]
        public void ASenderCanShareOneModelAtThePerModelLimits(string tier)
        {
            BasisModelTierLimits l = BasisModelTierLimits.Create(Tier(tier));
            BasisModelLimits v = l.Validation;
            var largest = new BasisGlbClaims
            {
                FormatVersion = 1,
                Nodes = (ushort)v.MaxNodes,
                Vertices = v.MaxVertices,
                DrawCalls = v.MaxDrawCalls,
                TexturePixels = v.MaxTotalTexturePixels,
                EstimatedDecodedBytes = v.MaxEstimatedDecodedBytes,
                RenderedTriangles = v.MaxRenderedTriangles,
                SkinnedVertexInstances = v.MaxSkinnedVertexInstances,
            };
            var total = new BasisModelAggregate();
            total.Add(largest, (int)v.MaxModelBytes);
            Assert.That(BasisModelBudget.IsWithinSenderLimits(total, l.Sender, out string reason), Is.True, reason);
            Assert.That(BasisModelBudget.IsWithinResidentLimits(total, l.Resident, out reason), Is.True, reason);
        }

        [Test]
        public void CreateReturnsAFreshInstance()
        {
            BasisModelTierLimits first = BasisModelTierLimits.Create(BasisModelTier.Desktop);
            first.Sender.MaxModels = 1;
            first.Validation.MaxModelBytes = 1;
            BasisModelTierLimits second = BasisModelTierLimits.Create(BasisModelTier.Desktop);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Sender.MaxModels, Is.EqualTo(8));
            Assert.That(second.Validation.MaxModelBytes, Is.EqualTo(32 * MiB));
            Assert.That(BasisModelLimits.Desktop.MaxModelBytes, Is.EqualTo(32 * MiB));
        }

        [Test]
        public void AnUnknownTierValueBuildsMid()
        {
            Assert.That(BasisModelTierLimits.Create((BasisModelTier)99).Tier, Is.EqualTo(BasisModelTier.Mid));
        }

        private static BasisModelTier Tier(string name)
        {
            return (BasisModelTier)System.Enum.Parse(typeof(BasisModelTier), name);
        }

        private static void AssertSender(in BasisModelSenderLimits s, int models, long bytes, long vertices, long texturePixels,
            long residentBytes, long drawCalls, long renderedTriangles, long skinnedVertices, long nodes)
        {
            Assert.That(s.MaxModels, Is.EqualTo(models), "MaxModels");
            Assert.That(s.MaxBytes, Is.EqualTo(bytes), "MaxBytes");
            Assert.That(s.MaxVertices, Is.EqualTo(vertices), "MaxVertices");
            Assert.That(s.MaxTexturePixels, Is.EqualTo(texturePixels), "MaxTexturePixels");
            Assert.That(s.MaxResidentBytes, Is.EqualTo(residentBytes), "MaxResidentBytes");
            Assert.That(s.MaxDrawCalls, Is.EqualTo(drawCalls), "MaxDrawCalls");
            Assert.That(s.MaxRenderedTriangles, Is.EqualTo(renderedTriangles), "MaxRenderedTriangles");
            Assert.That(s.MaxSkinnedVertexInstances, Is.EqualTo(skinnedVertices), "MaxSkinnedVertexInstances");
            Assert.That(s.MaxNodes, Is.EqualTo(nodes), "MaxNodes");
        }

        private static void AssertResident(in BasisModelResidentLimits r, int models, long residentBytes, long drawCalls,
            long renderedTriangles, long skinnedVertices, long nodes)
        {
            Assert.That(r.MaxModels, Is.EqualTo(models), "MaxModels");
            Assert.That(r.MaxResidentBytes, Is.EqualTo(residentBytes), "MaxResidentBytes");
            Assert.That(r.MaxDrawCalls, Is.EqualTo(drawCalls), "MaxDrawCalls");
            Assert.That(r.MaxRenderedTriangles, Is.EqualTo(renderedTriangles), "MaxRenderedTriangles");
            Assert.That(r.MaxSkinnedVertexInstances, Is.EqualTo(skinnedVertices), "MaxSkinnedVertexInstances");
            Assert.That(r.MaxNodes, Is.EqualTo(nodes), "MaxNodes");
        }

        private static void AssertSameLimits(BasisModelLimits expected, BasisModelLimits actual)
        {
            foreach (FieldInfo field in typeof(BasisModelLimits).GetFields(BindingFlags.Public | BindingFlags.Instance))
                Assert.That(field.GetValue(actual), Is.EqualTo(field.GetValue(expected)), field.Name);
        }
    }
}
