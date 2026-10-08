using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public sealed class BasisModelSpawnRateLimiterTests
    {
        [Test]
        public void ANewSenderStartsWithAFullBurst()
        {
            var limiter = new BasisModelSpawnRateLimiter(3f, 1f);

            Assert.That(limiter.TryConsume(1, 10f), Is.True);
            Assert.That(limiter.TryConsume(1, 10f), Is.True);
            Assert.That(limiter.TryConsume(1, 10f), Is.True);
            Assert.That(limiter.TryConsume(1, 10f), Is.False);
            Assert.That(limiter.TrackedSenders, Is.EqualTo(1));
        }

        [Test]
        public void TokensRefillAtTheConfiguredInterval()
        {
            var limiter = new BasisModelSpawnRateLimiter(3f, 2f);
            for (int i = 0; i < 3; i++)
                limiter.TryConsume(1, 10f);

            Assert.That(limiter.TryConsume(1, 11f), Is.False, "half a token");
            Assert.That(limiter.TryConsume(1, 12f), Is.True, "a whole token");
            Assert.That(limiter.TryConsume(1, 12f), Is.False);

            // A long silence banks no more than the burst.
            Assert.That(limiter.TryConsume(1, 1000f), Is.True);
            Assert.That(limiter.TryConsume(1, 1000f), Is.True);
            Assert.That(limiter.TryConsume(1, 1000f), Is.True);
            Assert.That(limiter.TryConsume(1, 1000f), Is.False);
        }

        [Test]
        public void AClockGoingBackwardsEarnsNothing()
        {
            var limiter = new BasisModelSpawnRateLimiter(1f, 1f);
            Assert.That(limiter.TryConsume(1, 50f), Is.True);

            Assert.That(limiter.TryConsume(1, 10f), Is.False);
            Assert.That(limiter.TryConsume(1, 10.5f), Is.False);
            Assert.That(limiter.TryConsume(1, 11f), Is.True);
        }

        [Test]
        public void ZeroIntervalMeansUnlimited()
        {
            var limiter = new BasisModelSpawnRateLimiter(1f, 0f);
            for (int i = 0; i < 100; i++)
                Assert.That(limiter.TryConsume(1, 0f), Is.True);
            Assert.That(limiter.TrackedSenders, Is.EqualTo(0));
        }

        [Test]
        public void ARemovedSenderStartsFullAgain()
        {
            var limiter = new BasisModelSpawnRateLimiter(2f, 10f);
            limiter.TryConsume(4, 0f);
            limiter.TryConsume(4, 0f);
            limiter.TryConsume(5, 0f);
            Assert.That(limiter.TryConsume(4, 0f), Is.False);

            limiter.Remove(4);

            Assert.That(limiter.TryConsume(4, 0f), Is.True);
            Assert.That(limiter.TryConsume(4, 0f), Is.True);
            Assert.That(limiter.TrackedSenders, Is.EqualTo(2));

            limiter.Clear();
            Assert.That(limiter.TrackedSenders, Is.EqualTo(0));
        }

        [Test]
        public void SendersAreIndependent()
        {
            var limiter = new BasisModelSpawnRateLimiter(1f, 10f);

            Assert.That(limiter.TryConsume(1, 0f), Is.True);
            Assert.That(limiter.TryConsume(2, 0f), Is.True);
            Assert.That(limiter.TryConsume(1, 0f), Is.False);
        }
    }
}
