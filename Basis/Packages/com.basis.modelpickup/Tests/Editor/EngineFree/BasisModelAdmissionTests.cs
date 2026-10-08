using System;
using Basis.ModelPickup.Validation;
using NUnit.Framework;
using static Basis.ModelPickup.Tests.BasisModelCoreTestData;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelAdmissionTests
    {
        private const int MiB = 1024 * 1024;

        private static BasisModelTierLimits Desktop()
        {
            return BasisModelTierLimits.Create(BasisModelTier.Desktop);
        }

        private static BasisModelAdmissionResult Evaluate(in BasisModelAdmissionInput input, BasisModelTierLimits limits, out string reason)
        {
            return BasisModelAdmission.Evaluate(input, limits, out reason);
        }

        /// <summary>Makes <paramref name="input"/> fail exactly the check behind <paramref name="failure"/>.</summary>
        private static void Break(ref BasisModelAdmissionInput input, BasisModelAdmissionResult failure, BasisModelTierLimits limits)
        {
            switch (failure)
            {
                case BasisModelAdmissionResult.ReceiveDisabled:
                    input.ReceiveEnabled = false;
                    break;
                case BasisModelAdmissionResult.Header:
                    input.HeaderOk = false;
                    input.HeaderError = "tail is 10 bytes; at least 96 are required";
                    break;
                case BasisModelAdmissionResult.Size:
                    input.TotalBytes = 0;
                    break;
                case BasisModelAdmissionResult.ChunkCount:
                    input.TotalChunks = 3;
                    break;
                case BasisModelAdmissionResult.Claims:
                    input.Tail.Claims.Vertices = limits.Validation.MaxVertices + 1;
                    break;
                case BasisModelAdmissionResult.Pose:
                    input.PoseValid = false;
                    break;
                case BasisModelAdmissionResult.SenderBudget:
                    input.SenderTotals.Count = limits.Sender.MaxModels;
                    break;
                case BasisModelAdmissionResult.ResidentBudget:
                    input.ResidentTotals.Count = limits.Resident.MaxModels;
                    break;
                case BasisModelAdmissionResult.TooManyTransfers:
                    input.ActiveTransfersFromSender = BasisModelAdmission.MaxInboundTransfersPerSender;
                    break;
                default:
                    Assert.Fail("no way to break " + failure);
                    break;
            }
        }

        [Test]
        public void AnAcceptableSpawnIsAccepted()
        {
            Assert.That(Evaluate(AcceptableInput(), Desktop(), out string reason), Is.EqualTo(BasisModelAdmissionResult.Accepted));
            Assert.That(reason, Is.Null);
        }

        // Results are passed as their byte values: the enum is internal and NUnit needs public test methods.
        [TestCase((byte)BasisModelAdmissionResult.ReceiveDisabled)]
        [TestCase((byte)BasisModelAdmissionResult.Header)]
        [TestCase((byte)BasisModelAdmissionResult.Size)]
        [TestCase((byte)BasisModelAdmissionResult.ChunkCount)]
        [TestCase((byte)BasisModelAdmissionResult.Claims)]
        [TestCase((byte)BasisModelAdmissionResult.Pose)]
        [TestCase((byte)BasisModelAdmissionResult.SenderBudget)]
        [TestCase((byte)BasisModelAdmissionResult.ResidentBudget)]
        [TestCase((byte)BasisModelAdmissionResult.TooManyTransfers)]
        public void EachFailureReturnsItsResult(byte failureValue)
        {
            var failure = (BasisModelAdmissionResult)failureValue;
            BasisModelTierLimits limits = Desktop();
            BasisModelAdmissionInput input = AcceptableInput();
            Break(ref input, failure, limits);
            Assert.That(Evaluate(input, limits, out string reason), Is.EqualTo(failure));
            Assert.That(reason, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void EarlierChecksWinOverLaterOnes()
        {
            BasisModelTierLimits limits = Desktop();
            var order = (BasisModelAdmissionResult[])Enum.GetValues(typeof(BasisModelAdmissionResult));
            for (int first = 1; first < order.Length; first++)
            {
                BasisModelAdmissionInput input = AcceptableInput();
                for (int later = first; later < order.Length; later++)
                    Break(ref input, order[later], limits);
                Assert.That(Evaluate(input, limits, out _), Is.EqualTo(order[first]), "with every check from " + order[first] + " on failing");
            }
        }

        [Test]
        public void TheHeaderErrorIsPassedThrough()
        {
            BasisModelAdmissionInput input = AcceptableInput();
            input.HeaderOk = false;
            input.HeaderError = "unsupported header version 2";
            Evaluate(input, Desktop(), out string reason);
            Assert.That(reason, Is.EqualTo("unsupported header version 2"));

            input.HeaderError = null;
            Evaluate(input, Desktop(), out reason);
            Assert.That(reason, Is.EqualTo("malformed spawn header"));
        }

        [Test]
        public void SizeUsesTheTiersValidationLimit()
        {
            BasisModelAdmissionInput input = AcceptableInput();
            input.TotalBytes = 16 * MiB + 1;
            input.TotalChunks = BasisModelShareWire.ExpectedChunkCount(input.TotalBytes, 16384);

            Assert.That(Evaluate(input, BasisModelTierLimits.Create(BasisModelTier.Mobile), out string reason),
                Is.EqualTo(BasisModelAdmissionResult.Size));
            StringAssert.Contains("16 MiB", reason);
            Assert.That(Evaluate(input, Desktop(), out reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);

            input.TotalBytes = 32 * MiB + 1;
            input.TotalChunks = BasisModelShareWire.ExpectedChunkCount(input.TotalBytes, 16384);
            Assert.That(Evaluate(input, Desktop(), out _), Is.EqualTo(BasisModelAdmissionResult.Size));

            input.TotalBytes = -5;
            Assert.That(Evaluate(input, Desktop(), out reason), Is.EqualTo(BasisModelAdmissionResult.Size));
            Assert.That(reason, Is.EqualTo("the model is empty"));
        }

        [TestCase(16384, 1, true)]
        [TestCase(16385, 2, true)]
        [TestCase(16385, 1, false)]
        [TestCase(16385, 3, false)]
        [TestCase(1000, 0, false)]
        [TestCase(1000, -1, false)]
        public void ChunkCountsUseSixteenKibibyteChunks(int totalBytes, int totalChunks, bool accepted)
        {
            BasisModelAdmissionInput input = AcceptableInput();
            input.TotalBytes = totalBytes;
            input.TotalChunks = totalChunks;
            Assert.That(Evaluate(input, Desktop(), out _),
                Is.EqualTo(accepted ? BasisModelAdmissionResult.Accepted : BasisModelAdmissionResult.ChunkCount));
        }

        [Test]
        public void ClaimsAreAdmittedAgainstTheTier()
        {
            BasisModelAdmissionInput input = AcceptableInput();
            input.Tail.Claims.Vertices = BasisModelLimits.Mobile.MaxVertices + 1;
            Assert.That(Evaluate(input, BasisModelTierLimits.Create(BasisModelTier.Mobile), out string reason),
                Is.EqualTo(BasisModelAdmissionResult.Claims));
            StringAssert.StartsWith("Vertex count", reason);
            Assert.That(Evaluate(input, Desktop(), out reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);
        }

        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        [TestCase(3, false)]
        public void TransfersPerSenderAreCappedAtTwo(int active, bool accepted)
        {
            BasisModelAdmissionInput input = AcceptableInput();
            input.ActiveTransfersFromSender = active;
            Assert.That(Evaluate(input, Desktop(), out _),
                Is.EqualTo(accepted ? BasisModelAdmissionResult.Accepted : BasisModelAdmissionResult.TooManyTransfers));
        }

        [TestCase("DrawCalls")]
        [TestCase("RenderedTriangles")]
        [TestCase("SkinnedVertexInstances")]
        [TestCase("Nodes")]
        public void ResidentRenderBudgetsRejectTheOverflowingModel(string budget)
        {
            BasisModelTierLimits limits = Desktop();
            BasisModelAdmissionInput input = CostsOneOfEachRenderBudget();
            long limit = ResidentLimit(limits, budget);
            // The candidate costs 1 of each, so a resident total of limit − 1 fits exactly and limit does not.
            SetTotal(ref input.ResidentTotals, budget, limit - 1);
            Assert.That(Evaluate(input, limits, out string reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);

            SetTotal(ref input.ResidentTotals, budget, limit);
            Assert.That(Evaluate(input, limits, out reason), Is.EqualTo(BasisModelAdmissionResult.ResidentBudget));
            StringAssert.StartsWith("resident", reason);
        }

        [TestCase("DrawCalls")]
        [TestCase("RenderedTriangles")]
        [TestCase("SkinnedVertexInstances")]
        [TestCase("Nodes")]
        public void SenderRenderBudgetsRejectTheOverflowingModel(string budget)
        {
            BasisModelTierLimits limits = BasisModelTierLimits.Create(BasisModelTier.Mobile);
            BasisModelAdmissionInput input = CostsOneOfEachRenderBudget();
            long limit = SenderLimit(limits, budget);
            SetTotal(ref input.SenderTotals, budget, limit - 1);
            Assert.That(Evaluate(input, limits, out string reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);

            SetTotal(ref input.SenderTotals, budget, limit);
            Assert.That(Evaluate(input, limits, out reason), Is.EqualTo(BasisModelAdmissionResult.SenderBudget));
            StringAssert.StartsWith("per-sender", reason);
        }

        [Test]
        public void TheCandidatesGlbCountsAgainstMemoryBudgets()
        {
            BasisModelTierLimits limits = Desktop();
            BasisModelAdmissionInput input = AcceptableInput();
            long candidate = input.Tail.Claims.EstimatedDecodedBytes + input.TotalBytes;

            input.SenderTotals.ResidentBytes = limits.Sender.MaxResidentBytes - candidate;
            Assert.That(Evaluate(input, limits, out string reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);
            input.SenderTotals.ResidentBytes++;
            Assert.That(Evaluate(input, limits, out _), Is.EqualTo(BasisModelAdmissionResult.SenderBudget));

            input = AcceptableInput();
            input.ResidentTotals.ResidentBytes = limits.Resident.MaxResidentBytes - candidate;
            Assert.That(Evaluate(input, limits, out reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);
            input.ResidentTotals.ResidentBytes++;
            Assert.That(Evaluate(input, limits, out _), Is.EqualTo(BasisModelAdmissionResult.ResidentBudget));
        }

        [Test]
        public void RequestedReplayBypassesRateAndTransferCaps()
        {
            BasisModelTierLimits limits = Desktop();
            BasisModelSpawnRateLimiter rate = BasisModelAdmission.CreateRateLimiter();
            const ushort sender = 4;
            for (int i = 0; i < 16; i++)
                Assert.That(rate.TryConsume(sender, 0f), Is.True);

            BasisModelAdmissionInput live = AcceptableInput();
            live.ActiveTransfersFromSender = BasisModelAdmission.MaxInboundTransfersPerSender;
            Assert.That(Evaluate(live, limits, out _), Is.EqualTo(BasisModelAdmissionResult.TooManyTransfers));
            live.ActiveTransfersFromSender = 0;
            Assert.That(BasisModelAdmission.TryTakeRateToken(live, rate, sender, 0f), Is.False, "the burst is spent");

            BasisModelAdmissionInput requested = AcceptableInput();
            requested.WasRequested = true;
            requested.ActiveTransfersFromSender = BasisModelAdmission.MaxInboundTransfersPerSender + 3;
            Assert.That(Evaluate(requested, limits, out string reason), Is.EqualTo(BasisModelAdmissionResult.Accepted), reason);
            Assert.That(BasisModelAdmission.TryTakeRateToken(requested, rate, sender, 0f), Is.True);
            Assert.That(BasisModelAdmission.TryTakeRateToken(live, rate, sender, 0f), Is.False, "a replay spends no token");
        }

        [Test]
        public void ARequestedReplayStillHonoursEverythingElse()
        {
            BasisModelTierLimits limits = Desktop();
            BasisModelAdmissionResult[] all = (BasisModelAdmissionResult[])Enum.GetValues(typeof(BasisModelAdmissionResult));
            foreach (BasisModelAdmissionResult failure in all)
            {
                if (failure == BasisModelAdmissionResult.Accepted || failure == BasisModelAdmissionResult.TooManyTransfers)
                    continue;
                BasisModelAdmissionInput input = AcceptableInput();
                input.WasRequested = true;
                Break(ref input, failure, limits);
                Assert.That(Evaluate(input, limits, out _), Is.EqualTo(failure));
            }
        }

        [Test]
        public void TheModelRateLimiterAllowsSixteenThenOneEveryTwoSeconds()
        {
            Assert.That(BasisModelAdmission.SpawnRateCapacity, Is.EqualTo(16f));
            Assert.That(BasisModelAdmission.SpawnRateIntervalSeconds, Is.EqualTo(2f));

            BasisModelSpawnRateLimiter rate = BasisModelAdmission.CreateRateLimiter();
            for (int i = 0; i < 16; i++)
                Assert.That(rate.TryConsume(1, 10f), Is.True);
            Assert.That(rate.TryConsume(1, 10f), Is.False);
            Assert.That(rate.TryConsume(1, 11.9f), Is.False);
            Assert.That(rate.TryConsume(1, 12f), Is.True);
            Assert.That(rate.TryConsume(2, 12f), Is.True, "senders are independent");
        }

        [Test]
        public void EvaluateNeedsLimits()
        {
            Assert.Throws<ArgumentNullException>(() => BasisModelAdmission.Evaluate(AcceptableInput(), null, out _));
        }

        [Test]
        public void AValidOfferIsParsed()
        {
            byte[] offer = OfferBytes(SmallTail(), 1000, 1, SamplePose());
            BasisModelSpawnHeader header = ReadHeader(offer);
            Assert.That(BasisModelAdmission.TryParseOffer(header, offer, Desktop(), out BasisModelOfferInfo info, out string reason), Is.True, reason);
            Assert.That(info.TotalBytes, Is.EqualTo(1000));
            Assert.That(info.ClaimedOwnerId, Is.EqualTo(SampleOwnerId));
            Assert.That(info.Tail.BaseScale, Is.EqualTo(0.5f));
            CollectionAssert.AreEqual(ClaimsBytes(SmallClaims()), ClaimsBytes(info.Tail.Claims));
        }

        [Test]
        public void OffersMeetTheSameRulesAsSpawns()
        {
            BasisModelTierLimits mobile = BasisModelTierLimits.Create(BasisModelTier.Mobile);

            AssertOfferRefused(OfferBytes(SmallTail(), 1000, 2, SamplePose()), mobile, "chunks");
            AssertOfferRefused(OfferBytes(SmallTail(), 16 * MiB + 1, 1025, SamplePose()), mobile, "MiB");

            BasisModelSpawnTail heavy = SmallTail();
            heavy.Claims.Vertices = BasisModelLimits.Mobile.MaxVertices + 1;
            AssertOfferRefused(OfferBytes(heavy, 1000, 1, SamplePose()), mobile, "Vertex count");

            var farAway = new BasisModelPose(new BasisModelVec3(0f, 0f, 2e5f), BasisModelQuat.Identity);
            AssertOfferRefused(OfferBytes(SmallTail(), 1000, 1, farAway), mobile, "pose");

            byte[] shortTail = RawSpawn(1, 40, new byte[40]);
            shortTail[0] = BasisModelShareWire.OpServerCacheOffer;
            AssertOfferRefused(shortTail, mobile, "tail is 40 bytes");
        }

        [Test]
        public void OfferBudgetsMatchSpawnBudgets()
        {
            BasisModelTierLimits limits = Desktop();
            var offer = new BasisModelOfferInfo { Tail = SmallTail(), TotalBytes = 1000, ClaimedOwnerId = 3 };
            var empty = new BasisModelAggregate();
            Assert.That(BasisModelAdmission.OfferFitsBudgets(offer, empty, empty, limits, out string reason), Is.True, reason);

            var fullResident = new BasisModelAggregate { Count = limits.Resident.MaxModels };
            Assert.That(BasisModelAdmission.OfferFitsBudgets(offer, empty, fullResident, limits, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("resident model count limit of 64"));

            var fullSender = new BasisModelAggregate { Count = limits.Sender.MaxModels };
            Assert.That(BasisModelAdmission.OfferFitsBudgets(offer, fullSender, empty, limits, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("per-sender model count limit of 8"));
        }

        private static BasisModelAdmissionInput CostsOneOfEachRenderBudget()
        {
            BasisModelAdmissionInput input = AcceptableInput();
            input.Tail.Claims.SkinnedVertexInstances = 1;
            Assert.That(input.Tail.Claims.DrawCalls, Is.EqualTo(1));
            Assert.That(input.Tail.Claims.RenderedTriangles, Is.EqualTo(1));
            Assert.That(input.Tail.Claims.Nodes, Is.EqualTo(1));
            return input;
        }

        private static void AssertOfferRefused(byte[] offer, BasisModelTierLimits limits, string reasonFragment)
        {
            BasisModelSpawnHeader header = ReadHeader(offer);
            Assert.That(BasisModelAdmission.TryParseOffer(header, offer, limits, out BasisModelOfferInfo info, out string reason), Is.False);
            StringAssert.Contains(reasonFragment, reason);
            Assert.That(info.TotalBytes, Is.EqualTo(0), "a refused offer leaves nothing behind");
        }

        private static byte[] OfferBytes(in BasisModelSpawnTail tail, int totalBytes, int totalChunks, in BasisModelPose pose)
        {
            byte[] message = BasisModelWire.EncodeSpawn(SampleId, SampleOwnerId, "Alice", totalBytes, totalChunks, pose, tail);
            message[0] = BasisModelShareWire.OpServerCacheOffer;
            return message;
        }

        private static long ResidentLimit(BasisModelTierLimits limits, string budget)
        {
            switch (budget)
            {
                case "DrawCalls": return limits.Resident.MaxDrawCalls;
                case "RenderedTriangles": return limits.Resident.MaxRenderedTriangles;
                case "SkinnedVertexInstances": return limits.Resident.MaxSkinnedVertexInstances;
                default: return limits.Resident.MaxNodes;
            }
        }

        private static long SenderLimit(BasisModelTierLimits limits, string budget)
        {
            switch (budget)
            {
                case "DrawCalls": return limits.Sender.MaxDrawCalls;
                case "RenderedTriangles": return limits.Sender.MaxRenderedTriangles;
                case "SkinnedVertexInstances": return limits.Sender.MaxSkinnedVertexInstances;
                default: return limits.Sender.MaxNodes;
            }
        }

        private static void SetTotal(ref BasisModelAggregate total, string budget, long value)
        {
            switch (budget)
            {
                case "DrawCalls": total.DrawCalls = value; break;
                case "RenderedTriangles": total.RenderedTriangles = value; break;
                case "SkinnedVertexInstances": total.SkinnedVertexInstances = value; break;
                default: total.Nodes = value; break;
            }
        }
    }
}
