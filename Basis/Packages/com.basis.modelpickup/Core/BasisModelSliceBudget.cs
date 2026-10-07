using System;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Time-slice arithmetic behind the import defer agent. The slice is measured from the agent's first query after
    /// <see cref="Arm"/>, not from the start of the frame, so other scripts' work earlier in the frame is not charged to
    /// the import; and that first query never defers, so every armed frame advances at least one import step even when
    /// the step is predicted to exceed the whole budget.
    /// </summary>
    public sealed class BasisModelSliceBudget
    {
        private readonly double _ticksPerSecond;
        private readonly double _budgetTicks;
        private bool _armed;
        private bool _started;
        private long _sliceStartTicks;

        public BasisModelSliceBudget(double budgetMilliseconds, long ticksPerSecond)
        {
            if (ticksPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            _ticksPerSecond = ticksPerSecond;
            // NaN and non-positive budgets mean one step per frame; an infinite one never defers while armed.
            _budgetTicks = budgetMilliseconds > 0d ? budgetMilliseconds * 0.001d * ticksPerSecond : 0d;
        }

        /// <summary>Starts a fresh slice. The driver calls this once per frame.</summary>
        public void Arm()
        {
            _armed = true;
            _started = false;
        }

        /// <summary>
        /// True to yield until next frame. Never armed means the driver is not ticking, and an import must not run
        /// unpaced on the main thread, so it always yields. A NaN or negative prediction counts as zero.
        /// </summary>
        public bool ShouldDefer(double predictedSeconds, long nowTicks)
        {
            if (!_armed)
                return true;
            if (!_started)
            {
                _started = true;
                _sliceStartTicks = nowTicks;
                return false;
            }
            double elapsed = Math.Max(0d, (double)nowTicks - _sliceStartTicks);
            double predicted = predictedSeconds > 0d ? predictedSeconds * _ticksPerSecond : 0d;
            return elapsed + predicted > _budgetTicks;
        }
    }
}
