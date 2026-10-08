using System;
using System.Collections.Generic;
using Basis.BasisUI;
using NUnit.Framework;
using UnityEngine;

namespace Basis.ModelPickup.Tests
{
    /// <summary>
    /// The prompt state machine, against a fake menu: the real one is a whole Addressables UI that edit mode
    /// cannot build. Panels are real <see cref="BasisMenuDialoguePanel"/> components on inactive objects, so
    /// their release events behave exactly as in play. What the real menu draws is a manual check.
    /// </summary>
    public sealed class BasisModelDialogsTests
    {
        private sealed class FakeMenu : IBasisModelDialogMenu
        {
            public readonly List<BasisMenuDialoguePanel> Created = new List<BasisMenuDialoguePanel>();
            public int Opens;
            public int Closes;
            public Action Pending;
            public bool RefuseDialogues;

            public bool IsOpen { get; set; }
            public BasisMenuDialoguePanel Dialogue { get; set; }

            public void Open()
            {
                Opens++;
                IsOpen = true;
            }

            /// <summary>The real close tears the menu down, taking any dialogue with it.</summary>
            public void Close()
            {
                Closes++;
                IsOpen = false;
                BasisMenuDialoguePanel current = Dialogue;
                Dialogue = null;
                if (current != null && !current.IsReleased)
                    current.ReleaseInstance();
            }

            public void OpenDialogue(string title, string description, string accept, string deny, Action<bool> callback)
            {
                if (RefuseDialogues || (Dialogue != null && !Dialogue.IsReleased))
                    return;
                var host = new GameObject("FakeDialogue " + title);
                host.SetActive(false);
                BasisMenuDialoguePanel panel = host.AddComponent<BasisMenuDialoguePanel>();
                panel.Title = title;
                panel.Description = description;
                panel.Accept = accept;
                panel.Decline = deny;
                panel.Callback = callback;
                Created.Add(panel);
                Dialogue = panel;
            }

            public void RunNextFrame(Action action)
            {
                Pending -= action;
                Pending += action;
            }

            public void Tick()
            {
                Action pending = Pending;
                Pending = null;
                pending?.Invoke();
            }

            /// <summary>As a button press: the callback first, then the panel releases itself.</summary>
            public void Press(BasisMenuDialoguePanel panel, bool accept)
            {
                panel.Callback?.Invoke(accept);
                if (panel != null && !panel.IsReleased)
                    panel.ReleaseInstance();
            }

            /// <summary>Something else taking the panel down (a tab switch, a foreign prompt).</summary>
            public void Displace(BasisMenuDialoguePanel panel)
            {
                panel.ReleaseInstance();
            }

            public BasisMenuDialoguePanel Last => Created.Count > 0 ? Created[Created.Count - 1] : null;
        }

        private IBasisModelDialogMenu _savedMenu;
        private FakeMenu _menu;
        private readonly List<BasisModelDialogChoice> _resolutions = new List<BasisModelDialogChoice>();

        [SetUp]
        public void SetUp()
        {
            _savedMenu = BasisModelDialogs.Menu;
            _menu = new FakeMenu();
            BasisModelDialogs.Menu = _menu;
            BasisModelDialogs.ResetForTests();
            _resolutions.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            BasisModelDialogs.ResetForTests();
            BasisModelDialogs.Menu = _savedMenu;
            for (int i = 0; i < _menu.Created.Count; i++)
            {
                if (_menu.Created[i] != null)
                    UnityEngine.Object.DestroyImmediate(_menu.Created[i].gameObject);
            }
        }

        private bool ShowChoice(out int token)
        {
            return BasisModelDialogs.TryShowChoice("Size", "Fit or original?", "Fit", "Original", _resolutions.Add, out token);
        }

        [Test]
        public void AnAnswerResolvesOnceAndRestoresTheMenu()
        {
            Assert.That(ShowChoice(out int token), Is.True);
            Assert.That(token, Is.Not.EqualTo(0));
            Assert.That(BasisModelDialogs.IsChoiceOpen, Is.True);
            Assert.That(_menu.Opens, Is.EqualTo(1));
            BasisMenuDialoguePanel panel = _menu.Last;
            Assert.That(panel.CaptureOnClose, Is.False, "a choice is never parked in the notification centre");

            _menu.Press(panel, false);

            Assert.That(_resolutions, Is.EqualTo(new[] { BasisModelDialogChoice.Deny }));
            Assert.That(BasisModelDialogs.IsChoiceOpen, Is.False);
            Assert.That(panel.IsReleased, Is.True);
            Assert.That(_menu.Closes, Is.EqualTo(1), "we opened the menu for it, so we close it");

            BasisModelDialogs.CloseChoice(token);
            Assert.That(_resolutions.Count, Is.EqualTo(1));
        }

