using Basis.Scripts.Common;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Model pickup constants and the device tier's limits. Per-model limits come from the validator's
    /// <see cref="Validation.BasisModelLimits"/>; the tier (<see cref="BasisModelTierLimits"/>) adds aggregates,
    /// the working set, the import time slice and shadows. The tier is resolved on the main thread.
    /// </summary>
    public static class BasisModelPickupSettings
    {
        public const string ReceiveEnabledKey = "Basis.ModelPickup.ReceiveEnabled";
        public const string SaveFolderName = "Models";

        // Transfer.
        public const int MaxNetworkChunksPerFrame = 64;
        public const float InboundTransferTimeoutSeconds = 30f;

        /// <summary>
        /// A header whose first chunk never comes gives its reservation back after this long. Not shorter: the
        /// header goes out unmetered, and the first chunk can wait several seconds behind the metered uplink while
        /// later ones flow.
        /// </summary>
        public const float ZeroChunkTimeoutSeconds = 30f;

        public const float StalledTransferWarningSeconds = 5f;

        // Timing.
        public const float RecipientRangeRefreshSeconds = 5f;
        public const float OfferRangeCheckSeconds = 0.5f;
        public const int MaxPendingOffers = 256;

        /// <summary>A requested replay that has not arrived after this long stops holding budget.</summary>
        public const float RequestedOfferTimeoutSeconds = 60f;

        /// <summary>How long a despawned id is refused: longer than the server takes to send what it had queued.</summary>
        public const float DespawnTombstoneSeconds = 120f;

        /// <summary>A received model still waiting to validate or import after this long is dropped.</summary>
        public const float MaxQueuedImportWaitSeconds = 120f;

        public const float SizeDialogTimeoutSeconds = 120f;

        // Placement.
        public const float SpawnDistanceMeters = 1.5f;

        /// <summary>Half the unknown-shape cube: how far a batch's lowest row must clear the floor before shapes are known.</summary>
        public const float BatchHalfItemHeightMeters = BasisModelPickupObject.UnknownShapeEdgeMeters * 0.5f;

        public const float GroundClearanceMeters = 0.05f;
        public static readonly BasisModelBatchSpacing BatchSpacing = new BasisModelBatchSpacing(4, 8, 0.8f, 0.8f);

        public const int MaxBackPanelUpdatesPerFrame = 1;

        private static readonly BasisModelPersistentBool _receiveEnabled = new BasisModelPersistentBool(ReceiveEnabledKey, true);
        private static BasisModelTierLimits _limits;

        /// <summary>This device's tier. Resolved on first use (main thread) and again by the manager's Initialize.</summary>
        public static BasisModelTierLimits Limits
        {
            get
            {
                if (_limits == null)
                    ResolveLimits();
                return _limits;
            }
        }

        public static void ResolveLimits()
        {
            _limits = BasisModelTierLimits.ForDevice(SystemInfo.systemMemorySize, BasisGpuDetection.IsMobileGpu);
        }

        /// <summary>
        /// When false, models other players share are refused (you can still share your own), and this client stops
        /// announcing itself, so owners stop sending it model data. Turning it back on announces it again. Persisted.
        /// </summary>
        public static bool ReceiveEnabled
        {
            get => _receiveEnabled.Value;
            set
            {
                bool was = _receiveEnabled.Value;
                _receiveEnabled.Value = value;
                if (value && !was)
                    BasisModelPickupManager.OnReceiveEnabled();
            }
        }

        /// <summary>Documents\Basis\Models on Windows; Basis/Models under persistent data elsewhere.</summary>
        public static string SaveFolder()
        {
            return BasisModelPickupObject.SaveFolder();
        }
    }
}
