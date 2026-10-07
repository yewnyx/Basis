using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using Basis.ModelPickup.Validation;
using UnityEngine;

namespace Basis.ModelPickup
{
    public static partial class BasisModelPickupManager
    {
        private static readonly List<string> _dropPaths = new List<string>();
        private static readonly List<long> _dropSizes = new List<long>();
        private static readonly string[] _singleDrop = new string[1];

        /// <summary>One file; see <see cref="SpawnFromFiles"/>.</summary>
        public static bool SpawnFromFile(string path)
        {
            _singleDrop[0] = path;
            try
            {
                return SpawnFromFiles(_singleDrop) > 0;
            }
            finally
            {
                _singleDrop[0] = null;
            }
        }

        /// <summary>
        /// Spawns the existing .glb and .gltf files among <paramref name="paths"/> (others are left to whoever
        /// else handles drops) as one batch: a loading placeholder for each at once, laid out in front of the
        /// viewer, and one size question for all of them. Returns how many were accepted. Main thread.
        /// </summary>
        public static int SpawnFromFiles(IReadOnlyList<string> paths)
        {
            if (paths == null)
                return 0;
            _dropPaths.Clear();
            _dropSizes.Clear();
            int pathCount = paths.Count;
            for (int i = 0; i < pathCount; i++)
            {
                string path = paths[i];
                if (!BasisGlbValidator.HasSupportedModelExtension(path) || !TryGetFileLength(path, out long length))
                    continue;
                _dropPaths.Add(path);
                _dropSizes.Add(length);
            }
            if (_dropPaths.Count == 0)
                return 0;

            try
            {
                return SpawnDropped();
            }
            finally
            {
                _dropPaths.Clear();
                _dropSizes.Clear();
            }
        }

        private static int SpawnDropped()
        {
            int supported = _dropPaths.Count;
            string first = _dropPaths[0];
            if (!_initialized)
            {
                BasisDebug.LogWarning("Model pickup is not initialized; ignoring dropped models.", LogTag);
                return 0;
            }
            if (BasisModelShareLocks.IsBlockedLocally())
            {
                BasisDebug.LogWarning("Model pickup rejected: an administrator has locked props.", LogTag);
                BasisModelPickupPopups.ShowRejected(first, BasisLocalization.Get(BasisModelPickupPopups.AdminLockedKey));
                return 0;
            }
            if (!BasisModelShareLocks.LocalPlayerMayShare())
            {
                BasisDebug.LogWarning("Model pickup rejected: this server does not let you load props.", LogTag);
                BasisModelPickupPopups.ShowRejected(first, BasisLocalization.Get(BasisModelPickupPopups.NoPermissionKey));
                return 0;
            }

            // Every local job counts from the moment it is queued, so a burst of drops cannot overshoot the cap.
            int inFlight = CountLocalJobs();
            int current = _owned.Count + inFlight;
            int slots = BasisModelBudget.AvailableLocalSlots(_owned.Count, inFlight, Limits.Sender.MaxModels);
            if (slots == 0)
            {
                BasisDebug.LogWarning($"Model pickup rejected: the local model limit of {Limits.Sender.MaxModels} is reached.", LogTag);
                BasisModelPickupPopups.ShowLimit(current, supported);
                return 0;
            }
            int attempt = Math.Min(supported, slots);

            BasisModelSpawnPose.GetSpawnPose(BasisModelPickupSettings.SpawnDistanceMeters, out Vector3 position, out Quaternion rotation, out Vector3 right);
            float groundY = BasisModelSpawnPose.MinimumBatchCenterY(position.y, 0f, 0f);
            float minimumCenterY = groundY + BasisModelPickupSettings.BatchHalfItemHeightMeters + BasisModelPickupSettings.GroundClearanceMeters;
            BasisModelBatchSpacing spacing = BasisModelPickupSettings.BatchSpacing;
            int columns = BasisModelBatchLayout.Columns(attempt, position.y, minimumCenterY, spacing);

            float now = Time.unscaledTime;
            BasisModelSizeBatch batch = BasisModelSizeDialog.BeginBatch(now);
            ushort ownerId = LocalId();
            string ownerName = BasisModelShareNet.LocalOwnerName();
            string loading = BasisLocalization.Get("modelPickup.shareable.loading");
            int generation = Identity.ConnectionGeneration;

            for (int i = 0; i < attempt; i++)
            {
                BasisModelBatchLayout.LocalOffset(i, attempt, columns, minimumCenterY - position.y, spacing, out float x, out float y);
                Vector3 spawn = position + right * x + Vector3.up * y;
                Guid id = Guid.NewGuid();
                BasisModelPickupObject pickup = BasisModelPickupObject.Build(id, ownerId, ownerName, true, spawn, rotation, PickupHost.Instance);
                TrackModel(id, pickup);
                BasisModelShareables.Register(id, loading, ownerName, () => RequestDespawn(id));
                batch.Members++;
                string path = _dropPaths[i];
                AddJob(
                    new BasisModelJob
                    {
                        Kind = BasisModelJobKind.Local,
                        Stage = BasisModelJobStage.Queued,
                        Id = id,
                        Pickup = pickup,
                        Path = path,
                        Label = path,
                        SourceBytes = _dropSizes[i],
                        Batch = batch,
                        GroundY = groundY,
                        WaitingSince = now,
                        Generation = generation,
                        Cancel = new CancellationTokenSource(),
                    }
                );
            }

            BasisDebug.Log($"Model pickup queued {attempt:N0} of {supported:N0} dropped model(s).", LogTag);
            BasisModelPickupPopups.ShowBatchNotice(current, supported, attempt);
            return attempt;
        }