        [Test]
        public void CloseChoiceResolvesDismissedOnce()
        {
            _menu.IsOpen = true;
            Assert.That(ShowChoice(out int token), Is.True);
            BasisMenuDialoguePanel panel = _menu.Last;

            BasisModelDialogs.CloseChoice(token);
            BasisModelDialogs.CloseChoice(token);
            BasisModelDialogs.CloseChoice(0);

            Assert.That(_resolutions, Is.EqualTo(new[] { BasisModelDialogChoice.Dismissed }));
            Assert.That(panel.IsReleased, Is.True);
            Assert.That(_menu.Closes, Is.EqualTo(0), "the menu was already open, so it stays");
        }

        [Test]
        public void ReleasingTheChoiceWithoutAnAnswerIsDismissed()
        {
            _menu.IsOpen = true;
            Assert.That(ShowChoice(out int token), Is.True);

            _menu.Close();

            Assert.That(_resolutions, Is.EqualTo(new[] { BasisModelDialogChoice.Dismissed }));
            Assert.That(_menu.Closes, Is.EqualTo(1), "a menu already closing is not closed again");
            BasisModelDialogs.CloseChoice(token);
            Assert.That(_resolutions.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADisplacedChoiceLeavesTheMenuToWhoeverDisplacedIt()
        {
            Assert.That(ShowChoice(out _), Is.True);

            _menu.Displace(_menu.Last);

            Assert.That(_resolutions, Is.EqualTo(new[] { BasisModelDialogChoice.Dismissed }));
            Assert.That(_menu.Closes, Is.EqualTo(0));
        }

        [Test]
        public void SomeoneElsesDialogueRefusesTheChoiceAndNeverCallsBack()
        {
            _menu.IsOpen = true;
            _menu.OpenDialogue("Foreign", "theirs", "OK", "No", _ => { });
            BasisMenuDialoguePanel foreign = _menu.Last;

            Assert.That(ShowChoice(out int token), Is.False);
            Assert.That(token, Is.EqualTo(0));
            Assert.That(foreign.IsReleased, Is.False, "their prompt is never released for ours");
            Assert.That(_resolutions, Is.Empty);

            _menu.IsOpen = false;
            _menu.Dialogue = null;
            _menu.RefuseDialogues = true;
            Assert.That(ShowChoice(out token), Is.False, "the menu opened but showed nothing");
            Assert.That(_menu.Closes, Is.EqualTo(1), "and is put back the way it was");
            Assert.That(_resolutions, Is.Empty);
        }

        [Test]
        public void ANoticeWaitsForTheChoiceAndShowsWhenItIsAnswered()
        {
            Assert.That(ShowChoice(out _), Is.True);
            BasisMenuDialoguePanel choice = _menu.Last;

            BasisModelDialogs.ShowNotice("First", "older", "OK");
            BasisModelDialogs.ShowNotice("Second", "latest wins", "OK");
            Assert.That(_menu.Created.Count, Is.EqualTo(1), "a notice never pushes out an open choice");

            _menu.Press(choice, true);

            Assert.That(_resolutions, Is.EqualTo(new[] { BasisModelDialogChoice.Accept }));
            Assert.That(_menu.Created.Count, Is.EqualTo(2));
            Assert.That(_menu.Last.Title, Is.EqualTo("Second"));
            Assert.That(_menu.Closes, Is.EqualTo(0), "the notice inherited closing the menu");

            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(1));
        }

        [Test]
        public void DeferredNoticeShowsWhenTheChoiceDies()
        {
            _menu.IsOpen = true;
            Assert.That(ShowChoice(out _), Is.True);
            BasisModelDialogs.ShowNotice("Waiting", "behind the choice", "OK");

            _menu.Displace(_menu.Last);
            Assert.That(_resolutions, Is.EqualTo(new[] { BasisModelDialogChoice.Dismissed }));
            Assert.That(_menu.Created.Count, Is.EqualTo(1), "not into a menu that may be mid-teardown");

            _menu.Tick();
            Assert.That(_menu.Created.Count, Is.EqualTo(2));
            Assert.That(_menu.Last.Title, Is.EqualTo("Waiting"));
        }

        [Test]
        public void TheNextPromptFlushesAWaitingNoticeFirst()
        {
            _menu.IsOpen = true;
            Assert.That(ShowChoice(out _), Is.True);
            BasisModelDialogs.ShowNotice("Waiting", "behind the choice", "OK");
            _menu.Displace(_menu.Last);

            BasisModelDialogs.ShowNotice("Newer", "arrives before the next tick", "OK");

            Assert.That(_menu.Created.Count, Is.EqualTo(3));
            Assert.That(_menu.Created[1].Title, Is.EqualTo("Waiting"));
            Assert.That(_menu.Created[1].IsReleased, Is.True, "replaced by the newer notice, as notices always were");
            Assert.That(_menu.Last.Title, Is.EqualTo("Newer"));
            _menu.Tick();
            Assert.That(_menu.Created.Count, Is.EqualTo(3), "nothing is shown twice");
        }

        [Test]
        public void AChoiceReplacesOurNoticeAndInheritsItsMenuDuty()
        {
            BasisModelDialogs.ShowNotice("Notice", "first", "OK");
            Assert.That(_menu.Opens, Is.EqualTo(1));
            BasisMenuDialoguePanel notice = _menu.Last;

            Assert.That(ShowChoice(out _), Is.True);
            Assert.That(notice.IsReleased, Is.True);
            Assert.That(_menu.Closes, Is.EqualTo(0));

            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(1), "the duty to close the menu came with the notice");
        }

