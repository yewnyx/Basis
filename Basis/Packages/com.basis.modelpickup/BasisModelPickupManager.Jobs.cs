using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.BasisUI;

namespace Basis.ModelPickup
{
    public static partial class BasisModelPickupManager
    {
        /// <summary>Last import start per sender, for the Mobile tier's spacing between one peer's imports.</summary>
        private static readonly Dictionary<ushort, float> _lastImportBySender = new Dictionary<ushort, float>();

        private static bool _workPausedLogged;

        /// <summary>
        /// The polled stage machine. Completions first, so budget freed this frame can start the next stage this
        /// frame; then starts, in queue order. Nothing awaits a job: a result is read only after its task is seen
        /// complete, and only once the job is still alive.
        /// </summary>
        private static void AdvanceJobs(float now)
        {
            CompleteJobs(now);
            StartJobs(now);
        }

        /// <summary>
        /// The placeholder this job fills is still the tracked one, and for inbound work, the connection, the
        /// sender's ownership and the receive switch are unchanged. Local jobs survive connecting (their models are
        /// re-stamped) and die with their models on leave.
        /// </summary>
        private static bool IsAlive(BasisModelJob job)
        {
            if (job.Dead || !_initialized)
                return false;
            if (!Models.TryGetValue(job.Id, out BasisModelPickupObject pickup) || pickup == null || !ReferenceEquals(pickup, job.Pickup))
                return false;
            if (job.Kind == BasisModelJobKind.Inbound)
            {
                if (job.Generation != Identity.ConnectionGeneration)
                    return false;
                if (!BasisModelPickupSettings.ReceiveEnabled || pickup.OwnerId != job.Sender)
                    return false;
            }
            return true;
        }

        // Reverse, so dropping the current job never shifts one not yet visited. Nothing called from here adds or
        // removes jobs; removals only mark them dead, and they are dropped when this loop reaches them.
        private static void CompleteJobs(float now)
        {
            for (int i = JobCount - 1; i >= 0; i--)
            {
                BasisModelJob job = Jobs[i];
                Task task = job.RunningTask;
                if (task != null && !task.IsCompleted)
                    continue;
                // Held only while a stage's task runs, so this returns it exactly once.
                ReleaseBudget(job);

                if (!IsAlive(job))
                {
                    RetireJob(job);
                    DisposeUnclaimedResult(job);
                    DropJob(i);
                    continue;
                }

                if (job.Kind == BasisModelJobKind.Local && BasisModelShareLocks.IsBlockedLocally())
                {
                    DisposeUnclaimedResult(job);
                    RejectLockedDuringLoad(job);
                    DropJob(i);
                    continue;
                }

                if (task == null)
                    continue;

                switch (job.Stage)
                {
                    case BasisModelJobStage.Preparing:
                        OnPrepared(job);
                        break;
                    case BasisModelJobStage.Sanitizing:
                        OnSanitized(job);
                        break;
                    case BasisModelJobStage.Canonicalizing:
                        OnCanonicalized(job, now);
                        break;
                    case BasisModelJobStage.Validating:
                        OnValidated(job, now);
                        break;
                    case BasisModelJobStage.Importing:
                        OnImported(job);
                        break;
                }

                if (job.Dead || job.Stage == BasisModelJobStage.Finished)
                {
                    DisposeUnclaimedResult(job);
                    DropJob(i);
                }
            }
        }

