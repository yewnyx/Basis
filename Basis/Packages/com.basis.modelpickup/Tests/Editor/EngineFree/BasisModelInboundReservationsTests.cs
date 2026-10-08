using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelInboundReservationsTests
    {
        private const long Limit = 1000;

        [SetUp]
        public void SetUp()
        {
            BasisModelInboundReservations.ReleaseAll();
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelInboundReservations.ReleaseAll();
        }

        [Test]
        public void FitsNeverOverflowsAndRefusesBadInputs()
        {
            const long limit = 512L * 1024L * 1024L;
            Assert.That(BasisModelInboundReservations.Fits(0, 1, limit), Is.True);
            Assert.That(BasisModelInboundReservations.Fits(limit - 1, 1, limit), Is.True);
            Assert.That(BasisModelInboundReservations.Fits(limit, 1, limit), Is.False);
            Assert.That(BasisModelInboundReservations.Fits(limit - 1, 2, limit), Is.False);
            Assert.That(BasisModelInboundReservations.Fits(-1, 1, limit), Is.False);
            Assert.That(BasisModelInboundReservations.Fits(0, long.MaxValue, limit), Is.False);
        }

        [Test]
        public void TheLimitCapsEveryTransferTogether()
        {
            Assert.That(BasisModelInboundReservations.TryReserve(600, Limit, out _), Is.True);
            Assert.That(BasisModelInboundReservations.TryReserve(401, Limit, out string reason), Is.False);
            Assert.That(reason, Is.EqualTo(BasisModelInboundReservations.BudgetReason));
            Assert.That(BasisModelInboundReservations.TryReserve(400, Limit, out reason), Is.True);
            Assert.That(reason, Is.Null);
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(Limit));
        }

        [Test]
        public void ReleaseNeverGoesNegative()
        {
            BasisModelInboundReservations.TryReserve(300, Limit, out _);

            BasisModelInboundReservations.Release(200);
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(100));
            BasisModelInboundReservations.Release(1_000_000);
            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0));
            BasisModelInboundReservations.Release(1);

            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(0));
        }

        [Test]
        public void NonPositiveCandidatesAreRefused()
        {
            Assert.That(BasisModelInboundReservations.TryReserve(0, Limit, out _), Is.False);
            Assert.That(BasisModelInboundReservations.TryReserve(-5, Limit, out _), Is.False);
            BasisModelInboundReservations.TryReserve(50, Limit, out _);
            BasisModelInboundReservations.Release(-5);
            BasisModelInboundReservations.Release(0);

            Assert.That(BasisModelInboundReservations.Reserved, Is.EqualTo(50));
        }
    }
}
