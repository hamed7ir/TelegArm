using System;
using System.Windows.Forms;
using TelegArm.UI.Controls;

namespace TelegArm.UI.Admin
{
    /// <summary>
    /// Redesign T21 — how a new invite link behaves: when it stops working and how many people it may let in. The admin's
    /// InviteLinksForm opens this before messages.exportChatInvite; the defaults are the old behaviour (never, no limit).
    /// </summary>
    public sealed class InviteOptionsForm : DialogForm
    {
        private static readonly string[] ExpireLabels = { "Never", "1 hour", "1 day", "1 week" };
        private static readonly int[] ExpireHours = { 0, 1, 24, 24 * 7 };
        private static readonly string[] LimitLabels = { "No limit", "1", "10", "100" };
        private static readonly int[] Limits = { 0, 1, 10, 100 };

        private readonly SegmentedRow _expire, _limit;

        /// <summary>When the link stops working (UTC), or null for never.</summary>
        public DateTime? ExpireDate
        {
            get
            {
                int i = Math.Max(0, _expire.SelectedIndex);
                return ExpireHours[i] > 0 ? DateTime.UtcNow.AddHours(ExpireHours[i]) : (DateTime?)null;
            }
        }

        /// <summary>How many people may join through it, or null for no limit.</summary>
        public int? UsageLimit
        {
            get
            {
                int i = Math.Max(0, _limit.SelectedIndex);
                return Limits[i] > 0 ? Limits[i] : (int?)null;
            }
        }

        public InviteOptionsForm() : base("New invite link", 400, 300)
        {
            _expire = new SegmentedRow("Expires", ExpireLabels, 0);
            _limit = new SegmentedRow("People who can join", LimitLabels, 0);
            PopulateBody(_expire, _limit);
            var create = AddFooterButton("Create", RoundedButtonKind.Primary, DialogResult.OK);   // first added = rightmost
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            AcceptButton = create;
            FitToContent();
        }
    }
}