        private static void StartJobs(float now)
        {
            bool preparedThisFrame = false;
            int count = JobCount;
            for (int i = 0; i < count; i++)
            {
                BasisModelJob job = Jobs[i];
                if (job.Dead || job.RunningTask != null)
                    continue;

                if (job.Kind == BasisModelJobKind.Local)
                {
                    // The drain check: a lock that lands while a drop waits for a slot stops it before more work.
                    if (BasisModelShareLocks.IsBlockedLocally())
                    {
                        RejectLockedDuringLoad(job);
                        continue;
                    }
                }
                else if ((job.Stage == BasisModelJobStage.Queued || job.Stage == BasisModelJobStage.AwaitImport)
                    && now - job.WaitingSince >= BasisModelPickupSettings.MaxQueuedImportWaitSeconds)
                {
                    BasisDebug.LogWarning($"Model pickup from {job.Sender} waited too long to load and was dropped.", LogTag);
                    RemoveModel(job.Id);
                    continue;
                }

                bool started;
                try
                {
                    switch (job.Stage)
                    {
                        case BasisModelJobStage.Queued:
                            if (job.Kind == BasisModelJobKind.Local)
                            {
                                // One file read and parse per frame: each is a burst of worker allocation.
                                if (preparedThisFrame)
                                    continue;
                                started = TryStartPrepare(job);
                                preparedThisFrame |= started;
                            }
                            else
                            {
                                started = TryStartValidate(job);
                            }
                            break;
                        case BasisModelJobStage.AwaitSanitize:
                            started = TryStartSanitize(job);
                            break;
                        case BasisModelJobStage.AwaitCanonical:
                            started = TryStartCanonical(job);
                            break;
                        case BasisModelJobStage.AwaitImport:
                            started = TryStartImport(job, now);
                            break;
                        default:
                            continue;
                    }
                }
                catch (Exception exception)
                {
                    // The stage was set before the starter threw, so no task will ever land for it: give up the job
                    // here or it holds its slot and reservation until someone deletes the placeholder.
                    BasisDebug.LogError($"Model pickup could not start {job.Stage} for model {job.Id}: {exception.Message}", LogTag);
                    ReleaseBudget(job);
                    RemoveModel(job.Id);
                    KillJob(job);
                    continue;
                }

                if (started)
                    _workPausedLogged = false;
            }
        }

        private static bool TryBeginWorker(BasisModelJob job, long charge)
        {
            if (!_work.TryBeginWorker(charge))
            {
                LogWorkPaused();
                return false;
            }
            job.HoldsBudget = true;
            job.Charge = charge;
            job.ChargedMainThread = false;
            return true;
        }

        private static bool TryBeginMainThread(BasisModelJob job, long charge)
        {
            if (!_work.TryBeginMainThread(charge))
            {
                if (_work.ActiveMainThread == 0)
                    LogWorkPaused();
                return false;
            }
            job.HoldsBudget = true;
            job.Charge = charge;
            job.ChargedMainThread = true;
            return true;
        }

        private static void ReleaseBudget(BasisModelJob job)
        {
            if (!job.HoldsBudget)
                return;
            if (job.ChargedMainThread)
                _work.EndMainThread(job.Charge);
            else
                _work.EndWorker(job.Charge);
            job.HoldsBudget = false;
            job.Charge = 0;
        }

        private static void LogWorkPaused()
        {
            if (_workPausedLogged)
                return;
            _workPausedLogged = true;
            BasisDebug.Log(
                $"Model pickup is waiting for working memory: {_work.ChargedBytes / (1024L * 1024L):N0} MiB of "
                    + $"{_work.WorkingSetBytes / (1024L * 1024L):N0} MiB in use.",
                LogTag
            );
        }

        /// <summary>
        /// One main-thread import at a time (the main-thread slot), on the shared defer agent the tick arms. On the
        /// Mobile tier one sender's imports are spaced out, since a texture decode cannot be split across frames.
        /// </summary>
        private static bool TryStartImport(BasisModelJob job, float now)
        {
            bool inbound = job.Kind == BasisModelJobKind.Inbound;
            float spacing = Limits.MinSecondsBetweenImportsPerSender;
            if (inbound && spacing > 0f && _lastImportBySender.TryGetValue(job.Sender, out float last) && now - last < spacing)
                return false;

            if (!TryBeginMainThread(job, Math.Max(0L, job.Stats.EstimatedPeakBytes)))
                return false;

            job.Stage = BasisModelJobStage.Importing;
            var ticket = new BasisModelImportTicket(
                inbound ? job.Label : BasisModelRichText.FileNameForDisplay(job.Path),
                inbound
            );
            job.Ticket = ticket;
            job.Pickup.AttachTicket(ticket);
            var options = new BasisModelImportOptions
            {
                Layer = job.Pickup.gameObject.layer,
                CastShadows = Limits.CastShadows,
                // The validator's decoded-memory estimate assumes mips on every tier.
                GenerateMipMaps = true,
            };
            job.ImportTask = BasisModelGltfLoader.ImportAsync(job.CleanGlb, job.Stats, options, BasisModelGltfLoader.DeferAgent, ticket);
            if (inbound)
                _lastImportBySender[job.Sender] = now;
            return true;
        }

