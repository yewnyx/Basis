using System;
using Basis.BasisUI;
using Basis.EventDriver;

namespace Basis.ModelPickup
{
    /// <summary>
    /// The few main-menu operations the dialogs use. Edit-mode tests cannot build the real menu (it is a whole
    /// Addressables UI), so they swap in a fake through <see cref="BasisModelDialogs.Menu"/>.
    /// </summary>
    public interface IBasisModelDialogMenu
    {
        bool IsOpen { get; }
        void Open();
        void Close();
        BasisMenuDialoguePanel Dialogue { get; }

        /// <summary>One button when <paramref name="deny"/> is null, two otherwise. May show nothing.</summary>
        void OpenDialogue(string title, string description, string accept, string deny, Action<bool> callback);

        /// <summary>Runs <paramref name="action"/> once, on the next tick.</summary>
        void RunNextFrame(Action action);
    }

    /// <summary>
    /// The model pickup's popups, through one state so a notice never destroys an open size choice and the menu
    /// is put back the way it was. Main thread only.
    ///
    /// A notice (one button) replaces whatever dialogue is showing. If the
    /// menu was opened for it, the menu closes when it is answered, and a notice that replaces ours inherits that
    /// duty. A notice displaced unanswered goes to the notification centre as before, and answering it there
    /// later never closes the menu.
    ///
    /// A choice (two buttons) is never parked: it resolves exactly once as <see cref="BasisModelDialogChoice.Accept"/>,
    /// <see cref="BasisModelDialogChoice.Deny"/> or <see cref="BasisModelDialogChoice.Dismissed"/> (menu closed, tab
    /// switched, or <see cref="CloseChoice"/>). Notices raised while a choice is up wait in one slot, latest
    /// wins, and show once it resolves.
    /// </summary>
    public static class BasisModelDialogs
    {
        public static IBasisModelDialogMenu Menu = new MainMenuDialogs();

        private static readonly Action FlushDeferredNoticeAction = FlushDeferredNotice;

        private static int _lastToken;

        private static BasisMenuDialoguePanel _noticePanel;
        private static int _noticeToken;
        private static bool _noticeOpenedMenu;

        private static BasisMenuDialoguePanel _choicePanel;
        private static int _choiceToken;
        private static bool _choiceOpenedMenu;
        private static Action<BasisModelDialogChoice> _choiceCallback;

        private static bool _hasDeferredNotice;
        private static string _deferredTitle;
        private static string _deferredDescription;
        private static string _deferredAccept;

        public static bool IsChoiceOpen => _choiceToken != 0 && IsAlive(_choicePanel);

        public static void ShowNotice(string title, string description, string acceptLabel)
        {
            ResolveDeadChoice();
            if (IsChoiceOpen)
            {
                _hasDeferredNotice = true;
                _deferredTitle = title;
                _deferredDescription = description;
                _deferredAccept = acceptLabel;
                return;
            }

            FlushDeferredNotice();
            ShowNoticeNow(title, description, acceptLabel, false);
        }

        /// <summary>
        /// Shows a two-button prompt. False, with <paramref name="choiceToken"/> 0 and no callback ever, when the
        /// menu cannot open or someone else's dialogue is up (theirs is never released for ours). True means
        /// <paramref name="onResolved"/> runs exactly once, on the main thread, possibly in a later frame.
        /// </summary>
        public static bool TryShowChoice(
            string title,
            string description,
            string acceptLabel,
            string denyLabel,
            Action<BasisModelDialogChoice> onResolved,
            out int choiceToken
        )
        {
            choiceToken = 0;
            if (onResolved == null)
                throw new ArgumentNullException(nameof(onResolved));

            ResolveDeadChoice();
            FlushDeferredNotice();

            bool wasOpen = Menu.IsOpen;
            if (!wasOpen)
                Menu.Open();
            if (!Menu.IsOpen)
                return false;

            BasisMenuDialoguePanel current = Menu.Dialogue;
            bool inherited = false;
            if (IsAlive(current))
            {
                if (!ReferenceEquals(current, _noticePanel))
                {
                    if (!wasOpen)
                        Menu.Close();
                    return false;
                }
                inherited = _noticeOpenedMenu;
                ForgetNotice();
                current.ReleaseInstance();
            }

            int token = NextToken();
            _choiceToken = token;
            _choiceCallback = onResolved;
            Menu.OpenDialogue(
                title,
                description,
                acceptLabel,
                denyLabel ?? string.Empty,
                accepted => ResolveChoice(token, accepted ? BasisModelDialogChoice.Accept : BasisModelDialogChoice.Deny, true)
            );

            BasisMenuDialoguePanel panel = Menu.Dialogue;
            if (!IsAlive(panel) || ReferenceEquals(panel, current))
            {
                _choiceToken = 0;
                _choiceCallback = null;
                if (!wasOpen || inherited)
                    Menu.Close();
                return false;
            }

            _choicePanel = panel;
            _choiceOpenedMenu = !wasOpen || inherited;
            // Never parked in the notification centre: a choice answered minutes later from the bell would
            // resolve a batch long since settled. Our release handler runs after the panel's own two, and a
            // button press has already resolved by then, so it only ever reports a genuine dismissal.
            panel.CaptureOnClose = false;
            panel.OnInstanceReleased += () => ResolveChoice(token, BasisModelDialogChoice.Dismissed, false);
            choiceToken = token;
            return true;
        }

