using System;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Concurrency and working-set scheduler for model jobs. A stage starts only when a slot and enough of the working
    /// set are free; otherwise it stays queued and is retried next frame. A job larger than the whole working set
    /// starts only when nothing else is running, so it is never starved and never runs beside another heavy job.
    /// Charges are estimates of peak transient memory, released when the stage's task completes.
    /// </summary>
    public sealed class BasisModelWorkBudget
    {
        public int MaxWorkerJobs;
        public int MaxMainThreadJobs;
        public long WorkingSetBytes;

        public int ActiveWorkers;
        public int ActiveMainThread;
        public long ChargedBytes;

        public BasisModelWorkBudget(int maxWorkerJobs, int maxMainThreadJobs, long workingSetBytes)
        {
            Configure(maxWorkerJobs, maxMainThreadJobs, workingSetBytes);
        }

        /// <summary>Changes the caps without touching running work. A cap below one would never run anything, so it becomes one.</summary>
        public void Configure(int maxWorkerJobs, int maxMainThreadJobs, long workingSetBytes)
        {
            MaxWorkerJobs = Math.Max(1, maxWorkerJobs);
            MaxMainThreadJobs = Math.Max(1, maxMainThreadJobs);
            WorkingSetBytes = Math.Max(0L, workingSetBytes);
        }

        public bool TryBeginWorker(long workingBytes)
        {
            if (ActiveWorkers >= MaxWorkerJobs || !FitsWorkingSet(workingBytes))
                return false;
            ActiveWorkers++;
            ChargedBytes += Math.Max(0L, workingBytes);
            return true;
        }

        public void EndWorker(long workingBytes)
        {
            ActiveWorkers = Math.Max(0, ActiveWorkers - 1);
            Release(workingBytes);
        }

        public bool TryBeginMainThread(long workingBytes)
        {
            if (ActiveMainThread >= MaxMainThreadJobs || !FitsWorkingSet(workingBytes))
                return false;
            ActiveMainThread++;
            ChargedBytes += Math.Max(0L, workingBytes);
            return true;
        }

        public void EndMainThread(long workingBytes)
        {
            ActiveMainThread = Math.Max(0, ActiveMainThread - 1);
            Release(workingBytes);
        }

        public void Reset()
        {
            ActiveWorkers = 0;
            ActiveMainThread = 0;
            ChargedBytes = 0;
        }

        /// <summary>Sender Prepare: the source, its decoded data URIs, and the parsed document.</summary>
        public static long EstimatePrepareBytes(long sourceBytes)
        {
            return SaturatingMultiply(sourceBytes, 3);
        }

        /// <summary>Sender Finish: the source and sanitised images, the canonical output and its self-check copy.</summary>
        public static long EstimateFinishBytes(long sourceBytes, long sanitizedBytes, long maxModelBytes)
        {
            return SaturatingAdd(SaturatingAdd(Math.Max(0L, sourceBytes), Math.Max(0L, sanitizedBytes)),
                SaturatingMultiply(maxModelBytes, 2));
        }

        /// <summary>Receiver validation: the received bytes and one re-emitted canonical copy.</summary>
        public static long EstimateReceiveBytes(long wireBytes)
        {
            return SaturatingMultiply(wireBytes, 2);
        }

        private bool FitsWorkingSet(long workingBytes)
        {
            long bytes = Math.Max(0L, workingBytes);
            if (bytes <= WorkingSetBytes - ChargedBytes)
                return true;
            return ActiveWorkers == 0 && ActiveMainThread == 0 && ChargedBytes == 0;
        }

        private void Release(long workingBytes)
        {
            ChargedBytes = Math.Max(0L, ChargedBytes - Math.Max(0L, workingBytes));
        }

        private static long SaturatingMultiply(long value, long factor)
        {
            if (value <= 0)
                return 0;
            return value > long.MaxValue / factor ? long.MaxValue : value * factor;
        }

        private static long SaturatingAdd(long a, long b)
        {
            return a > long.MaxValue - b ? long.MaxValue : a + b;
        }
    }
}
