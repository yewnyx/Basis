using Basis.BasisUI;
using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// Shows pickups' back panels only while the main menu is open. Polls rather than hooking an open/close
    /// event because the menu exposes none (it assigns and nulls its static instance), and polling stays right
    /// for every teardown path.
    ///
    /// Pickups converge on the menu's state a few per frame in both directions: toggling the menu in a busy
    /// instance would otherwise build or tear down every canvas at once, the very stall this gating avoids. The
    /// scan stops once every pickup agrees, so the steady state costs one null check per frame.
    /// </summary>
    public sealed class BasisModelBackPanelSync
    {
        public bool Visible;
        public bool Pending;

        /// <summary>A pickup was added (they are born without a panel), so the next update rescans.</summary>
        public void MarkPending()
        {
            Pending = true;
        }

        public void Reset()
        {
            Visible = false;
            Pending = false;
        }

        /// <summary>Converges the first <paramref name="count"/> of <paramref name="items"/> on the menu's state.</summary>
        public void Update<T>(T[] items, int count, int maxUpdates)
            where T : Component, IBasisModelBackPanelHost
        {
            Update(items, count, maxUpdates, BasisMainMenu.Instance != null);
        }

        /// <summary>As <see cref="Update{T}(T[],int,int)"/>, with the menu state supplied.</summary>
        public void Update<T>(T[] items, int count, int maxUpdates, bool menuOpen)
            where T : Component, IBasisModelBackPanelHost
        {
            if (menuOpen != Visible)
            {
                Visible = menuOpen;
                Pending = true;
            }

            if (!Pending)
                return;

            int budget = maxUpdates;
            for (int i = 0; i < count; i++)
            {
                T item = items[i];
                if (item == null || item.BackPanelVisible == Visible)
                    continue;
                item.SetBackPanelVisible(Visible);
                if (--budget <= 0)
                    return;
            }
            Pending = false;
        }
    }
}
