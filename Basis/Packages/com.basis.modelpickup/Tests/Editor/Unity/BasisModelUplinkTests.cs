using Basis.Scripts.Networking;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelUplinkTests
    {
        private int _savedEgressMegabits;

        [SetUp]
        public void SetUp()
        {
            _savedEgressMegabits = BasisNetworkManagement.ServerMetaDataMessage.ImageShareEgressMegabitsPerSecond;
            BasisModelUplink.ResetForNewConnection();
        }

        [TearDown]
        public void TearDown()
        {
            BasisNetworkManagement.ServerMetaDataMessage.ImageShareEgressMegabitsPerSecond = _savedEgressMegabits;
            BasisModelUplink.ResetForNewConnection();
        }

        [Test]
        public void BeginFrameRunsOncePerFrame()
        {
            BasisModelUplink.BeginFrame();
            Assert.That(BasisModelBandwidth.HasBegunFrame(Time.frameCount), Is.True);

            // Spend some budget, then a second tick in the same frame: no second refill.
            Assert.That(BasisModelBandwidth.TryConsume(1000, 0, 1), Is.True);
            double uplink = BasisModelBandwidth.UplinkTokens;
            double relay = BasisModelBandwidth.RelayTokens;
            BasisModelUplink.BeginFrame();
            Assert.That(BasisModelBandwidth.UplinkTokens, Is.EqualTo(uplink));
            Assert.That(BasisModelBandwidth.RelayTokens, Is.EqualTo(relay));
        }

        [Test]
        public void TheAdvertisedEgressIsReadInMegabits()
        {
            BasisNetworkManagement.ServerMetaDataMessage.ImageShareEgressMegabitsPerSecond = 8;
            BasisModelUplink.RefreshServerRelayBudget();
            Assert.That(BasisModelBandwidth.ServerRelayBudgetBytesPerSecond, Is.EqualTo(1_000_000L));

            BasisNetworkManagement.ServerMetaDataMessage.ImageShareEgressMegabitsPerSecond = 0;
            BasisModelUplink.RefreshServerRelayBudget();
            Assert.That(BasisModelBandwidth.ServerRelayBudgetBytesPerSecond, Is.EqualTo(0L));
        }

        [Test]
        public void ResetForNewConnectionForgetsTheLinkAndTheAdvertisedBudget()
        {
            BasisModelBandwidth.ServerRelayBudgetBytesPerSecond = 25_000_000L;
            BasisModelBandwidth.TryConsume(1 << 20, 1, 1);

            BasisModelUplink.ResetForNewConnection();

            Assert.That(BasisModelBandwidth.ServerRelayBudgetBytesPerSecond, Is.EqualTo(0L));
            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond)
            );
            Assert.That(BasisModelBandwidth.UplinkTokens, Is.EqualTo(BasisModelBandwidth.UplinkCapacityBytes));
            Assert.That(BasisModelBandwidth.RelayTokens, Is.EqualTo(BasisModelBandwidth.RelayCapacityBytes));
        }

        [Test]
        public void RecipientsWithoutAP2PSessionGoThroughTheRelay()
        {
            BasisModelUplink.CountRecipients(new ushort[] { 60001, 60002, 60003 }, out int direct, out int relay);
            Assert.That(direct, Is.EqualTo(0));
            Assert.That(relay, Is.EqualTo(3));

            BasisModelUplink.CountRecipients(null, out direct, out relay);
            Assert.That(direct + relay, Is.EqualTo(0));
        }

        [Test]
        public void ABulkPacketIsChargedWithItsSceneFraming()
        {
            ushort[] recipients = { 60001, 60002 };
            double before = BasisModelBandwidth.RelayTokens;
            Assert.That(BasisModelUplink.TryReserveSendBandwidth(100, recipients), Is.True);

            // Relayed: the packet plus framing (two u16 headers and one u16 per recipient), once per recipient.
            int wire = 100 + BasisNetworkGenericMessages.SceneDataFramingBytes(recipients);
            Assert.That(before - BasisModelBandwidth.RelayTokens, Is.EqualTo(wire * 2.0).Within(1e-6));
        }

        [Test]
        public void TheLinkProbeRestartsItsWindowWithoutAServerPeer()
        {
            Assert.That(BasisNetworkManagement.LocalPlayerPeer, Is.Null, "edit mode has no connection");
            BasisModelLinkProbe.Tick(10f);
            BasisModelLinkProbe.Tick(20f);
            Assert.That(
                BasisModelLinkProbe.DiscoveredUplinkBytesPerSecond,
                Is.EqualTo((float)BasisModelShareSettings.StartingUplinkBudgetBytesPerSecond)
            );
        }
    }
}
