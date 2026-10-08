using System.Collections.Generic;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    /// <summary>Player lookups, with no players present (edit mode).</summary>
    public sealed class BasisModelShareNetTests
    {
        private float _savedRange;

        [SetUp]
        public void SetUp()
        {
            _savedRange = BasisNetworkManagement.ServerMetaDataMessage.ImagePickupRangeMeters;
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkManagement.ServerMetaDataMessage.ImagePickupRangeMeters = _savedRange;
        }

        [Test]
        public void AnUnknownOwnerIsNamedByTheirIdNeverByTheWire()
        {
            Assert.That(BasisModelShareNet.ResolveOwnerName(60123), Is.EqualTo("Player 60123"));
        }

        [Test]
        public void WithoutALocalPlayerTheOwnerIsNobodyAndUnknown()
        {
            Assume.That(BasisLocalPlayer.Instance == null, "a local player exists in this editor session");
            Assert.That(BasisModelShareNet.LocalPlayerId(), Is.EqualTo(BasisModelShareNet.UnownedPlayerId));
            Assert.That(BasisModelShareNet.LocalOwnerName(), Is.EqualTo("Unknown"));
            Assert.That(BasisModelReplicationScan.TryGetLocalViewerPosition(out _), Is.False);
        }

        [Test]
        public void ADestroyedOrMissingPlayerHasNoPosition()
        {
            Assert.That(BasisModelReplicationScan.TryGetPlayerPosition(null, out Vector3 position), Is.False);
            Assert.That(position, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void GatheringStartsFromAnEmptyList()
        {
            var results = new List<BasisModelReplicationCandidate> { new BasisModelReplicationCandidate { PlayerId = 9 } };
            BasisModelReplicationScan.GatherCandidates(9, results);
            Assert.That(results, Is.Empty, "edit mode has no remote players");
        }

        [Test]
        public void SnapshotsConvertTheItemPosition()
        {
            var candidates = new List<BasisModelReplicationCandidate>
            {
                new BasisModelReplicationCandidate { PlayerId = 5, Position = new BasisModelVec3(0f, 0f, 3f) },
                new BasisModelReplicationCandidate { PlayerId = 4, Position = new BasisModelVec3(0f, 0f, 30f) },
                new BasisModelReplicationCandidate { PlayerId = 2, Position = new BasisModelVec3(0f, 0f, 1f) },
            };
            ushort[] eligible = BasisModelReplicationScan.SnapshotEligible(
                candidates,
                new Vector3(0f, 0f, 2f),
                5f,
                new HashSet<ushort>(),
                new List<ushort>()
            );
            Assert.That(eligible, Is.EqualTo(new ushort[] { 2, 5 }));
        }

        [Test]
        public void TheRangeIsReadFromTheServerAndNeverNegative()
        {
            var reporter = new BasisModelRangeReporter("Test pickup", "test", BasisDebug.LogTag.Pickups);

            BasisNetworkManagement.ServerMetaDataMessage.ImagePickupRangeMeters = 24f;
            Assert.That(reporter.ServerRangeMeters(), Is.EqualTo(24f));

            BasisNetworkManagement.ServerMetaDataMessage.ImagePickupRangeMeters = -3f;
            Assert.That(reporter.ServerRangeMeters(), Is.EqualTo(0f));

            reporter.AdvertisedRange = () => 7.5f;
            Assert.That(reporter.ServerRangeMeters(), Is.EqualTo(7.5f));

            reporter.Reset();
            Assert.That(reporter.ServerRangeMeters(), Is.EqualTo(7.5f));
        }

        [Test]
        public void APersistentSwitchRemembersItsValue()
        {
            const string key = "Basis.ModelPickup.Tests.PersistentBool";
            bool existed = PlayerPrefs.HasKey(key);
            int saved = PlayerPrefs.GetInt(key, 0);
            try
            {
                PlayerPrefs.DeleteKey(key);
                Assert.That(new BasisModelPersistentBool(key, true).Value, Is.True, "default when unset");
                Assert.That(new BasisModelPersistentBool(key, false).Value, Is.False, "default when unset");

                var toggle = new BasisModelPersistentBool(key, true);
                toggle.Value = false;
                Assert.That(toggle.Value, Is.False);
                Assert.That(PlayerPrefs.GetInt(key, 1), Is.EqualTo(0));
                Assert.That(new BasisModelPersistentBool(key, true).Value, Is.False, "a fresh reader sees the saved value");
                Assert.That(toggle.Key, Is.EqualTo(key));
            }
            finally
            {
                if (existed)
                    PlayerPrefs.SetInt(key, saved);
                else
                    PlayerPrefs.DeleteKey(key);
            }
        }
    }
}
