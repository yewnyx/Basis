using System;
using System.Threading;
using System.Threading.Tasks;
using Basis.ModelPickup.Validation;

namespace Basis.ModelPickup
{
    public enum BasisModelJobKind : byte
    {
        /// <summary>A file this client dropped: read, prepared, images sanitised, canonicalised, imported.</summary>
        Local,

        /// <summary>A model another player sent: validated against this device's limits, then imported.</summary>
        Inbound,
    }

    /// <summary>
    /// Where a job is. The Await stages are queued for a budget slot; the -ing stages have a task running. Local jobs
    /// go Queued, Preparing, (AwaitSanitize, Sanitizing)*, AwaitCanonical, Canonicalizing, AwaitImport, Importing;
    /// inbound jobs go Queued, Validating, AwaitImport, Importing.
    /// </summary>
    public enum BasisModelJobStage : byte
    {
        Queued,
        Preparing,
        AwaitSanitize,
        Sanitizing,
        AwaitCanonical,
        Canonicalizing,
        Validating,
        AwaitImport,
        Importing,

        /// <summary>Adopted by its pickup; the manager drops the job.</summary>
        Finished,
    }

    /// <summary>
    /// One model on its way from bytes to an adopted pickup. The manager polls it once per tick: nothing awaits it,
    /// so a stage's result is only ever consumed on the main thread, after liveness has been checked again. At most
    /// one task runs at a time, and its budget charge is released exactly once, when the manager sees it complete.
    /// </summary>
    public sealed class BasisModelJob
    {
        public BasisModelJobKind Kind;
        public BasisModelJobStage Stage;
        public Guid Id;

        /// <summary>The placeholder this job fills. The job is alive only while the manager still tracks this exact object.</summary>
        public BasisModelPickupObject Pickup;

        public bool Dead;

        /// <summary>Since when the job has waited in Queued or AwaitImport, for the inbound wait limit.</summary>
        public float WaitingSince;

        /// <summary>The network identity's connection generation when the job began; inbound work dies with its connection.</summary>
        public int Generation;

        /// <summary>Created with the job, cancelled when it dies, disposed when it is dropped.</summary>
        public CancellationTokenSource Cancel;

        /// <summary>The running task's budget charge and which budget it came from, while <see cref="HoldsBudget"/>.</summary>
        public bool HoldsBudget;
        public long Charge;
        public bool ChargedMainThread;

        /// <summary>Names the job in log lines and popups: the dropped path for local jobs, "player N" for inbound ones.</summary>
        public string Label;

        // Local jobs.
        public string Path;
        public long SourceBytes;
        public BasisModelSizeBatch Batch;
        public float GroundY;
        public BasisGlbPreparedModel Prepared;
        public bool HasBounds;
        public BasisGlbAabb Bounds;
        public int NextImage;
        public long SanitizedBytes;

        // Inbound jobs.
        public ushort Sender;
        public byte[] Received;

        /// <summary>Inbound bytes this job holds in the model's inbound pool, moved here from the assembly; zeroed when released.</summary>
        public long ReservedBytes;

        // Both kinds.
        /// <summary>Header tail as received, or the sender's own once its stats are known.</summary>
        public BasisModelSpawnTail Tail;

        public BasisGlbStats Stats;
        public byte[] CleanGlb;
        public BasisModelImportTicket Ticket;

        public Task<BasisGlbPrepareResult> PrepareTask;
        public Task<BasisModelImageSanitizeResult> SanitizeTask;
        public Task<BasisGlbValidationResult> ValidateTask;
        public Task<BasisModelImportResult> ImportTask;

        /// <summary>The task of the running stage, or null between stages.</summary>
        public Task RunningTask
        {
            get
            {
                if (PrepareTask != null)
                    return PrepareTask;
                if (SanitizeTask != null)
                    return SanitizeTask;
                if (ValidateTask != null)
                    return ValidateTask;
                return ImportTask;
            }
        }
    }
}
