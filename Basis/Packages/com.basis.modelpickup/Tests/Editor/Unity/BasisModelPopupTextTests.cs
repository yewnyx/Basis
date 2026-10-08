using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    /// <summary>The text of the local user's notices, in English (the localization fallback).</summary>
    public sealed class BasisModelPopupTextTests
    {
        [Test]
        public void RejectionDescriptionEscapesFileAndReason()
        {
            string description = BasisModelPickupPopups.BuildDescription("/tmp/chair<b>.glb", "Bounds < limit & more");

            Assert.That(description, Does.Contain("chair&lt;b&gt;.glb"));
            Assert.That(description, Does.Contain("Bounds &lt; limit &amp; more"));
            Assert.That(description, Does.Contain("<b>File:</b>"));
            Assert.That(description, Does.Contain("<b>Reason:</b>"));
        }

        [Test]
        public void BatchNoticeShowsLimitAndCurrent()
        {
            int limit = BasisModelPickupSettings.Limits.Sender.MaxModels;

            string some = BasisModelPickupPopups.BuildBatchNotice(6, 4, 2);
            Assert.That(some, Does.Contain("<b>" + limit + "</b> shared models"));
            Assert.That(some, Does.Contain("<b>6</b> active or pending"));
            Assert.That(some, Does.Contain("Only <b>2</b> of the <b>4</b>"));

            string none = BasisModelPickupPopups.BuildBatchNotice(limit, 3, 0);
            Assert.That(none, Does.Contain("No additional models were imported."));
        }

        [Test]
        public void SizeDescriptionFormatsConstants()
        {
            string description = BasisModelSizeDialog.BuildDescription();

            Assert.That(description, Does.Contain("is 0.5 m"));
            Assert.That(description, Does.Contain("between 2 cm and 4 m"));
            Assert.That(BasisModelSizeDialog.BuildFitLabel(), Is.EqualTo("Fit (0.5 m)"));
        }
    }
}
