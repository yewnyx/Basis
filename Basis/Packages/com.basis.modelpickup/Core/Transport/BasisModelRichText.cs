using System.IO;

namespace Basis.ModelPickup
{
    /// <summary>Helpers for putting untrusted text into rich-text (TMP) labels and popups.</summary>
    public static class BasisModelRichText
    {
        /// <summary>
        /// Neutralises rich-text tags. Ampersands go first so the entities added for angle brackets are not
        /// escaped a second time. Null becomes empty.
        /// </summary>
        public static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>
        /// The last path segment for display. Some runtimes throw on characters they consider invalid in a
        /// path; the full path is shown instead rather than losing the popup.
        /// </summary>
        public static string FileNameForDisplay(string path)
        {
            try
            {
                return Path.GetFileName(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
