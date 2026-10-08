using System.Collections.Generic;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelReplicationTests
    {
        private static readonly BasisModelVec3 Origin = new BasisModelVec3(0f, 0f, 0f);

        [Test]
        public void ReplicationRangeUsesInclusiveRadiusAndZeroMeansUnlimited()
        {
            var image = new BasisModelVec3(10f, 2f, -3f);
            Assert.That(
                BasisModelReplication.IsWithinRange(image, Offset(image, 64f), 64f),
                Is.True
            );
            Assert.That(
                BasisModelReplication.IsWithinRange(image, Offset(image, 64.01f), 64f),
                Is.False
            );
            Assert.That(
                BasisModelReplication.IsWithinRange(image, Offset(image, 10000f), 0f),
                Is.True
            );
            Assert.That(
                BasisModelReplication.IsWithinRange(image, Offset(image, 10000f), -1f),
                Is.True
            );
        }

        [Test]
        public void RecipientSnapshotsKeepCatchupTransfersIndependent()
        {
            ushort[] initial = { 2, 5, 9 };
            ushort[] sameInitial = { 2, 5, 9 };
            ushort[] catchup = { 10 };
            Assert.That(BasisModelReplication.SnapshotsMatch(initial, sameInitial), Is.True);
            Assert.That(BasisModelReplication.SnapshotsMatch(initial, catchup), Is.False);
            Assert.That(
                BasisModelReplication.SnapshotsMatch(initial, new ushort[] { 9, 5, 2 }),
                Is.False
            );
            Assert.That(BasisModelReplication.SnapshotsMatch(null, null), Is.True);
            Assert.That(BasisModelReplication.SnapshotsMatch(initial, null), Is.False);
        }

        [Test]
        public void RemovingRecipientFromSnapshotPreservesOtherRecipients()
        {
            ushort[] original = { 2, 5, 9 };
            CollectionAssert.AreEqual(
                new ushort[] { 2, 9 },
                BasisModelReplication.RemoveRecipient(original, 5)
            );
            CollectionAssert.IsEmpty(
                BasisModelReplication.RemoveRecipient(new ushort[] { 5 }, 5)
            );
            Assert.That(
                BasisModelReplication.RemoveRecipient(original, 99),
                Is.SameAs(original)
            );
            CollectionAssert.IsEmpty(BasisModelReplication.RemoveRecipient(null, 5));
            CollectionAssert.AreEqual(new ushort[] { 2, 5, 9 }, original);
        }

        private static List<BasisModelReplicationCandidate> Candidates(params (ushort Id, BasisModelVec3 Position)[] entries)
        {
            var candidates = new List<BasisModelReplicationCandidate>();
            foreach ((ushort id, BasisModelVec3 position) in entries)
            {
                candidates.Add(new BasisModelReplicationCandidate { PlayerId = id, Position = position });
            }
            return candidates;
        }

        [Test]
        public void EligibleRecipientsAreRangeFilteredAndSorted()
        {
            List<BasisModelReplicationCandidate> candidates = Candidates(
                (9, new BasisModelVec3(1f, 0f, 0f)),
                (2, new BasisModelVec3(0f, 0f, 63f)),
                (5, new BasisModelVec3(0f, 0f, 65f))
            );
            var results = new List<ushort>();

            BasisModelReplication.SelectEligible(candidates, Origin, 64f, null, results);

            CollectionAssert.AreEqual(new ushort[] { 2, 9 }, results);
        }

        [Test]
        public void EligibleRecipientsSkipPlayersAlreadyServedWithoutMutatingTheSet()
        {
            List<BasisModelReplicationCandidate> candidates = Candidates(
                (2, Origin),
                (5, Origin),
                (9, Origin)
            );
            var alreadySent = new HashSet<ushort> { 5 };
            var results = new List<ushort>();

            BasisModelReplication.SelectEligible(candidates, Origin, 64f, alreadySent, results);

            CollectionAssert.AreEqual(new ushort[] { 2, 9 }, results);
            // The selection is a candidate list, not a commitment: a cohort that never queues must not
            // leave players recorded as served.
            CollectionAssert.AreEqual(new ushort[] { 5 }, new List<ushort>(alreadySent));
        }

        [Test]
        public void EligibleRecipientsIgnoreRangeWhenTheServerSendsZero()
        {
            List<BasisModelReplicationCandidate> candidates = Candidates(
                (3, new BasisModelVec3(0f, 0f, 10000f))
            );
            var results = new List<ushort>();

            BasisModelReplication.SelectEligible(candidates, Origin, 0f, null, results);

            CollectionAssert.AreEqual(new ushort[] { 3 }, results);
        }

        [Test]
        public void SelectionClearsStaleResultsAndToleratesNoCandidates()
        {
            var results = new List<ushort> { 1, 2, 3 };

            BasisModelReplication.SelectEligible(null, Origin, 64f, null, results);

            CollectionAssert.IsEmpty(results);
        }

        [Test]
        public void MarkSentAddsEveryRecipient()
        {
            var sent = new HashSet<ushort> { 1 };

            BasisModelReplication.MarkSent(sent, new ushort[] { 4, 1, 7 });
            BasisModelReplication.MarkSent(sent, null);
            BasisModelReplication.MarkSent(null, new ushort[] { 9 });

            Assert.That(sent.Count, Is.EqualTo(3));
            Assert.That(sent.Contains(4) && sent.Contains(1) && sent.Contains(7), Is.True);
        }

        [Test]
        public void SnapshotIsANewSortedArray()
        {
            List<BasisModelReplicationCandidate> candidates = Candidates(
                (8, Origin),
                (3, Origin),
                (6, new BasisModelVec3(100f, 0f, 0f))
            );
            var scratch = new List<ushort>();

            ushort[] first = BasisModelReplication.SnapshotEligible(candidates, Origin, 10f, null, scratch);
            ushort[] second = BasisModelReplication.SnapshotEligible(candidates, Origin, 10f, null, scratch);

            CollectionAssert.AreEqual(new ushort[] { 3, 8 }, first);
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(BasisModelReplication.SnapshotsMatch(first, second), Is.True);
        }

        private static BasisModelVec3 Offset(in BasisModelVec3 origin, float x)
        {
            return new BasisModelVec3(origin.X + x, origin.Y, origin.Z);
        }
    }
}
