using System.Diagnostics;
using System.Threading.Tasks;
using GLTFast;

namespace Basis.ModelPickup
{
    /// <summary>
    /// glTFast's per-frame time slice for model imports, armed by an explicit <see cref="BeginFrame"/> from the model
    /// manager's <c>BasisEventDriver</c> tick rather than a MonoBehaviour <c>Update</c>. glTFast's own default agent
    /// assumes 30 fps whenever <c>targetFrameRate</c> is unset (16.7 ms, more than a whole Quest frame) and shares one
    /// global GameObject; this one is a plain object with the tier's budget.
    ///
    /// The slice starts at the first query after <see cref="BeginFrame"/> (<see cref="BasisModelSliceBudget"/>), so the
    /// work of scripts that ran earlier in the frame is not charged to the import, and each frame advances at least one
    /// glTFast step. A yield resumes in the next frame: Unity runs continuations posted during its delayed-task pass in
    /// the following frame's pass.
    /// </summary>
    public sealed class BasisModelDeferAgent : IDeferAgent
    {
        private readonly BasisModelSliceBudget _slice;
        private readonly float _budgetSeconds;

        public BasisModelDeferAgent(float budgetMilliseconds)
        {
            _slice = new BasisModelSliceBudget(budgetMilliseconds, Stopwatch.Frequency);
            _budgetSeconds = budgetMilliseconds > 0f ? budgetMilliseconds * 0.001f : 0f;
        }

        /// <summary>Starts this frame's slice. Call once per frame, before any import step runs.</summary>
        public void BeginFrame()
        {
            _slice.Arm();
        }

        public bool ShouldDefer()
        {
            return _slice.ShouldDefer(0d, Stopwatch.GetTimestamp());
        }

        /// <summary>
        /// glTFast asks this only to choose between doing a step on the main thread now and handing it to a worker
        /// (JSON parse, base64 decode). Work predicted to outlast the whole slice always goes to the worker: on the
        /// main thread it would stall the frame even as the slice's guaranteed first step.
        /// </summary>
        public bool ShouldDefer(float duration)
        {
            if (duration > _budgetSeconds)
                return true;
            return _slice.ShouldDefer(duration, Stopwatch.GetTimestamp());
        }

        public Task BreakPoint()
        {
            return ShouldDefer() ? YieldOnce() : Task.CompletedTask;
        }

        /// <summary>
        /// Uses the slice alone, never the worker rule above: a step predicted to outlast the budget must still run as
        /// some frame's first step, or it would yield forever.
        /// </summary>
        public Task BreakPoint(float duration)
        {
            return _slice.ShouldDefer(duration, Stopwatch.GetTimestamp()) ? YieldOnce() : Task.CompletedTask;
        }

        private static async Task YieldOnce()
        {
            await Task.Yield();
        }
    }
}
