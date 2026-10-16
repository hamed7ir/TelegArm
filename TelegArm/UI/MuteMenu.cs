using System;
using System.Globalization;
using System.Windows.Forms;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T1 — mute for a while, not only forever: the four choices Telegram's apps offer, shared by the chat menu
    /// and the profile's Mute tile so the two can never offer different durations.
    /// </summary>
    internal static class MuteMenu
    {
        /// <summary>"Forever" — sent as ten years ahead, the value TelegArm has always written (TelegramService).</summary>
        public static readonly TimeSpan Forever = TimeSpan.MaxValue;

        private static readonly Tuple<string, TimeSpan>[] Choices =
        {
            Tuple.Create("For 1 hour", TimeSpan.FromHours(1)),
            Tuple.Create("For 8 hours", TimeSpan.FromHours(8)),
            Tuple.Create("For 2 days", TimeSpan.FromDays(2)),
            Tuple.Create("Forever", Forever),
        };

        /// <summary>Adds the four durations to a menu (or a submenu's DropDownItems). <paramref name="owner"/> marshals the
        /// pick past the menu's own closing, as every menu action here does.</summary>
        public static void AddChoices(ToolStripItemCollection items, Control owner, Action<TimeSpan> pick)
        {
            foreach (var c in Choices)
            {
                var d = c.Item2;
                var it = new ToolStripMenuItem(c.Item1);
                it.Click += (s, e) => { if (owner != null && owner.IsHandleCreated) owner.BeginInvoke((Action)(() => pick(d))); else pick(d); };
                items.Add(it);
            }
        }

        /// <summary>The mute_until for a duration (UTC).</summary>
        public static DateTime UntilUtc(TimeSpan d)
        {
            return d == Forever ? DateTime.UtcNow.AddYears(10) : DateTime.UtcNow + d;
        }

        /// <summary>"until 18:40" / "until Tue 18:40" for a mute that ends within a week; null for "forever" (or none).</summary>
        public static string Until(DateTime? untilUtc)
        {
            if (!untilUtc.HasValue) return null;
            var left = untilUtc.Value - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || left > TimeSpan.FromDays(7)) return null;
            var local = untilUtc.Value.ToLocalTime();
            string time = local.ToString("t", CultureInfo.CurrentCulture);
            return "until " + (local.Date == DateTime.Now.Date ? time : local.ToString("ddd", CultureInfo.CurrentCulture) + " " + time);
        }
    }
}