        /// <summary>
        /// Takes down the choice <paramref name="choiceToken"/> named, resolving it as
        /// <see cref="BasisModelDialogChoice.Dismissed"/> if it is still open. A stale or zero token does nothing.
        /// </summary>
        public static void CloseChoice(int choiceToken)
        {
            if (choiceToken == 0 || choiceToken != _choiceToken)
                return;
            BasisMenuDialoguePanel panel = _choicePanel;
            ResolveChoice(choiceToken, BasisModelDialogChoice.Dismissed, true);
            if (IsAlive(panel))
                panel.ReleaseInstance();
        }

        /// <param name="honourMenuDuty">
        /// False when the panel went because something else took it down (the menu closing or rebuilding, a tab
        /// switch). The menu is then left alone, and a waiting notice shows next frame instead of into a menu
        /// mid-teardown.
        /// </param>
        private static void ResolveChoice(int token, BasisModelDialogChoice choice, bool honourMenuDuty)
        {
            if (token == 0 || token != _choiceToken)
                return;

            Action<BasisModelDialogChoice> callback = _choiceCallback;
            BasisMenuDialoguePanel panel = _choicePanel;
            bool closeMenu = _choiceOpenedMenu && honourMenuDuty;
            _choiceToken = 0;
            _choiceCallback = null;
            _choicePanel = null;
            _choiceOpenedMenu = false;

            // A button press resolves before the panel releases itself. Take it down now so the menu is clear for
            // whatever the waiting notice or the callback shows next; the panel's own release then does nothing.
            if (honourMenuDuty && IsAlive(panel))
                panel.ReleaseInstance();

            // The waiting notice is older than anything the callback might raise, so it goes up first and a
            // newer notice from the callback replaces it the ordinary way.
            if (_hasDeferredNotice)
            {
                if (honourMenuDuty)
                {
                    TakeDeferredNotice(out string title, out string description, out string accept);
                    ShowNoticeNow(title, description, accept, closeMenu);
                    closeMenu = false;
                }
                else
                {
                    Menu.RunNextFrame(FlushDeferredNoticeAction);
                }
            }

            try
            {
                callback?.Invoke(choice);
            }
            catch (Exception exception)
            {
                BasisDebug.LogError($"A model pickup choice callback failed: {exception}", BasisDebug.LogTag.Pickups);
            }

            if (closeMenu)
                HandOffMenuDuty();
        }

        private static void ShowNoticeNow(string title, string description, string acceptLabel, bool inheritedMenuDuty)
        {
            bool wasOpen = Menu.IsOpen;
            if (!wasOpen)
                Menu.Open();
            if (!Menu.IsOpen)
                return;

            BasisMenuDialoguePanel current = Menu.Dialogue;
            bool inherit = inheritedMenuDuty;
            if (IsAlive(current))
            {
                if (ReferenceEquals(current, _noticePanel))
                    inherit |= _noticeOpenedMenu;
                ForgetNotice();
                // Our old notice, or whatever else was up, moves to the notification centre unanswered.
                current.ReleaseInstance();
            }

            int token = NextToken();
            _noticeToken = token;
            Menu.OpenDialogue(title, description, acceptLabel, null, _ => OnNoticeAnswered(token));

            BasisMenuDialoguePanel panel = Menu.Dialogue;
            if (!IsAlive(panel) || ReferenceEquals(panel, current))
            {
                _noticeToken = 0;
                if (!wasOpen || inherit)
                    Menu.Close();
                return;
            }

            _noticePanel = panel;
            _noticeOpenedMenu = !wasOpen || inherit;
            // A notice displaced or closed unanswered is parked in the notification centre with this callback.
            // Answering it there later must not close whatever menu happens to be open by then.
            panel.OnInstanceReleased += () => OnNoticeReleased(token);
        }

