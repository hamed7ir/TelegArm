using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using TelegArm.Core;
using TelegArm.UI.Controls;
using TL;

namespace TelegArm.UI
{
    /// <summary>
    /// Redesign T13 — create or edit a chat folder (a dialogFilter): its name, the kinds of chat it takes in, what it leaves
    /// out, and the chats it always / never shows. Editing starts from a FRESH copy of the folder (the caller re-reads it)
    /// and carries every field it does not edit across verbatim — emoticon, colour, title entities, and the peers of the
    /// lists that are not in the loaded chat list (the picker only shows loaded chats; the rest are kept, never dropped).
    /// The caller sends <see cref="Result"/> with messages.updateDialogFilter.
    /// </summary>
    public sealed class FolderEditForm : DialogForm
    {
        private const string Caption = "Folder";

        private readonly TL.DialogFilter _src;
        private readonly int _id;
        private readonly List<ChatEntry> _chats;
        private readonly HashSet<long> _loaded;
        private readonly Func<long, Image> _cachedAvatar;
        private readonly Func<long, IPeerInfo, Task<Image>> _loadAvatar;

        private readonly TextRow _name;
        private readonly ToggleRow _contacts, _nonContacts, _groups, _channels, _bots, _muted, _read, _archived;
        private readonly ActionRow _included, _excluded;

        private List<InputPeer> _pin, _inc, _exc;

        public TL.DialogFilter Result { get; private set; }

        public FolderEditForm(TL.DialogFilter src, int newId, List<ChatEntry> chats,
                              Func<long, Image> cachedAvatar, Func<long, IPeerInfo, Task<Image>> loadAvatar)
            : base(src == null ? "New folder" : "Edit folder", 400, 640)
        {
            _src = src; _id = src != null ? src.id : newId;
            _chats = chats ?? new List<ChatEntry>();
            _loaded = new HashSet<long>(_chats.Select(c => c.PeerId));
            _cachedAvatar = cachedAvatar; _loadAvatar = loadAvatar;
            _pin = new List<InputPeer>(src?.pinned_peers ?? new InputPeer[0]);
            _inc = new List<InputPeer>(src?.include_peers ?? new InputPeer[0]);
            _exc = new List<InputPeer>(src?.exclude_peers ?? new InputPeer[0]);
            var fl = src != null ? src.flags : 0;
            Func<TL.DialogFilter.Flags, bool> has = f => (fl & f) != 0;

            string title = "";
            try { title = src?.title?.text ?? ""; } catch { }
            _name = new TextRow("Folder name", title) { Hint = "e.g. Work", MaxLength = 12 };
            _contacts = new ToggleRow("Contacts", has(TL.DialogFilter.Flags.contacts));
            _nonContacts = new ToggleRow("Non-contacts", has(TL.DialogFilter.Flags.non_contacts));
            _groups = new ToggleRow("Groups", has(TL.DialogFilter.Flags.groups));
            _channels = new ToggleRow("Channels", has(TL.DialogFilter.Flags.broadcasts));
            _bots = new ToggleRow("Bots", has(TL.DialogFilter.Flags.bots));
            _included = new ActionRow("Always show these chats", "Choose");
            _included.Click += (s, e) => PickChats(true);
            _muted = new ToggleRow("Muted chats", has(TL.DialogFilter.Flags.exclude_muted));
            _read = new ToggleRow("Read chats", has(TL.DialogFilter.Flags.exclude_read));
            _archived = new ToggleRow("Archived chats", has(TL.DialogFilter.Flags.exclude_archived));
            _excluded = new ActionRow("Never show these chats", "Choose");
            _excluded.Click += (s, e) => PickChats(false);
            ShowCounts();

            PopulateBody(_name,
                         new SectionHeader("Include"), _contacts, _nonContacts, _groups, _channels, _bots, _included,
                         new SectionHeader("Leave out"), _muted, _read, _archived, _excluded);

            var save = AddFooterButton("Save", RoundedButtonKind.Primary, DialogResult.None);   // first added = rightmost
            save.Click += (s, e) => OnSave();
            CancelButton = AddFooterButton("Cancel", RoundedButtonKind.Neutral, DialogResult.Cancel);
            FocusOnShow(_name.Field.Inner);
            FitToContent();
        }

        private void ShowCounts()
        {
            int i = _pin.Count + _inc.Count, x = _exc.Count;
            _included.Subtitle = i == 0 ? "None" : i == 1 ? "1 chat" : i + " chats";
            _excluded.Subtitle = x == 0 ? "None" : x == 1 ? "1 chat" : x + " chats";
        }

