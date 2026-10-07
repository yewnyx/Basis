using System;
using System.Globalization;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    /// <summary>Why <see cref="BasisModelAdmission.Evaluate"/> refused a spawn, in evaluation order.</summary>
    public enum BasisModelAdmissionResult : byte
    {
        Accepted = 0,
        ReceiveDisabled = 1,
        Header = 2,
        Size = 3,
        ChunkCount = 4,
        Claims = 5,
        Pose = 6,
        SenderBudget = 7,
        ResidentBudget = 8,
        TooManyTransfers = 9,
    }

    /// <summary>Everything a receiver knows about a spawn header before it reserves memory for the download.</summary>
    public struct BasisModelAdmissionInput
    {
        public bool ReceiveEnabled;

        /// <summary>Result of <see cref="BasisModelWire.TryReadTail"/>; when false, <see cref="HeaderError"/> says why.</summary>
        public bool HeaderOk;

        public string HeaderError;
        public BasisModelSpawnTail Tail;
        public int TotalBytes;
        public int TotalChunks;

        /// <summary>Result of <see cref="BasisModelPoseValidation.TrySanitizePose"/> with <see cref="BasisModelSizing.RemoteLimits"/>.</summary>
        public bool PoseValid;

        /// <summary>This sender's models already on this client, excluding the candidate.</summary>
        public BasisModelAggregate SenderTotals;

        /// <summary>Every model on this client, excluding the candidate.</summary>
        public BasisModelAggregate ResidentTotals;

        public int ActiveTransfersFromSender;

        /// <summary>
        /// This client asked the server cache for this model. The replay was already checked against its claims before
        /// the request and is paced by the server, so it skips the rate token and the per-sender transfer cap; a live
        /// push from the owner landing in the same seconds would otherwise be dropped for good.
        /// </summary>
        public bool WasRequested;
    }

    /// <summary>
    /// Header-time admission for inbound models: pure, so every rule is testable without a network. Order is cheap
    /// and decisive first: switch, header, size, chunk count, claims against the tier, pose, then the budgets that
    /// depend on what is already here. The rate token is not taken here; the caller takes it only after
    /// <see cref="BasisModelAdmissionResult.Accepted"/> and a successful reservation, through
    /// <see cref="TryTakeRateToken"/>.
    /// </summary>
    public static class BasisModelAdmission
    {
        public const int MaxInboundTransfersPerSender = 2;

        /// <summary>Spawn burst per sender: twice <see cref="BasisModelSenderLimits.MaxModels"/>, so a sender can replace its whole set at once.</summary>
        public const float SpawnRateCapacity = 16f;

        public const float SpawnRateIntervalSeconds = 2f;

        public static BasisModelSpawnRateLimiter CreateRateLimiter()
        {
            return new BasisModelSpawnRateLimiter(SpawnRateCapacity, SpawnRateIntervalSeconds);
        }

        public static BasisModelAdmissionResult Evaluate(in BasisModelAdmissionInput input, BasisModelTierLimits limits, out string reason)
        {
            if (limits == null)
                throw new ArgumentNullException(nameof(limits));

            if (!input.ReceiveEnabled)
            {
                reason = "receiving shared models is turned off";
                return BasisModelAdmissionResult.ReceiveDisabled;
            }
            if (!input.HeaderOk)
            {
                reason = string.IsNullOrEmpty(input.HeaderError) ? "malformed spawn header" : input.HeaderError;
                return BasisModelAdmissionResult.Header;
            }
            if (!TryCheckSize(input.TotalBytes, limits.Validation, out reason))
                return BasisModelAdmissionResult.Size;
            if (!TryCheckChunkCount(input.TotalBytes, input.TotalChunks, out reason))
                return BasisModelAdmissionResult.ChunkCount;

            BasisGlbClaims claims = input.Tail.Claims;
            if (!claims.TryAdmit(input.TotalBytes, limits.Validation, out reason))
                return BasisModelAdmissionResult.Claims;
            if (!input.PoseValid)
            {
                reason = "spawn pose is not finite or out of range";
                return BasisModelAdmissionResult.Pose;
            }

            BasisModelAdmissionResult budget = CheckBudgets(claims, input.TotalBytes, input.SenderTotals, input.ResidentTotals, limits, out reason);
            if (budget != BasisModelAdmissionResult.Accepted)
                return budget;

            if (!input.WasRequested && input.ActiveTransfersFromSender >= MaxInboundTransfersPerSender)
            {
                reason = "already receiving " + MaxInboundTransfersPerSender.ToString(CultureInfo.InvariantCulture)
                    + " models from this sender";
                return BasisModelAdmissionResult.TooManyTransfers;
            }
            reason = null;
            return BasisModelAdmissionResult.Accepted;
        }

        /// <summary>
        /// The rate-limit step after admission and reservation. A requested replay never spends a token (see
        /// <see cref="BasisModelAdmissionInput.WasRequested"/>).
        /// </summary>
        public static bool TryTakeRateToken(in BasisModelAdmissionInput input, BasisModelSpawnRateLimiter rate, ushort sender, float now)
        {
            if (rate == null)
                throw new ArgumentNullException(nameof(rate));
            return input.WasRequested || rate.TryConsume(sender, now);
        }

        /// <summary>
        /// Decides whether a server-cache offer is worth tracking: the same header, size, chunk-count, claims and pose
        /// rules a live spawn passes, so a request is only ever sent for something this client could accept.
        /// </summary>
        public static bool TryParseOffer(in BasisModelSpawnHeader header, ReadOnlySpan<byte> message, BasisModelTierLimits limits,
            out BasisModelOfferInfo offer, out string reason)
        {
            if (limits == null)
                throw new ArgumentNullException(nameof(limits));

            offer = default;
            if (!BasisModelWire.TryReadTail(header, message, out BasisModelSpawnTail tail, out reason))
                return false;
            if (!TryCheckSize(header.TotalBytes, limits.Validation, out reason))
                return false;
            if (!TryCheckChunkCount(header.TotalBytes, header.TotalChunks, out reason))
                return false;
            if (!tail.Claims.TryAdmit(header.TotalBytes, limits.Validation, out reason))
                return false;
            BasisModelPose pose = header.Pose;
            if (!BasisModelPoseValidation.TrySanitizePose(ref pose, BasisModelSizing.RemoteLimits))
            {
                reason = "offer pose is not finite or out of range";
                return false;
            }

            offer = new BasisModelOfferInfo { Tail = tail, TotalBytes = header.TotalBytes, ClaimedOwnerId = header.OwnerId };
            reason = null;
            return true;
        }

        /// <summary>
        /// The budget half of deciding whether to request an offer now. A false result keeps the offer pending, since
        /// budgets free up as models are deleted.
        /// </summary>
        public static bool OfferFitsBudgets(in BasisModelOfferInfo offer, in BasisModelAggregate senderTotals,
            in BasisModelAggregate residentTotals, BasisModelTierLimits limits, out string reason)
        {
            if (limits == null)
                throw new ArgumentNullException(nameof(limits));
            return CheckBudgets(offer.Tail.Claims, offer.TotalBytes, senderTotals, residentTotals, limits, out reason)
                == BasisModelAdmissionResult.Accepted;
        }

        private static BasisModelAdmissionResult CheckBudgets(in BasisGlbClaims claims, int totalBytes,
            in BasisModelAggregate senderTotals, in BasisModelAggregate residentTotals, BasisModelTierLimits limits, out string reason)
        {
            // The candidate is still downloading and validating, so it holds its GLB whatever this tier does after import.
            BasisModelAggregate sender = senderTotals;
            sender.Add(claims, totalBytes);
            if (!BasisModelBudget.IsWithinSenderLimits(sender, limits.Sender, out reason))
                return BasisModelAdmissionResult.SenderBudget;

            BasisModelAggregate resident = residentTotals;
            resident.Add(claims, totalBytes);
            if (!BasisModelBudget.IsWithinResidentLimits(resident, limits.Resident, out reason))
                return BasisModelAdmissionResult.ResidentBudget;
            return BasisModelAdmissionResult.Accepted;
        }

        private static bool TryCheckSize(int totalBytes, in BasisModelLimits validation, out string reason)
        {
            if (totalBytes <= 0)
            {
                reason = "the model is empty";
                return false;
            }
            if (totalBytes > validation.MaxModelBytes)
            {
                reason = BasisGlbErrors.Bytes("Model", totalBytes, validation.MaxModelBytes);
                return false;
            }
            reason = null;
            return true;
        }

        private static bool TryCheckChunkCount(int totalBytes, int totalChunks, out string reason)
        {
            int expected = BasisModelShareWire.ExpectedChunkCount(totalBytes, BasisModelWire.ChunkPayloadBytes);
            if (totalChunks != expected)
            {
                reason = "header declares " + totalChunks.ToString(CultureInfo.InvariantCulture) + " chunks; "
                    + totalBytes.ToString(CultureInfo.InvariantCulture) + " bytes need "
                    + expected.ToString(CultureInfo.InvariantCulture);
                return false;
            }
            reason = null;
            return true;
        }
    }
}