        private static void OnNoticeAnswered(int token)
        {
            if (token != _noticeToken)
                return;
            bool closeMenu = _noticeOpenedMenu;
            ForgetNotice();
            if (closeMenu)
                Menu.Close();
        }

        private static void OnNoticeReleased(int token)
        {
            if (token == _noticeToken)
                ForgetNotice();
        }

        /// <summary>
        /// The close-the-menu duty of a resolved choice passes to whatever of ours is showing now (the callback
        /// may have raised a notice or the next choice); the menu closes only when nothing of ours is.
        /// </summary>
        private static void HandOffMenuDuty()
        {
            if (IsChoiceOpen)
            {
                _choiceOpenedMenu = true;
                return;
            }
            if (_noticeToken != 0 && IsAlive(_noticePanel))
            {
                _noticeOpenedMenu = true;
                return;
            }
            Menu.Close();
        }

        /// <summary>A choice whose panel died without our release handler running still resolves, as dismissed.</summary>
        private static void ResolveDeadChoice()
        {
            if (_choiceToken != 0 && !IsAlive(_choicePanel))
                ResolveChoice(_choiceToken, BasisModelDialogChoice.Dismissed, false);
        }

        private static void FlushDeferredNotice()
        {
            if (!_hasDeferredNotice || IsChoiceOpen)
                return;
            TakeDeferredNotice(out string title, out string description, out string accept);
            ShowNoticeNow(title, description, accept, false);
        }

        private static void TakeDeferredNotice(out string title, out string description, out string accept)
        {
            title = _deferredTitle;
            description = _deferredDescription;
            accept = _deferredAccept;
            _hasDeferredNotice = false;
            _deferredTitle = null;
            _deferredDescription = null;
            _deferredAccept = null;
        }

        private static void ForgetNotice()
        {
            _noticeToken = 0;
            _noticePanel = null;
            _noticeOpenedMenu = false;
        }

        private static int NextToken()
        {
            _lastToken = _lastToken == int.MaxValue ? 1 : _lastToken + 1;
            return _lastToken;
        }

        /// <summary>A released panel lingers until the end of the frame in play mode; it is not a live dialogue.</summary>
        private static bool IsAlive(BasisMenuDialoguePanel panel)
        {
            return panel != null && !panel.IsReleased;
        }

        /// <summary>Forgets every prompt without touching the menu. Tests only.</summary>
        public static void ResetForTests()
        {
            ForgetNotice();
            _choicePanel = null;
            _choiceToken = 0;
            _choiceOpenedMenu = false;
            _choiceCallback = null;
            _hasDeferredNotice = false;
            _deferredTitle = null;
            _deferredDescription = null;
            _deferredAccept = null;
        }

        private sealed class MainMenuDialogs : IBasisModelDialogMenu
        {
            private Action _nextFrame;

            public bool IsOpen => BasisMainMenu.Instance != null;

            public void Open()
            {
                BasisMainMenu.Open();
            }

            public void Close()
            {
                BasisMainMenu.Close();
            }

            public BasisMenuDialoguePanel Dialogue => BasisMainMenu.Instance != null ? BasisMainMenu.Instance.Dialogue : null;

            public void OpenDialogue(string title, string description, string accept, string deny, Action<bool> callback)
            {
                BasisMenuBase<BasisMainMenu> menu = BasisMainMenu.Instance;
                if (menu == null)
                    return;
                // The menu refuses to open a dialogue over a live one, and in play mode a released panel stays a
                // live object until the end of the frame.
                if (menu.Dialogue != null && menu.Dialogue.IsReleased)
                    menu.Dialogue = null;
                if (deny == null)
                {
                    menu.OpenDialogue(title, description, accept, callback, category: BasisNotificationCategory.Content);
                    return;
                }
                menu.OpenDialogue(
                    title,
                    description,
                    accept,
                    deny,
                    callback,
                    divertible: false,
                    BasisPanelSeverity.None,
                    BasisNotificationCategory.Content
                );
            }

            public void RunNextFrame(Action action)
            {
                if (action == null)
                    return;
                bool subscribe = _nextFrame == null;
                _nextFrame -= action;
                _nextFrame += action;
                if (subscribe)
                    BasisEventDriver.OnUpdate += RunPending;
            }

            /// <summary>One-shot: unsubscribed before it runs, so a failure is logged once and never repeats.</summary>
            private void RunPending()
            {
                BasisEventDriver.OnUpdate -= RunPending;
                Action pending = _nextFrame;
                _nextFrame = null;
                try
                {
                    pending?.Invoke();
                }
                catch (Exception exception)
                {
                    BasisDebug.LogError($"A deferred model pickup notice failed: {exception}", BasisDebug.LogTag.Pickups);
                }
            }
        }
    }
}