        private static bool TryGetFileLength(string path, out long length)
        {
            length = 0;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                    return false;
                length = info.Length;
                return true;
            }
            catch (Exception)
            {
                // A path the file system refuses to describe is not a file we can load.
                return false;
            }
        }

        private static int CountLocalJobs()
        {
            int count = 0;
            int jobCount = JobCount;
            for (int i = 0; i < jobCount; i++)
            {
                BasisModelJob job = Jobs[i];
                if (job.Kind == BasisModelJobKind.Local && !job.Dead)
                    count++;
            }
            return count;
        }

        private static BasisModelSizeMode EffectiveMode(BasisModelJob job)
        {
            return job.Batch != null ? job.Batch.EffectiveMode : job.Tail.SizeMode;
        }

        private static bool TryStartPrepare(BasisModelJob job)
        {
            if (!TryBeginWorker(job, BasisModelWorkBudget.EstimatePrepareBytes(job.SourceBytes)))
                return false;
            job.Stage = BasisModelJobStage.Preparing;
            string path = job.Path;
            CancellationToken token = job.Cancel.Token;
            job.PrepareTask = Task.Run(() => PrepareFromFile(path, token));
            return true;
        }

        /// <summary>Worker thread: the file and the validator only, never Unity or the manager's state.</summary>
        private static BasisGlbPrepareResult PrepareFromFile(string path, CancellationToken token)
        {
            if (!BasisModelFileRules.TryReadBounded(path, BasisModelLimits.Desktop.MaxSourceBytes, out byte[] source, out string error))
                return new BasisGlbPrepareResult { Ok = false, ErrorKind = BasisGlbErrorKind.Malformed, Error = error };
            if (!BasisGlbValidator.TryDetectFormat(path, source, out BasisModelSourceFormat format, out error))
                return new BasisGlbPrepareResult { Ok = false, ErrorKind = BasisGlbErrorKind.Unsupported, Error = error };
            return BasisGlbValidator.Prepare(source, format, BasisModelLimits.Desktop, token);
        }

        private static void OnPrepared(BasisModelJob job)
        {
            bool ran = TryGetResult(job.PrepareTask, out BasisGlbPrepareResult result, out string failure);
            job.PrepareTask = null;
            if (!ran || !result.Ok)
            {
                RejectLocalValidation(job, ran ? result.Error : failure, ran ? result.ErrorKind : BasisGlbErrorKind.Internal);
                return;
            }

            BasisGlbPreparedModel prepared = result.Model;
            job.Prepared = prepared;
            job.Bounds = prepared.Bounds;
            job.HasBounds = true;
            ApplyLocalShape(job, EffectiveMode(job));
            job.NextImage = 0;
            job.Stage = prepared.ImageCount > 0 ? BasisModelJobStage.AwaitSanitize : BasisModelJobStage.AwaitCanonical;
        }

        /// <summary>Embedded images are decoded and re-encoded on the main thread, one at a time across every job.</summary>
        private static bool TryStartSanitize(BasisModelJob job)
        {
            if (!TryBeginMainThread(job, 0))
                return false;
            job.Stage = BasisModelJobStage.Sanitizing;
            BasisGlbPreparedModel prepared = job.Prepared;
            int index = job.NextImage;
            job.SanitizeTask = BasisModelImageSanitizer.Instance.SanitizeAsync(
                prepared.CopyImageSource(index),
                prepared.GetImageFormat(index),
                job.Cancel.Token
            );
            return true;
        }

