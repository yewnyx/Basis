using System.Collections.Generic;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelCapablePeersTests
    {
        private const ushort Local = 1;

        [Test]
        public void AHelloAddsThePeerOnce()
        {
            var peers = new BasisModelCapablePeers();
            Assert.That(peers.OnHello(5, Local, 1, false, true, out _, out bool added), Is.True);
            Assert.That(added, Is.True);
            Assert.That(peers.Contains(5), Is.True);
            Assert.That(peers.Count, Is.EqualTo(1));

            Assert.That(peers.OnHello(5, Local, 1, false, true, out _, out added), Is.True);
            Assert.That(added, Is.False, "a repeated hello is not a new peer");
            Assert.That(peers.Count, Is.EqualTo(1));
        }

        [Test]
        public void OurOwnEchoIsIgnored()
        {
            var peers = new BasisModelCapablePeers();
            Assert.That(peers.OnHello(Local, Local, 1, false, true, out bool sendReply, out bool added), Is.False);
            Assert.That(sendReply, Is.False);
            Assert.That(added, Is.False);
            Assert.That(peers.Count, Is.EqualTo(0));
        }

        [Test]
        public void VersionZeroIsIgnoredAndLaterVersionsAreCapable()
        {
            var peers = new BasisModelCapablePeers();
            Assert.That(peers.OnHello(5, Local, 0, false, true, out bool sendReply, out bool added), Is.False);
            Assert.That(sendReply, Is.False);
            Assert.That(added, Is.False);
            Assert.That(peers.Contains(5), Is.False);

            Assert.That(peers.OnHello(6, Local, 2, false, true, out _, out added), Is.True);
            Assert.That(added, Is.True);
        }

        [TestCase(false, true, true)]
        [TestCase(false, false, false)]
        [TestCase(true, true, false)]
        [TestCase(true, false, false)]
        public void OnlyAFirstContactHelloIsAnsweredAndOnlyWhileWeCanReceive(bool reply, bool localCapable, bool expectReply)
        {
            var peers = new BasisModelCapablePeers();
            peers.OnHello(9, Local, 1, reply, localCapable, out bool sendReply, out bool added);
            Assert.That(sendReply, Is.EqualTo(expectReply));
            Assert.That(added, Is.True, "the sender is capable whatever we can do");
        }

        [Test]
        public void RemoveAndClearForgetPeers()
        {
            var peers = new BasisModelCapablePeers();
            peers.OnHello(5, Local, 1, false, true, out _, out _);
            peers.OnHello(6, Local, 1, false, true, out _, out _);
            Assert.That(peers.Remove(5), Is.True);
            Assert.That(peers.Remove(5), Is.False);
            Assert.That(peers.Contains(5), Is.False);
            Assert.That(peers.Contains(6), Is.True);

            peers.Clear();
            Assert.That(peers.Count, Is.EqualTo(0));
            peers.OnHello(6, Local, 1, false, true, out _, out bool added);
            Assert.That(added, Is.True, "a peer forgotten on release is new again after the next hello");
        }

        [Test]
        public void FilterKeepsOnlyCapablePeersInOrder()
        {
            var peers = new BasisModelCapablePeers();
            foreach (ushort id in new ushort[] { 1, 3, 7, 42 })
                peers.OnHello(id, 100, 1, false, true, out _, out _);

            List<BasisModelReplicationCandidate> candidates = Candidates(5, 1, 9, 3, 7);
            int capacity = candidates.Capacity;
            peers.Filter(candidates);

            Assert.That(candidates.Count, Is.EqualTo(3));
            Assert.That(candidates[0].PlayerId, Is.EqualTo(1));
            Assert.That(candidates[1].PlayerId, Is.EqualTo(3));
            Assert.That(candidates[2].PlayerId, Is.EqualTo(7));
            Assert.That(candidates[1].Position.X, Is.EqualTo(3f), "each candidate keeps its own position");
            Assert.That(candidates.Capacity, Is.EqualTo(capacity));
        }

        [Test]
        public void FilterWithNoCapablePeersRemovesEveryone()
        {
            var peers = new BasisModelCapablePeers();
            List<BasisModelReplicationCandidate> candidates = Candidates(2, 4);
            peers.Filter(candidates);
            Assert.That(candidates.Count, Is.EqualTo(0));
            peers.Filter(null);
        }

        [Test]
        public void FilterKeepsAnAllCapableListUntouched()
        {
            var peers = new BasisModelCapablePeers();
            peers.OnHello(2, Local, 1, false, true, out _, out _);
            peers.OnHello(4, Local, 1, false, true, out _, out _);
            List<BasisModelReplicationCandidate> candidates = Candidates(4, 2);
            peers.Filter(candidates);
            Assert.That(candidates.Count, Is.EqualTo(2));
            Assert.That(candidates[0].PlayerId, Is.EqualTo(4));
            Assert.That(candidates[1].PlayerId, Is.EqualTo(2));
        }

        private static List<BasisModelReplicationCandidate> Candidates(params ushort[] ids)
        {
            var list = new List<BasisModelReplicationCandidate>(16);
            foreach (ushort id in ids)
                list.Add(new BasisModelReplicationCandidate { PlayerId = id, Position = new BasisModelVec3(id, 0f, 0f) });
            return list;
        }
    }
}