        /// <summary>Picks the loaded chats of one list. Peers the picker cannot show (not loaded) stay as they were; a chat
        /// put in one list leaves the other.</summary>
        private void PickChats(bool include)
        {
            var now = new HashSet<long>((include ? _pin.Concat(_inc) : _exc).Select(MainForm.PeerIdOf));
            List<ChatEntry> picked;
            using (var f = new ForwardPickerDialog(_chats, include ? "Always show" : "Never show", "Done", now, _cachedAvatar, _loadAvatar))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                picked = f.SelectedChats;
            }
            var sel = new HashSet<long>(picked.Select(c => c.PeerId));
            Func<InputPeer, bool> keep = p => { long id = MainForm.PeerIdOf(p); return !_loaded.Contains(id) || sel.Contains(id); };
            if (include)
            {
                _pin = _pin.Where(keep).ToList();
                _inc = _inc.Where(keep).ToList();
                var have = new HashSet<long>(_pin.Concat(_inc).Select(MainForm.PeerIdOf));
                foreach (var c in picked) if (have.Add(c.PeerId)) _inc.Add(c.Peer);
                _exc = _exc.Where(p => !sel.Contains(MainForm.PeerIdOf(p))).ToList();
            }
            else
            {
                _exc = _exc.Where(keep).ToList();
                var have = new HashSet<long>(_exc.Select(MainForm.PeerIdOf));
                foreach (var c in picked) if (have.Add(c.PeerId)) _exc.Add(c.Peer);
                _pin = _pin.Where(p => !sel.Contains(MainForm.PeerIdOf(p))).ToList();
                _inc = _inc.Where(p => !sel.Contains(MainForm.PeerIdOf(p))).ToList();
            }
            ShowCounts();
        }

        private void OnSave()
        {
            string title = (_name.Value ?? "").Trim();
            if (title.Length == 0) { ThemedDialog.Show(this, Caption, "Give the folder a name.", "OK"); return; }
            const TL.DialogFilter.Flags Edited = TL.DialogFilter.Flags.contacts | TL.DialogFilter.Flags.non_contacts | TL.DialogFilter.Flags.groups
                | TL.DialogFilter.Flags.broadcasts | TL.DialogFilter.Flags.bots | TL.DialogFilter.Flags.exclude_muted
                | TL.DialogFilter.Flags.exclude_read | TL.DialogFilter.Flags.exclude_archived;
            var flags = (_src != null ? _src.flags : 0) & ~Edited;   // has_emoticon / has_color / title_noanimate carried
            if (_contacts.On) flags |= TL.DialogFilter.Flags.contacts;
            if (_nonContacts.On) flags |= TL.DialogFilter.Flags.non_contacts;
            if (_groups.On) flags |= TL.DialogFilter.Flags.groups;
            if (_channels.On) flags |= TL.DialogFilter.Flags.broadcasts;
            if (_bots.On) flags |= TL.DialogFilter.Flags.bots;
            if (_muted.On) flags |= TL.DialogFilter.Flags.exclude_muted;
            if (_read.On) flags |= TL.DialogFilter.Flags.exclude_read;
            if (_archived.On) flags |= TL.DialogFilter.Flags.exclude_archived;
            bool anyType = (flags & (TL.DialogFilter.Flags.contacts | TL.DialogFilter.Flags.non_contacts | TL.DialogFilter.Flags.groups
                                   | TL.DialogFilter.Flags.broadcasts | TL.DialogFilter.Flags.bots)) != 0;
            if (!anyType && _pin.Count + _inc.Count == 0)
            {
                ThemedDialog.Show(this, Caption, "Choose a kind of chat to include, or at least one chat.", "OK");
                return;
            }
            string old = "";
            try { old = _src?.title?.text ?? ""; } catch { }
            Result = new TL.DialogFilter
            {
                flags = flags,
                id = _id,
                // the title object itself when the name did not change (its entities and all), a new one when it did
                title = _src != null && _src.title != null && old == title ? _src.title : new TextWithEntities { text = title, entities = new MessageEntity[0] },
                emoticon = _src?.emoticon,
                color = _src != null ? _src.color : 0,
                pinned_peers = _pin.ToArray(),
                include_peers = _inc.ToArray(),
                exclude_peers = _exc.ToArray(),
            };
            DialogResult = DialogResult.OK;
        }
    }
}
