using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Jobs;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Every tracked pickup root in one <see cref="TransformAccessArray"/>, run through
    /// <see cref="BasisModelFollowJob"/> once per tick: followers eased, controllers due to send sampled, with no
    /// per-pickup transform call on the main thread.
    ///
    /// Invariants: root i is the root of the manager's dense model slot i, because <see cref="Add"/> and
    /// <see cref="RemoveAtSwapBack"/> are called in lockstep with that array; and <see cref="Run"/> schedules and
    /// completes inside the manager's tick, so no job is ever outstanding while anything else touches a root, adds
    /// one or removes one.
    /// </summary>
    public sealed class BasisModelFollowPass
    {
        private const int MinimumCapacity = 16;

        /// <summary>Every tracked pickup's root, at the pickup's <see cref="BasisModelPickupObject.ManagerIndex"/>.</summary>
        public TransformAccessArray Roots;

        /// <summary>
        /// Per-frame scratch indexed like <see cref="Roots"/> and at least as long: the manager stages every live
        /// slot before <see cref="Run"/>, so its contents never need to survive a frame or a resize.
        /// </summary>
        public NativeArray<BasisModelFollowSlot> Slots;

        public void Add(Transform root)
        {
            if (!Roots.isCreated)
                Roots = new TransformAccessArray(MinimumCapacity);
            else if (Roots.length == Roots.capacity)
                Roots.capacity = Roots.capacity * 2;
            Roots.Add(root);
            EnsureSlots(Roots.length);
        }

        public void RemoveAtSwapBack(int index)
        {
            if (Roots.isCreated && index >= 0 && index < Roots.length)
                Roots.RemoveAtSwapBack(index);
        }

        /// <summary>Runs the pass over the staged slots and waits for it. Main thread.</summary>
        public void Run(float lerpFactor)
        {
            JobHandle handle = new BasisModelFollowJob { Slots = Slots, LerpFactor = lerpFactor }.Schedule(Roots);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();
        }

        /// <summary>Frees both native buffers. Idempotent; the next <see cref="Add"/> creates them again.</summary>
        public void Dispose()
        {
            if (Roots.isCreated)
                Roots.Dispose();
            if (Slots.IsCreated)
                Slots.Dispose();
        }

        private void EnsureSlots(int count)
        {
            if (Slots.IsCreated && Slots.Length >= count)
                return;
            int capacity = Mathf.Max(MinimumCapacity, Mathf.NextPowerOfTwo(count));
            if (Slots.IsCreated)
                Slots.Dispose();
            Slots = new NativeArray<BasisModelFollowSlot>(capacity, Allocator.Persistent);
        }
    }
}
