using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    public enum BasisModelTier : byte
    {
        Mobile = 0,
        Mid = 1,
        Desktop = 2,
    }

    /// <summary>
    /// What one sender's models may add up to on this client, checked from header claims before anything is reserved.
    /// The render budgets are a quarter of the resident ones, so no single peer can take the whole frame.
    /// </summary>
    public struct BasisModelSenderLimits
    {
        public int MaxModels;
        public long MaxBytes;
        public long MaxVertices;
        public long MaxTexturePixels;

        /// <summary>Estimated decoded memory plus the retained canonical GLB (<see cref="BasisModelAggregate.ResidentBytes"/>).</summary>
        public long MaxResidentBytes;

        public long MaxDrawCalls;
        public long MaxRenderedTriangles;
        public long MaxSkinnedVertexInstances;
        public long MaxNodes;
    }

    /// <summary>What every model on this client together may add up to: memory, and the render cost of drawing them all.</summary>
    public struct BasisModelResidentLimits
    {
        public int MaxModels;
        public long MaxResidentBytes;
        public long MaxDrawCalls;
        public long MaxRenderedTriangles;
        public long MaxSkinnedVertexInstances;
        public long MaxNodes;
    }

    /// <summary>
    /// Every number that depends on the device. Per-model limits are the validator's <see cref="BasisModelLimits"/>
    /// (Desktop for the Desktop and Mid tiers, Mobile for Mobile); the tier itself sets aggregates, the working set,
    /// the import time slice and shadows. <see cref="Create"/> returns a fresh instance, so callers may adjust one.
    /// </summary>
    public sealed class BasisModelTierLimits
    {
        private const long MiB = 1024L * 1024L;
        private const long GiB = 1024L * MiB;

        public BasisModelTier Tier;

        /// <summary>Per-model limits this client validates received models with (and admits header claims against).</summary>
        public BasisModelLimits Validation;

        public BasisModelSenderLimits Sender;
        public BasisModelResidentLimits Resident;

        /// <summary>Size of the model's own inbound transfer pool (<see cref="BasisModelInboundReservations"/>).</summary>
        public long InboundReservationBytes;

        public long WorkingSetBytes;
        public int MaxConcurrentWorkerJobs;
        public int MaxConcurrentMainThreadJobs;
        public float DeferBudgetMilliseconds;
        public bool CastShadows;

        /// <summary>
        /// Whether a received model keeps its canonical GLB after import (for Save). Mobile drops it: two dozen
        /// retained 16 MiB copies are memory a headset cannot spare. Owners always keep theirs, to re-send.
        /// </summary>
        public bool RetainReceivedGlb;

        /// <summary>
        /// Least time between two imports from the same sender. A texture decode cannot be split across frames, so on
        /// Mobile this keeps one peer from causing a hitch every couple of seconds. Zero means no spacing.
        /// </summary>
        public float MinSecondsBetweenImportsPerSender;

        /// <summary>
        /// Mobile for a mobile GPU or known memory of 4 GB or less; Mid up to 8 GB, and when memory is unknown (0 or
        /// less); Desktop above that. The Mobile rule is the validator's <see cref="BasisModelLimits.ForDevice"/>.
        /// </summary>
        public static BasisModelTier ResolveTier(int systemMemoryMegabytes, bool mobileGpu)
        {
            if (mobileGpu || (systemMemoryMegabytes > 0 && systemMemoryMegabytes <= 4096))
                return BasisModelTier.Mobile;
            if (systemMemoryMegabytes <= 8192)
                return BasisModelTier.Mid;
            return BasisModelTier.Desktop;
        }

        public static BasisModelTierLimits ForDevice(int systemMemoryMegabytes, bool mobileGpu)
        {
            return Create(ResolveTier(systemMemoryMegabytes, mobileGpu));
        }

        public static BasisModelTierLimits Create(BasisModelTier tier)
        {
            switch (tier)
            {
                case BasisModelTier.Mobile:
                    return CreateMobile();
                case BasisModelTier.Desktop:
                    return CreateDesktop();
                default:
                    return CreateMid();
            }
        }

        private static BasisModelTierLimits CreateDesktop()
        {
            return new BasisModelTierLimits
            {
                Tier = BasisModelTier.Desktop,
                Validation = BasisModelLimits.Desktop,
                Sender = new BasisModelSenderLimits
                {
                    MaxModels = 8,
                    MaxBytes = 128L * MiB,
                    MaxVertices = 2000000,
                    MaxTexturePixels = 64L * MiB,
                    MaxResidentBytes = 768L * MiB,
                    MaxDrawCalls = 4096 / 4,
                    MaxRenderedTriangles = 8000000 / 4,
                    MaxSkinnedVertexInstances = 4000000 / 4,
                    MaxNodes = 16384 / 4,
                },
                Resident = DesktopResident(64, 2L * GiB),
                InboundReservationBytes = 256L * MiB,
                WorkingSetBytes = 1L * GiB,
                MaxConcurrentWorkerJobs = 2,
                MaxConcurrentMainThreadJobs = 1,
                DeferBudgetMilliseconds = 3f,
                CastShadows = true,
                RetainReceivedGlb = true,
                MinSecondsBetweenImportsPerSender = 0f,
            };
        }

        private static BasisModelTierLimits CreateMid()
        {
            return new BasisModelTierLimits
            {
                Tier = BasisModelTier.Mid,
                Validation = BasisModelLimits.Desktop,
                Sender = new BasisModelSenderLimits
                {
                    MaxModels = 8,
                    MaxBytes = 96L * MiB,
                    MaxVertices = 1200000,
                    MaxTexturePixels = 48L * MiB,
                    MaxResidentBytes = 480L * MiB,
                    MaxDrawCalls = 4096 / 4,
                    MaxRenderedTriangles = 8000000 / 4,
                    MaxSkinnedVertexInstances = 4000000 / 4,
                    MaxNodes = 16384 / 4,
                },
                Resident = DesktopResident(48, 1L * GiB),
                InboundReservationBytes = 192L * MiB,
                WorkingSetBytes = 512L * MiB,
                MaxConcurrentWorkerJobs = 2,
                MaxConcurrentMainThreadJobs = 1,
                DeferBudgetMilliseconds = 3f,
                CastShadows = true,
                RetainReceivedGlb = true,
                MinSecondsBetweenImportsPerSender = 0f,
            };
        }

        private static BasisModelTierLimits CreateMobile()
        {
            return new BasisModelTierLimits
            {
                Tier = BasisModelTier.Mobile,
                Validation = BasisModelLimits.Mobile,
                Sender = new BasisModelSenderLimits
                {
                    MaxModels = 8,
                    MaxBytes = 48L * MiB,
                    MaxVertices = 450000,
                    MaxTexturePixels = 24L * MiB,
                    MaxResidentBytes = 192L * MiB,
                    // A quarter of the resident budget would sit below the Mobile per-model limits and
                    // refuse a valid model at the header; each sender may always share one maximal model.
                    MaxDrawCalls = 256,
                    MaxRenderedTriangles = 300000,
                    MaxSkinnedVertexInstances = 300000,
                    MaxNodes = 2048,
                },
                Resident = new BasisModelResidentLimits
                {
                    MaxModels = 24,
                    MaxResidentBytes = 384L * MiB,
                    MaxDrawCalls = 512,
                    MaxRenderedTriangles = 1000000,
                    MaxSkinnedVertexInstances = 500000,
                    MaxNodes = 4096,
                },
                InboundReservationBytes = 96L * MiB,
                WorkingSetBytes = 256L * MiB,
                MaxConcurrentWorkerJobs = 1,
                MaxConcurrentMainThreadJobs = 1,
                DeferBudgetMilliseconds = 2f,
                CastShadows = false,
                RetainReceivedGlb = false,
                MinSecondsBetweenImportsPerSender = 5f,
            };
        }

        // Mid shares Desktop's render budgets; only the model count and memory differ.
        private static BasisModelResidentLimits DesktopResident(int maxModels, long maxResidentBytes)
        {
            return new BasisModelResidentLimits
            {
                MaxModels = maxModels,
                MaxResidentBytes = maxResidentBytes,
                MaxDrawCalls = 4096,
                MaxRenderedTriangles = 8000000,
                MaxSkinnedVertexInstances = 4000000,
                MaxNodes = 16384,
            };
        }
    }
}