        private static void OnSanitized(BasisModelJob job)
        {
            bool ran = TryGetResult(job.SanitizeTask, out BasisModelImageSanitizeResult result, out string failure);
            job.SanitizeTask = null;
            BasisGlbPreparedModel prepared = job.Prepared;
            int index = job.NextImage;
            if (!ran || !result.Ok || result.Png == null)
            {
                string error = ran ? result.Error : failure;
                RejectLocal(
                    job,
                    BasisLocalization.Get(BasisModelPickupPopups.TextureRejectedKey, prepared.GetSourceImageIndex(index) + 1, error),
                    false
                );
                return;
            }

            prepared.SetSanitizedImage(index, result.Png);
            job.SanitizedBytes += result.Png.Length;
            job.NextImage = index + 1;
            job.Stage = job.NextImage < prepared.ImageCount ? BasisModelJobStage.AwaitSanitize : BasisModelJobStage.AwaitCanonical;
        }

        private static bool TryStartCanonical(BasisModelJob job)
        {
            long charge = BasisModelWorkBudget.EstimateFinishBytes(job.SourceBytes, job.SanitizedBytes, BasisModelLimits.Desktop.MaxModelBytes);
            if (!TryBeginWorker(job, charge))
                return false;
            job.Stage = BasisModelJobStage.Canonicalizing;
            BasisGlbPreparedModel prepared = job.Prepared;
            CancellationToken token = job.Cancel.Token;
            job.ValidateTask = Task.Run(() => BasisGlbValidator.Finish(prepared, token));
            return true;
        }

        /// <summary>
        /// The canonical GLB exists and has already passed the receiver's own checks (Finish runs them). What is
        /// left is whether it fits this client's budgets with everything else it shares, and the claims receivers
        /// will admit it by.
        /// </summary>
        private static void OnCanonicalized(BasisModelJob job, float now)
        {
            bool ran = TryGetResult(job.ValidateTask, out BasisGlbValidationResult result, out string failure);
            job.ValidateTask = null;
            job.Prepared = null;
            if (!ran || !result.Ok)
            {
                RejectLocalValidation(job, ran ? result.Error : failure, ran ? result.ErrorKind : BasisGlbErrorKind.Internal);
                return;
            }

            BasisGlbClaims claims = BasisGlbClaims.FromStats(result.Stats);
            int totalBytes = result.CleanGlb.Length;
            BuildTotals(job.Id, true, 0, out BasisModelAggregate sender, out BasisModelAggregate resident);
            sender.Add(claims, totalBytes);
            resident.Add(claims, totalBytes);
            if (
                !BasisModelBudget.IsWithinSenderLimits(sender, Limits.Sender, out string budget)
                || !BasisModelBudget.IsWithinResidentLimits(resident, Limits.Resident, out budget)
            )
            {
                BasisDebug.LogWarning($"Model pickup: the model would exceed the {budget}.", LogTag);
                string cost = BasisModelFileRules.DescribeBytes(claims.EstimatedDecodedBytes + totalBytes);
                RejectLocal(job, BasisLocalization.Get(BasisModelPickupPopups.MemoryBudgetKey, cost), true);
                return;
            }

            string name = BasisModelRichText.FileNameForDisplay(job.Path);
            if (result.Stripped != BasisGlbStripped.None)
                BasisDebug.Log($"Model pickup removed unsupported content from {name}: {result.Stripped}.", LogTag);
            if (!claims.TryAdmit(totalBytes, BasisModelLimits.Mobile, out string mobile))
                BasisDebug.Log($"Model pickup: {name} is over the mobile limits ({mobile}); mobile players will not load it.", LogTag);

            job.Stats = result.Stats;
            job.CleanGlb = result.CleanGlb;
            job.Bounds = result.Stats.Bounds;
            job.HasBounds = true;
            BasisModelSizeMode mode = EffectiveMode(job);
            job.Tail = new BasisModelSpawnTail
            {
                SizeMode = mode,
                BaseScale = BasisModelSizing.ComputeBaseScale(mode, job.Bounds.MaxExtent),
                Claims = claims,
            };
            // Counted in budget totals from here on, like a received model's header claims.
            job.Pickup.Claims = claims;
            job.Pickup.TotalBytes = totalBytes;
            ApplyLocalShape(job, mode);
            BasisModelShareables.SetTitle(job.Id, BasisLocalization.Get("modelPickup.shareable.detail", result.Stats.Triangles));
            job.Stage = BasisModelJobStage.AwaitImport;
            job.WaitingSince = now;
        }

