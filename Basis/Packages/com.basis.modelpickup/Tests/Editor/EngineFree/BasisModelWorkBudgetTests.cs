using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelWorkBudgetTests
    {
        private const long MiB = 1024L * 1024L;

        [TestCase(1)]
        [TestCase(2)]
        public void WorkerJobsAreCapped(int cap)
        {
            var budget = new BasisModelWorkBudget(cap, 1, 1024 * MiB);
            for (int i = 0; i < cap; i++)
                Assert.That(budget.TryBeginWorker(1), Is.True);
            Assert.That(budget.TryBeginWorker(1), Is.False);
            Assert.That(budget.ActiveWorkers, Is.EqualTo(cap));

            budget.EndWorker(1);
            Assert.That(budget.TryBeginWorker(1), Is.True);
        }

        [Test]
        public void MainThreadJobsAreSerialized()
        {
            var budget = new BasisModelWorkBudget(2, 1, 1024 * MiB);
            Assert.That(budget.TryBeginMainThread(0), Is.True);
            Assert.That(budget.TryBeginMainThread(0), Is.False);
            Assert.That(budget.TryBeginWorker(10), Is.True, "workers have their own slots");
            budget.EndMainThread(0);
            Assert.That(budget.TryBeginMainThread(0), Is.True);
        }

        [Test]
        public void AWorkingSetOverflowDefers()
        {
            var budget = new BasisModelWorkBudget(4, 1, 100);
            Assert.That(budget.TryBeginWorker(60), Is.True);
            Assert.That(budget.TryBeginWorker(50), Is.False);
            Assert.That(budget.TryBeginMainThread(41), Is.False, "the working set is shared by both kinds of job");
            Assert.That(budget.TryBeginWorker(40), Is.True);
            Assert.That(budget.ChargedBytes, Is.EqualTo(100));

            budget.EndWorker(60);
            Assert.That(budget.TryBeginWorker(50), Is.True);
        }

        [Test]
        public void AnOversizedJobRunsAloneWhenIdle()
        {
            var budget = new BasisModelWorkBudget(2, 1, 100);
            Assert.That(budget.TryBeginWorker(150), Is.True);
            Assert.That(budget.TryBeginWorker(1), Is.False, "nothing runs beside an oversized job");
            Assert.That(budget.TryBeginMainThread(0), Is.False, "not even a job that declares no charge");

            budget.EndWorker(150);
            Assert.That(budget.ChargedBytes, Is.EqualTo(0));
            Assert.That(budget.TryBeginWorker(1), Is.True);
            Assert.That(budget.TryBeginWorker(150), Is.False, "an oversized job waits until nothing else runs");
        }

        [Test]
        public void AnOversizedJobWaitsForChargeFreeMainThreadWork()
        {
            var budget = new BasisModelWorkBudget(2, 1, 100);
            Assert.That(budget.TryBeginMainThread(0), Is.True);
            Assert.That(budget.TryBeginWorker(150), Is.False);
            budget.EndMainThread(0);
            Assert.That(budget.TryBeginWorker(150), Is.True);
        }

        [Test]
        public void EndNeverGoesNegative()
        {
            var budget = new BasisModelWorkBudget(2, 1, 100);
            budget.EndWorker(10);
            budget.EndMainThread(10);
            Assert.That(budget.ActiveWorkers, Is.EqualTo(0));
            Assert.That(budget.ActiveMainThread, Is.EqualTo(0));
            Assert.That(budget.ChargedBytes, Is.EqualTo(0));

            Assert.That(budget.TryBeginWorker(30), Is.True);
            budget.EndWorker(500);
            Assert.That(budget.ChargedBytes, Is.EqualTo(0));
        }

        [Test]
        public void NegativeChargesCountAsZero()
        {
            var budget = new BasisModelWorkBudget(2, 1, 100);
            Assert.That(budget.TryBeginWorker(-1000), Is.True);
            Assert.That(budget.ChargedBytes, Is.EqualTo(0));
            Assert.That(budget.TryBeginWorker(100), Is.True);
            budget.EndWorker(-1000);
            Assert.That(budget.ChargedBytes, Is.EqualTo(100));
        }

        [Test]
        public void ResetClearsCounters()
        {
            var budget = new BasisModelWorkBudget(2, 1, 100);
            budget.TryBeginWorker(40);
            budget.TryBeginMainThread(20);
            budget.Reset();
            Assert.That(budget.ActiveWorkers, Is.EqualTo(0));
            Assert.That(budget.ActiveMainThread, Is.EqualTo(0));
            Assert.That(budget.ChargedBytes, Is.EqualTo(0));
            Assert.That(budget.MaxWorkerJobs, Is.EqualTo(2), "caps survive a reset");
        }

        [Test]
        public void ConfigureKeepsRunningWorkAndClampsCapsToOne()
        {
            var budget = new BasisModelWorkBudget(2, 1, 100);
            budget.TryBeginWorker(40);
            budget.Configure(0, -3, -5);
            Assert.That(budget.MaxWorkerJobs, Is.EqualTo(1));
            Assert.That(budget.MaxMainThreadJobs, Is.EqualTo(1));
            Assert.That(budget.WorkingSetBytes, Is.EqualTo(0));
            Assert.That(budget.ActiveWorkers, Is.EqualTo(1));
            Assert.That(budget.ChargedBytes, Is.EqualTo(40));
            Assert.That(budget.TryBeginWorker(1), Is.False);
        }

        [Test]
        public void EstimatorsMatchTheContract()
        {
            Assert.That(BasisModelWorkBudget.EstimatePrepareBytes(10 * MiB), Is.EqualTo(30 * MiB));
            Assert.That(BasisModelWorkBudget.EstimateFinishBytes(10 * MiB, 5 * MiB, 32 * MiB), Is.EqualTo(79 * MiB));
            Assert.That(BasisModelWorkBudget.EstimateReceiveBytes(7 * MiB), Is.EqualTo(14 * MiB));
        }

        [Test]
        public void EstimatorsNeverGoNegativeOrWrap()
        {
            Assert.That(BasisModelWorkBudget.EstimatePrepareBytes(-1), Is.EqualTo(0));
            Assert.That(BasisModelWorkBudget.EstimateReceiveBytes(-1), Is.EqualTo(0));
            Assert.That(BasisModelWorkBudget.EstimateFinishBytes(-1, -1, -1), Is.EqualTo(0));
            Assert.That(BasisModelWorkBudget.EstimatePrepareBytes(long.MaxValue / 2), Is.EqualTo(long.MaxValue));
            Assert.That(BasisModelWorkBudget.EstimateReceiveBytes(long.MaxValue), Is.EqualTo(long.MaxValue));
            Assert.That(BasisModelWorkBudget.EstimateFinishBytes(long.MaxValue, long.MaxValue, long.MaxValue), Is.EqualTo(long.MaxValue));
        }
    }
}