        private static void OnImported(BasisModelJob job)
        {
            TryGetResult(job.ImportTask, out BasisModelImportResult result, out string failure);
            job.ImportTask = null;
            job.Ticket = null;

            if (result == null || !result.Ok)
            {
                result?.Dispose();
                FailImport(job, result != null && !string.IsNullOrEmpty(result.Error) ? result.Error : failure);
                return;
            }

            bool local = job.Kind == BasisModelJobKind.Local;
            // Mobile receivers drop the received GLB once it is loaded (and so cannot Save it); owners keep theirs to re-send.
            byte[] keep = local || Limits.RetainReceivedGlb ? job.CleanGlb : null;
            if (!job.Pickup.TryAdoptModel(result, keep))
            {
                result.Dispose();
                FailImport(job, "the pickup could not take the loaded model");
                return;
            }

            if (local)
                AdoptOwned(job);
            else
                ReleaseReservation(job);
            job.Stage = BasisModelJobStage.Finished;
            job.CleanGlb = null;
            job.Received = null;
        }

        private static void FailImport(BasisModelJob job, string reason)
        {
            if (job.Kind == BasisModelJobKind.Local)
            {
                RejectLocal(job, BasisLocalization.Get(BasisModelPickupPopups.ImportFailedKey, reason), false);
                return;
            }
            // Remote text: already capped and defused by the loader, and never logged as an error.
            BasisDebug.LogWarning($"Model pickup from {job.Sender} failed to load: {reason}", LogTag);
            RemoveModel(job.Id);
        }

        /// <summary>
        /// Ends a job: cancels its work, abandons its import, releases its reservation (once; see the pool's
        /// ownership rule) and gives up its seat in the size batch. Idempotent. The job stays listed until its
        /// running task lands, so the budget charge is returned exactly once.
        /// </summary>
        private static void KillJob(BasisModelJob job)
        {
            if (job.Dead)
                return;
            job.Dead = true;
            job.Cancel?.Cancel();
            if (job.Ticket != null)
                job.Ticket.Abandoned = true;
            ReleaseReservation(job);
            LeaveBatch(job);
            job.Prepared = null;
            job.Received = null;
            job.CleanGlb = null;
        }

        /// <summary>A job found dead: its placeholder goes with it unless something else already took it.</summary>
        private static void RetireJob(BasisModelJob job)
        {
            if (job.Dead)
                return;
            if (Models.TryGetValue(job.Id, out BasisModelPickupObject pickup) && ReferenceEquals(pickup, job.Pickup))
                RemoveModel(job.Id);
            KillJob(job);
        }

        private static void DropJob(int index)
        {
            BasisModelJob job = Jobs[index];
            ReleaseReservation(job);
            LeaveBatch(job);
            job.Cancel?.Dispose();
            job.Cancel = null;
            RemoveJobAt(index);
        }

        /// <summary>An import that landed for a job nobody wants any more is destroyed here.</summary>
        private static void DisposeUnclaimedResult(BasisModelJob job)
        {
            Task<BasisModelImportResult> import = job.ImportTask;
            job.PrepareTask = null;
            job.SanitizeTask = null;
            job.ValidateTask = null;
            job.ImportTask = null;
            if (import != null && import.Status == TaskStatus.RanToCompletion)
                import.Result?.Dispose();
        }

        private static void ReleaseReservation(BasisModelJob job)
        {
            if (job.ReservedBytes <= 0)
                return;
            BasisModelInboundReservations.Release(job.ReservedBytes);
            job.ReservedBytes = 0;
        }

        private static void LeaveBatch(BasisModelJob job)
        {
            BasisModelSizeBatch batch = job.Batch;
            if (batch == null)
                return;
            job.Batch = null;
            BasisModelSizeDialog.NotifyMemberRemoved(batch);
        }

        private static bool TryGetResult<T>(Task<T> task, out T result, out string failure)
        {
            if (task != null && task.Status == TaskStatus.RanToCompletion)
            {
                result = task.Result;
                failure = null;
                return true;
            }
            result = default;
            Exception exception = task?.Exception?.GetBaseException();
            failure = exception != null ? exception.GetType().Name + ": " + exception.Message : "the work was cancelled";
            return false;
        }

        private static void ClearSenderState()
        {
            _lastImportBySender.Clear();
            _nonCanonicalWarned.Clear();
            _handlerLogAt.Clear();
            _offerRefusalLogged = false;
            _invalidTransformLogged = false;
            _workPausedLogged = false;
        }

        private static void ForgetSender(ushort sender)
        {
            _lastImportBySender.Remove(sender);
            _nonCanonicalWarned.Remove(sender);
            _handlerLogAt.Remove(sender);
        }
    }
}