        private static void ApplyLocalShape(BasisModelJob job, BasisModelSizeMode mode)
        {
            float scale = BasisModelSizing.ComputeBaseScale(mode, job.Bounds.MaxExtent);
            job.Pickup.SetShape(job.Bounds, scale);
            job.Pickup.LiftAboveGround(job.GroundY, BasisModelPickupSettings.GroundClearanceMeters);
        }

        /// <summary>
        /// The size question was answered (or defaulted). Models of the batch still loading take the chosen size
        /// now; loaded ones also become replicable, since the size is what their spawn header tells receivers.
        /// </summary>
        private static void OnSizeBatchResolved(BasisModelSizeBatch batch)
        {
            if (batch == null || batch.Cancelled)
                return;
            BasisModelSizeMode mode = batch.Mode;

            int jobCount = JobCount;
            for (int i = 0; i < jobCount; i++)
            {
                BasisModelJob job = Jobs[i];
                if (job.Dead || !ReferenceEquals(job.Batch, batch) || !job.HasBounds || job.Pickup == null)
                    continue;
                ApplyLocalShape(job, mode);
                if (job.CleanGlb != null)
                {
                    job.Tail.SizeMode = mode;
                    job.Tail.BaseScale = BasisModelSizing.ComputeBaseScale(mode, job.Bounds.MaxExtent);
                }
            }

            // The answer can come up to two minutes after a model finished loading; a props lock that landed
            // meanwhile stops the share just as it would have stopped the load.
            if (BasisModelShareLocks.IsBlockedLocally())
            {
                RejectLockedWhileUndecided(batch);
                return;
            }

            foreach (KeyValuePair<Guid, OwnedModel> entry in _owned)
            {
                if (ReferenceEquals(entry.Value.Batch, batch))
                    MakeReplicable(entry.Value, mode);
            }
        }

        private static void RejectLockedWhileUndecided(BasisModelSizeBatch batch)
        {
            List<Guid> ids = null;
            string label = null;
            foreach (KeyValuePair<Guid, OwnedModel> entry in _owned)
            {
                if (!ReferenceEquals(entry.Value.Batch, batch))
                    continue;
                (ids ??= new List<Guid>()).Add(entry.Key);
                label ??= entry.Value.Label;
            }
            if (ids == null)
                return;
            for (int i = 0; i < ids.Count; i++)
                RemoveModel(ids[i]);
            BasisDebug.LogWarning($"Model pickup dropped {ids.Count} model(s): props were locked before their size was chosen.", LogTag);
            if (!batch.LockNoticeShown)
            {
                batch.LockNoticeShown = true;
                BasisModelPickupPopups.ShowRejected(label, BasisLocalization.Get(BasisModelPickupPopups.AdminLockedDuringLoadKey));
            }
        }

        private static void RejectLocalValidation(BasisModelJob job, string reason, BasisGlbErrorKind kind)
        {
            // Internal is a validator or caller bug, not something the user's file did.
            if (kind == BasisGlbErrorKind.Internal)
                BasisDebug.LogError($"Model pickup validation failed internally for {BasisModelRichText.FileNameForDisplay(job.Path)}: {reason}", LogTag);
            RejectLocal(job, reason, kind == BasisGlbErrorKind.OverLimit);
        }

        private static void RejectLocal(BasisModelJob job, string reason, bool overLimit)
        {
            string label = job.Label;
            RemoveModel(job.Id);
            BasisDebug.LogWarning($"Model pickup rejected {BasisModelRichText.FileNameForDisplay(label)}: {reason}", LogTag);
            if (overLimit)
                BasisModelPickupPopups.ShowOverLimit(label, reason);
            else
                BasisModelPickupPopups.ShowRejected(label, reason);
        }

        /// <summary>Props were locked while this drop was loading. Said once per batch, however many it held.</summary>
        private static void RejectLockedDuringLoad(BasisModelJob job)
        {
            BasisModelSizeBatch batch = job.Batch;
            bool notify = batch == null || !batch.LockNoticeShown;
            if (batch != null)
                batch.LockNoticeShown = true;
            string label = job.Label;
            RemoveModel(job.Id);
            KillJob(job);
            BasisDebug.LogWarning(
                $"Model pickup dropped {BasisModelRichText.FileNameForDisplay(label)}: props were locked while it was loading.",
                LogTag
            );
            if (notify)
                BasisModelPickupPopups.ShowRejected(label, BasisLocalization.Get(BasisModelPickupPopups.AdminLockedDuringLoadKey));
        }
    }
}
