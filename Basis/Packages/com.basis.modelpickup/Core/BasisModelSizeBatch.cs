
namespace Basis.ModelPickup
{
    /// <summary>
    /// One size decision shared by every model dropped while its prompt is open. Until it is decided the models
    /// preview at Fit and are not replicated; the decision reaches receivers only through the spawn tail.
    /// </summary>
    public sealed class BasisModelSizeBatch
    {
        public readonly float OpenedAt;
        public bool Decided;
        public bool Cancelled;
        public BasisModelSizeMode Mode;
        public int Members;

        /// <summary>The prompt's token from <c>BasisModelDialogs.TryShowChoice</c>; 0 when no prompt is open for this batch.</summary>
        public int ChoiceToken;

        /// <summary>The "props were locked while loading" notice has been shown for this batch, so it is shown once.</summary>
        public bool LockNoticeShown;

        public BasisModelSizeBatch(float openedAt)
        {
            OpenedAt = openedAt;
        }

        public BasisModelSizeMode EffectiveMode => Decided ? Mode : BasisModelSizeMode.Fit;

        /// <summary>
        /// The prompt's buttons are "Fit" (accept) and "Original size" (deny). Closing it unanswered, or any value this
        /// build does not know, means Fit: the safe size for a model of any authored scale.
        /// </summary>
        public static BasisModelSizeMode ModeFor(BasisModelDialogChoice choice)
        {
            return choice == BasisModelDialogChoice.Deny ? BasisModelSizeMode.Original : BasisModelSizeMode.Fit;
        }

        /// <summary>First call wins; later ones return false and change nothing. Unknown modes resolve as Fit.</summary>
        public bool TryResolve(BasisModelSizeMode mode)
        {
            if (Decided)
                return false;
            Decided = true;
            Mode = mode == BasisModelSizeMode.Original ? BasisModelSizeMode.Original : BasisModelSizeMode.Fit;
            return true;
        }

        /// <summary>
        /// Ends the batch without a resolution (leave, shutdown): an undecided batch becomes Fit, an earlier decision
        /// stands, and <see cref="Cancelled"/> tells the resolver to raise nothing.
        /// </summary>
        public void Cancel()
        {
            Cancelled = true;
            if (Decided)
                return;
            Decided = true;
            Mode = BasisModelSizeMode.Fit;
        }

        /// <summary>Only an undecided batch times out. A NaN time never does.</summary>
        public bool HasTimedOut(float now, float timeoutSeconds)
        {
            return !Decided && now - OpenedAt >= timeoutSeconds;
        }
    }
}
