using System;
using System.Globalization;
using Basis.BasisUI;

namespace Basis.ModelPickup
{
    /// <summary>Shows and takes down the size choice. Production uses <see cref="BasisModelDialogs"/>; tests substitute.</summary>
    public interface IBasisModelSizePrompt
    {
        /// <summary>False when no prompt can be shown, and then <paramref name="onResolved"/> is never called.</summary>
        bool TryShow(string title, string description, string fitLabel, string originalLabel,
            Action<BasisModelDialogChoice> onResolved, out int token);

        /// <summary>Takes the prompt down; one still open resolves as Dismissed.</summary>
        void Close(int token);
    }

    /// <summary>
    /// The once-per-drop question "Fit or Original size?". Every model dropped while it is open joins the same
    /// batch and previews at Fit; nothing in the batch replicates until it is answered, because the answer is what
    /// receivers apply. Accept means Fit, Deny means Original, and anything else (closed unanswered, timed out, no
    /// prompt possible, the batch emptied) means Fit. Main thread only.
    /// </summary>
    public static class BasisModelSizeDialog
    {
        private const BasisDebug.LogTag LogTag = BasisDebug.LogTag.Pickups;

        private const string TitleKey = "modelPickup.size.title";
        private const string DescriptionKey = "modelPickup.size.description";
        private const string FitKey = "modelPickup.size.fit";
        private const string OriginalKey = "modelPickup.size.original";

        /// <summary>Raised once per decided batch, never for a cancelled one. The manager re-sizes and replicates the batch.</summary>
        public static Action<BasisModelSizeBatch> Resolved;

        public static IBasisModelSizePrompt Prompt = PickupDialogsPrompt.Instance;

        private static BasisModelSizeBatch _open;

        /// <summary>An undecided batch is waiting for its answer.</summary>
        public static bool IsOpen => _open != null && !_open.Decided;

        /// <summary>
        /// The batch a new drop belongs to: the one still waiting for its answer, or a new one with its prompt shown.
        /// When no prompt can be shown (another choice is up, or the menu cannot open), the new batch is Fit at once.
        /// </summary>
        public static BasisModelSizeBatch BeginBatch(float now)
        {
            if (IsOpen)
                return _open;

            var batch = new BasisModelSizeBatch(now);
            _open = batch;
            bool shown;
            try
            {
                shown = Prompt.TryShow(
                    BasisLocalization.Get(TitleKey),
                    BuildDescription(),
                    BuildFitLabel(),
                    BasisLocalization.Get(OriginalKey),
                    choice => Resolve(batch, BasisModelSizeBatch.ModeFor(choice)),
                    out batch.ChoiceToken
                );
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning("Model size dialog failed to open: " + e.Message, LogTag);
                shown = false;
            }
            if (!shown)
            {
                batch.ChoiceToken = 0;
                BasisDebug.Log("Model size dialog unavailable; using Fit.", LogTag);
                Resolve(batch, BasisModelSizeMode.Fit);
            }
            return batch;
        }

        /// <summary>An unanswered choice gives way to Fit after the timeout.</summary>
        public static void Tick(float now)
        {
            BasisModelSizeBatch batch = _open;
            if (batch == null)
                return;
            if (batch.Decided)
            {
                _open = null;
                return;
            }
            if (!batch.HasTimedOut(now, BasisModelPickupSettings.SizeDialogTimeoutSeconds))
                return;
            BasisDebug.Log("Model size dialog timed out; using Fit.", LogTag);
            Close(batch);
            Resolve(batch, BasisModelSizeMode.Fit);
        }

        /// <summary>A member was removed. When the open batch has none left, the question is moot: Fit, prompt closed.</summary>
        public static void NotifyMemberRemoved(BasisModelSizeBatch batch)
        {
            if (batch == null)
                return;
            if (batch.Members > 0)
                batch.Members--;
            if (batch.Members > 0 || batch.Decided || !ReferenceEquals(batch, _open))
                return;
            Close(batch);
            Resolve(batch, BasisModelSizeMode.Fit);
        }

        /// <summary>Leave and shutdown: the open batch ends without a resolution and its prompt comes down.</summary>
        public static void CancelAll()
        {
            BasisModelSizeBatch batch = _open;
            _open = null;
            if (batch == null)
                return;
            batch.Cancel();
            Close(batch);
        }

        public static string BuildDescription()
        {
            return BasisLocalization.Get(
                DescriptionKey,
                BasisModelSizing.FitLargestDimensionMeters,
                (int)Math.Round(BasisModelSizing.OriginalMinLargestDimensionMeters * 100f),
                BasisModelSizing.OriginalMaxLargestDimensionMeters
            );
        }

        public static string BuildFitLabel()
        {
            return BasisLocalization.Get(FitKey, BasisModelSizing.FitLargestDimensionMeters);
        }

        private static void Resolve(BasisModelSizeBatch batch, BasisModelSizeMode mode)
        {
            if (!batch.TryResolve(mode))
                return;
            if (ReferenceEquals(batch, _open))
                _open = null;
            if (batch.Cancelled)
                return;
            BasisDebug.Log(
                "Model size decided: " + batch.Mode.ToString() + " for "
                    + batch.Members.ToString(CultureInfo.InvariantCulture) + " model(s).",
                LogTag
            );
            try
            {
                Resolved?.Invoke(batch);
            }
            catch (Exception e)
            {
                BasisDebug.LogError("Model size resolution failed: " + e, LogTag);
            }
        }

        private static void Close(BasisModelSizeBatch batch)
        {
            int token = batch.ChoiceToken;
            batch.ChoiceToken = 0;
            if (token == 0)
                return;
            try
            {
                Prompt.Close(token);
            }
            catch (Exception e)
            {
                BasisDebug.LogWarning("Model size dialog failed to close: " + e.Message, LogTag);
            }
        }

        /// <summary>Forgets the open batch without closing anything. Tests only.</summary>
        public static void ResetForTests()
        {
            _open = null;
            Resolved = null;
            Prompt = PickupDialogsPrompt.Instance;
        }

        public sealed class PickupDialogsPrompt : IBasisModelSizePrompt
        {
            public static PickupDialogsPrompt Instance = new PickupDialogsPrompt();

            public bool TryShow(string title, string description, string fitLabel, string originalLabel,
                Action<BasisModelDialogChoice> onResolved, out int token)
            {
                return BasisModelDialogs.TryShowChoice(title, description, fitLabel, originalLabel, onResolved, out token);
            }

            public void Close(int token)
            {
                BasisModelDialogs.CloseChoice(token);
            }
        }
    }
}