        [Test]
        public void ANoticeAnsweredLaterFromTheBellNeverClosesTheMenu()
        {
            BasisModelDialogs.ShowNotice("Notice", "parked", "OK");
            BasisMenuDialoguePanel notice = _menu.Last;
            Action<bool> parkedCallback = notice.Callback;

            _menu.Displace(notice);
            _menu.IsOpen = true;
            parkedCallback(true);

            Assert.That(_menu.Closes, Is.EqualTo(0));
        }

        [Test]
        public void AReplacementNoticeInheritsClosingTheMenu()
        {
            BasisModelDialogs.ShowNotice("First", "opens the menu", "OK");
            BasisModelDialogs.ShowNotice("Second", "replaces it", "OK");
            Assert.That(_menu.Opens, Is.EqualTo(1));
            Assert.That(_menu.Created[0].IsReleased, Is.True);

            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(1));
        }

        [Test]
        public void AnAnsweredNoticeClosesTheMenuOnlyIfItOpenedIt()
        {
            BasisModelDialogs.ShowNotice("Opened", "the menu was closed", "OK");
            Assert.That(_menu.Opens, Is.EqualTo(1));
            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(1));

            _menu.IsOpen = true;
            BasisModelDialogs.ShowNotice("Joined", "the menu was already open", "OK");
            Assert.That(_menu.Opens, Is.EqualTo(1));
            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(1), "a menu the player opened stays open");
        }

        [Test]
        public void ANoticeTheMenuCannotShowPutsTheMenuBack()
        {
            _menu.RefuseDialogues = true;

            BasisModelDialogs.ShowNotice("Refused", "nothing is shown", "OK");

            Assert.That(_menu.Opens, Is.EqualTo(1));
            Assert.That(_menu.Closes, Is.EqualTo(1));
            Assert.That(_menu.Created, Is.Empty);
        }

        [Test]
        public void ANoticeReplacesSomeoneElsesDialogueWithoutTakingTheirMenu()
        {
            _menu.IsOpen = true;
            _menu.OpenDialogue("Foreign", "theirs", "OK", null, _ => { });
            BasisMenuDialoguePanel foreign = _menu.Last;

            BasisModelDialogs.ShowNotice("Ours", "replaces it", "OK");

            Assert.That(foreign.IsReleased, Is.True, "notices replace whatever is up");
            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(0), "the menu was already open");
        }

        [Test]
        public void ACallbackThatShowsANoticeKeepsTheMenuForIt()
        {
            Assert.That(
                BasisModelDialogs.TryShowChoice(
                    "Size",
                    "?",
                    "Fit",
                    "Original",
                    choice =>
                    {
                        _resolutions.Add(choice);
                        BasisModelDialogs.ShowNotice("Result", "raised by the callback", "OK");
                    },
                    out _
                ),
                Is.True
            );

            _menu.Press(_menu.Last, true);

            Assert.That(_menu.Last.Title, Is.EqualTo("Result"));
            Assert.That(_menu.Last.IsReleased, Is.False);
            Assert.That(_menu.Closes, Is.EqualTo(0), "the choice's duty passed to the notice it raised");
            _menu.Press(_menu.Last, true);
            Assert.That(_menu.Closes, Is.EqualTo(1));
        }

        [Test]
        public void AThrowingCallbackIsLoggedAndTheMenuStillRestored()
        {
            UnityEngine.TestTools.LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("A model pickup choice callback failed"));
            Assert.That(
                BasisModelDialogs.TryShowChoice("Size", "?", "Fit", "Original", _ => throw new InvalidOperationException("boom"), out _),
                Is.True
            );

            _menu.Press(_menu.Last, true);

            Assert.That(_menu.Closes, Is.EqualTo(1));
            Assert.That(BasisModelDialogs.IsChoiceOpen, Is.False);
        }
    }
}
