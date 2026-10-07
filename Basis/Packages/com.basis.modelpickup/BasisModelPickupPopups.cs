using System.Text;
using Basis.BasisUI;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Notices for the local user's own drops, through <see cref="BasisModelDialogs"/> so they wait behind an open
    /// size choice instead of replacing it. Models other players share never raise a popup; their failures are logged.
    /// </summary>
    public static class BasisModelPickupPopups
    {
        private const string RejectedTitleKey = "modelPickup.popup.rejected.title";
        private const string LimitTitleKey = "modelPickup.popup.limit.title";
        private const string AcceptKey = "modelPickup.popup.accept";
        private const string UnknownFileKey = "modelPickup.popup.unknownFile";
        private const string DefaultReasonKey = "modelPickup.popup.defaultReason";
        private const string RejectionDescriptionKey = "modelPickup.popup.rejection.description";
        private const string LimitSummaryKey = "modelPickup.popup.limit.summary";
        private const string LimitAllowedSomeKey = "modelPickup.popup.limit.allowedSome";
        private const string LimitNoneImportedKey = "modelPickup.popup.limit.noneImported";

        public const string AdminLockedKey = "modelPickup.popup.reason.adminLocked";
        public const string AdminLockedDuringLoadKey = "modelPickup.popup.reason.adminLockedDuringLoad";
        public const string NoPermissionKey = "modelPickup.popup.reason.noPermission";
        public const string TextureRejectedKey = "modelPickup.popup.reason.textureRejected";
        public const string ImportFailedKey = "modelPickup.popup.reason.importFailed";
        public const string MemoryBudgetKey = "modelPickup.popup.reason.memoryBudget";

        public static void ShowRejected(string label, string reason)
        {
            Show(BasisLocalization.Get(RejectedTitleKey), BuildDescription(label, reason));
        }

        /// <summary>A rejection for exceeding a limit, under the limit title.</summary>
        public static void ShowOverLimit(string label, string reason)
        {
            Show(BasisLocalization.Get(LimitTitleKey), BuildDescription(label, reason));
        }

        public static void ShowLimit(int currentCount, int requestedCount)
        {
            Show(BasisLocalization.Get(LimitTitleKey), BuildBatchNotice(currentCount, requestedCount, 0));
        }

        /// <summary>Only when some of the batch was refused for the model limit.</summary>
        public static void ShowBatchNotice(int currentCount, int requestedCount, int allowedCount)
        {
            if (allowedCount >= requestedCount)
                return;
            Show(BasisLocalization.Get(LimitTitleKey), BuildBatchNotice(currentCount, requestedCount, allowedCount));
        }

        public static string BuildDescription(string label, string reason)
        {
            string fileName = BasisModelRichText.FileNameForDisplay(label);
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = BasisLocalization.Get(UnknownFileKey);
            if (string.IsNullOrWhiteSpace(reason))
                reason = BasisLocalization.Get(DefaultReasonKey);
            return BasisLocalization.Get(
                RejectionDescriptionKey,
                BasisModelRichText.Escape(fileName),
                BasisModelRichText.Escape(reason)
            );
        }

        public static string BuildBatchNotice(int currentCount, int requestedCount, int allowedCount)
        {
            int limit = BasisModelPickupSettings.Limits.Sender.MaxModels;
            if (currentCount < 0)
                currentCount = 0;
            if (requestedCount < 0)
                requestedCount = 0;
            if (allowedCount < 0)
                allowedCount = 0;
            else if (allowedCount > requestedCount)
                allowedCount = requestedCount;

            var description = new StringBuilder(256);
            description.Append(BasisLocalization.Get(LimitSummaryKey, limit, currentCount));
            if (allowedCount > 0)
                description.Append(BasisLocalization.Get(LimitAllowedSomeKey, allowedCount, requestedCount));
            else
                description.Append(BasisLocalization.Get(LimitNoneImportedKey));
            return description.ToString();
        }

        private static void Show(string title, string description)
        {
            BasisModelDialogs.ShowNotice(title, description, BasisLocalization.Get(AcceptKey));
        }
    }
}
