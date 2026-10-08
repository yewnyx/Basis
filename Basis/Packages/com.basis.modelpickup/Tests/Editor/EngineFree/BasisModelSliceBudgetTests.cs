using System;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelSliceBudgetTests
    {
        // One tick per millisecond, so tick values read as milliseconds.
        private const long TicksPerSecond = 1000;

        [Test]
        public void AnUnarmedSliceAlwaysDefers()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            Assert.That(slice.ShouldDefer(0, 0), Is.True);
            Assert.That(slice.ShouldDefer(0, 1000), Is.True);
        }

        [Test]
        public void TheFirstQueryAfterArmNeverDefers()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            Assert.That(slice.ShouldDefer(10.0, 0), Is.False, "a step predicted to take longer than the budget still runs once");
            Assert.That(slice.ShouldDefer(10.0, 0), Is.True);
        }

        [Test]
        public void TheSliceStartsAtTheFirstQueryAfterArm()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            // Other scripts ran for a second before the import asked; that time is not charged.
            Assert.That(slice.ShouldDefer(0, 1000), Is.False);
            Assert.That(slice.ShouldDefer(0, 1002), Is.False);
            Assert.That(slice.ShouldDefer(0, 1003), Is.False, "exactly at the budget is still inside it");
            Assert.That(slice.ShouldDefer(0, 1004), Is.True);
        }

        [Test]
        public void ItDefersOnceTheBudgetIsSpent()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            slice.ShouldDefer(0, 0);
            Assert.That(slice.ShouldDefer(0, 5), Is.True);
            Assert.That(slice.ShouldDefer(0, 6), Is.True);
        }

        [Test]
        public void APredictionBeyondTheRemainderDefers()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            slice.ShouldDefer(0, 0);
            Assert.That(slice.ShouldDefer(0.001, 1), Is.False, "1 ms spent + 1 ms predicted fits 3 ms");
            Assert.That(slice.ShouldDefer(0.0025, 1), Is.True, "1 ms spent + 2.5 ms predicted does not");
        }

        [Test]
        public void ReArmingStartsAFreshSlice()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            slice.ShouldDefer(0, 0);
            Assert.That(slice.ShouldDefer(0, 10), Is.True);

            slice.Arm();
            Assert.That(slice.ShouldDefer(0, 20), Is.False);
            Assert.That(slice.ShouldDefer(0, 22), Is.False);
            Assert.That(slice.ShouldDefer(0, 24), Is.True);
        }

        [TestCase(double.NaN)]
        [TestCase(-1.0)]
        [TestCase(double.NegativeInfinity)]
        public void UnusablePredictionsCountAsZero(double predicted)
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            slice.ShouldDefer(0, 0);
            Assert.That(slice.ShouldDefer(predicted, 1), Is.False);
        }

        [TestCase(0.0)]
        [TestCase(-2.0)]
        [TestCase(double.NaN)]
        public void AnEmptyBudgetAdvancesOneStepPerFrame(double budgetMilliseconds)
        {
            var slice = new BasisModelSliceBudget(budgetMilliseconds, TicksPerSecond);
            slice.Arm();
            Assert.That(slice.ShouldDefer(0, 0), Is.False);
            Assert.That(slice.ShouldDefer(0, 1), Is.True);
            slice.Arm();
            Assert.That(slice.ShouldDefer(0, 2), Is.False);
        }

        [Test]
        public void AClockThatStepsBackwardsDoesNotExtendTheSlice()
        {
            var slice = new BasisModelSliceBudget(3, TicksPerSecond);
            slice.Arm();
            slice.ShouldDefer(0, 100);
            Assert.That(slice.ShouldDefer(0, 50), Is.False);
            Assert.That(slice.ShouldDefer(0, 104), Is.True);
        }

        [TestCase(0L)]
        [TestCase(-1L)]
        public void ANonPositiveTickRateIsRefused(long ticksPerSecond)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BasisModelSliceBudget(3, ticksPerSecond));
        }
    }
}
