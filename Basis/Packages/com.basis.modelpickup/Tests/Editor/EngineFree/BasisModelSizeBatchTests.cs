using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    public class BasisModelSizeBatchTests
    {
        [Test]
        public void ResolvesOnce()
        {
            var batch = new BasisModelSizeBatch(10f);
            Assert.That(batch.TryResolve(BasisModelSizeMode.Original), Is.True);
            Assert.That(batch.TryResolve(BasisModelSizeMode.Fit), Is.False);
            Assert.That(batch.Decided, Is.True);
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Original));
            Assert.That(batch.EffectiveMode, Is.EqualTo(BasisModelSizeMode.Original));
        }

        [Test]
        public void AnUndecidedBatchPreviewsFit()
        {
            var batch = new BasisModelSizeBatch(10f);
            batch.Mode = BasisModelSizeMode.Original;
            Assert.That(batch.Decided, Is.False);
            Assert.That(batch.EffectiveMode, Is.EqualTo(BasisModelSizeMode.Fit));
        }

        [Test]
        public void TheTimeoutIsHonoured()
        {
            var batch = new BasisModelSizeBatch(10f);
            Assert.That(batch.OpenedAt, Is.EqualTo(10f));
            Assert.That(batch.HasTimedOut(129.9f, 120f), Is.False);
            Assert.That(batch.HasTimedOut(130f, 120f), Is.True);
            Assert.That(batch.HasTimedOut(float.NaN, 120f), Is.False);

            batch.TryResolve(BasisModelSizeMode.Fit);
            Assert.That(batch.HasTimedOut(1000f, 120f), Is.False, "a decided batch never times out");
        }

        [TestCase(BasisModelDialogChoice.Accept, false)]
        [TestCase(BasisModelDialogChoice.Deny, true)]
        [TestCase(BasisModelDialogChoice.Dismissed, false)]
        [TestCase((BasisModelDialogChoice)99, false)]
        public void AcceptAndDismissMeanFitAndDenyMeansOriginal(BasisModelDialogChoice choice, bool original)
        {
            Assert.That(BasisModelSizeBatch.ModeFor(choice),
                Is.EqualTo(original ? BasisModelSizeMode.Original : BasisModelSizeMode.Fit));
        }

        [Test]
        public void CancelDecidesFitWithoutAResolution()
        {
            var batch = new BasisModelSizeBatch(0f);
            batch.Cancel();
            Assert.That(batch.Cancelled, Is.True);
            Assert.That(batch.Decided, Is.True);
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Fit));
            Assert.That(batch.TryResolve(BasisModelSizeMode.Original), Is.False);
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Fit));
        }

        [Test]
        public void CancelKeepsAnEarlierDecision()
        {
            var batch = new BasisModelSizeBatch(0f);
            batch.TryResolve(BasisModelSizeMode.Original);
            batch.Cancel();
            Assert.That(batch.Cancelled, Is.True);
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Original));
        }

        [Test]
        public void AnUnknownModeResolvesAsFit()
        {
            var batch = new BasisModelSizeBatch(0f);
            Assert.That(batch.TryResolve((BasisModelSizeMode)7), Is.True);
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Fit));
        }

        [Test]
        public void ANewBatchHoldsNoPromptOrNotice()
        {
            var batch = new BasisModelSizeBatch(3f);
            Assert.That(batch.ChoiceToken, Is.EqualTo(0));
            Assert.That(batch.LockNoticeShown, Is.False);
            Assert.That(batch.Members, Is.EqualTo(0));
            Assert.That(batch.Cancelled, Is.False);
        }
    }
}
