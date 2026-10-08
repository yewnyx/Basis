using System;
using System.Collections.Generic;
using NUnit.Framework;

namespace Basis.ModelPickup.Tests
{
    /// <summary>The size question's state machine against a fake prompt; what the real menu draws is a manual check.</summary>
    public sealed class BasisModelSizeDialogTests
    {
        private FakePrompt _prompt;
        private readonly List<BasisModelSizeBatch> _resolved = new List<BasisModelSizeBatch>();

        [SetUp]
        public void SetUp()
        {
            BasisModelPickupManager.Shutdown();
            BasisModelSizeDialog.ResetForTests();
            _prompt = new FakePrompt();
            BasisModelSizeDialog.Prompt = _prompt;
            _resolved.Clear();
            BasisModelSizeDialog.Resolved = batch => _resolved.Add(batch);
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelSizeDialog.CancelAll();
            BasisModelSizeDialog.ResetForTests();
        }

        [TestCase(BasisModelDialogChoice.Accept, (byte)BasisModelSizeMode.Fit)]
        [TestCase(BasisModelDialogChoice.Deny, (byte)BasisModelSizeMode.Original)]
        [TestCase(BasisModelDialogChoice.Dismissed, (byte)BasisModelSizeMode.Fit)]
        public void AcceptIsFitDenyIsOriginalAndDismissalIsFit(BasisModelDialogChoice choice, byte expectedMode)
        {
            BasisModelSizeBatch batch = BasisModelSizeDialog.BeginBatch(0f);
            Assert.That(_prompt.Shown, Is.EqualTo(1));
            Assert.That(BasisModelSizeDialog.BeginBatch(1f), Is.SameAs(batch), "a drop while the choice is open joins it");
            Assert.That(_prompt.Shown, Is.EqualTo(1), "one prompt per batch");
            Assert.That(batch.Decided, Is.False);

            _prompt.Answer(choice);

            Assert.That(batch.Decided, Is.True);
            Assert.That((byte)batch.Mode, Is.EqualTo(expectedMode));
            Assert.That(_resolved, Is.EqualTo(new[] { batch }));
            Assert.That(BasisModelSizeDialog.IsOpen, Is.False);
        }

        [Test]
        public void UnavailablePromptResolvesFitAtOnce()
        {
            _prompt.Available = false;

            BasisModelSizeBatch batch = BasisModelSizeDialog.BeginBatch(0f);

            Assert.That(batch.Decided, Is.True);
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Fit));
            Assert.That(batch.ChoiceToken, Is.EqualTo(0));
            Assert.That(_resolved, Is.EqualTo(new[] { batch }));
            Assert.That(BasisModelSizeDialog.IsOpen, Is.False);
        }

        [Test]
        public void TimeoutClosesTheChoiceAndResolvesFit()
        {
            BasisModelSizeBatch batch = BasisModelSizeDialog.BeginBatch(10f);
            int token = batch.ChoiceToken;

            BasisModelSizeDialog.Tick(10f + BasisModelPickupSettings.SizeDialogTimeoutSeconds - 1f);
            Assert.That(batch.Decided, Is.False);

            BasisModelSizeDialog.Tick(10f + BasisModelPickupSettings.SizeDialogTimeoutSeconds);
            Assert.That(_prompt.Closed, Is.EqualTo(new[] { token }));
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Fit));
            Assert.That(_resolved, Is.EqualTo(new[] { batch }));
        }

        [Test]
        public void RemovingTheLastMemberClosesTheChoiceAsFit()
        {
            BasisModelSizeBatch batch = BasisModelSizeDialog.BeginBatch(0f);
            batch.Members = 2;
            int token = batch.ChoiceToken;

            BasisModelSizeDialog.NotifyMemberRemoved(batch);
            Assert.That(batch.Decided, Is.False);

            BasisModelSizeDialog.NotifyMemberRemoved(batch);
            Assert.That(_prompt.Closed, Is.EqualTo(new[] { token }));
            Assert.That(batch.Mode, Is.EqualTo(BasisModelSizeMode.Fit));
            Assert.That(_resolved, Is.EqualTo(new[] { batch }));
        }

        [Test]
        public void CancelAllClosesTheChoiceWithoutResolving()
        {
            BasisModelSizeBatch batch = BasisModelSizeDialog.BeginBatch(0f);
            int token = batch.ChoiceToken;

            BasisModelSizeDialog.CancelAll();

            Assert.That(batch.Cancelled, Is.True);
            Assert.That(_prompt.Closed, Is.EqualTo(new[] { token }));
            Assert.That(_resolved, Is.Empty);
            Assert.That(BasisModelSizeDialog.IsOpen, Is.False);
        }

        /// <summary>Behaves as the shared dialogs do: one callback per shown prompt, Dismissed when closed unanswered.</summary>
        private sealed class FakePrompt : IBasisModelSizePrompt
        {
            public bool Available = true;
            public int Shown;
            public readonly List<int> Closed = new List<int>();

            private Action<BasisModelDialogChoice> _callback;
            private int _token;

            public bool TryShow(string title, string description, string fitLabel, string originalLabel,
                Action<BasisModelDialogChoice> onResolved, out int token)
            {
                token = 0;
                if (!Available)
                    return false;
                Shown++;
                _token = Shown;
                _callback = onResolved;
                token = _token;
                return true;
            }

            public void Close(int token)
            {
                Closed.Add(token);
                if (token == _token)
                    Answer(BasisModelDialogChoice.Dismissed);
            }

            public void Answer(BasisModelDialogChoice choice)
            {
                Action<BasisModelDialogChoice> callback = _callback;
                _callback = null;
                _token = 0;
                callback?.Invoke(choice);
            }
        }
    }
}
