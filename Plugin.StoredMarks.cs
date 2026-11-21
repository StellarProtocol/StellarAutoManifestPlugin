using System;
using System.Globalization;
using System.Text;

namespace Stellar.AutoManifest;

/// <summary>
/// The "stored &lt;value&gt;" mark, persisted with its target row so it survives a game restart.
/// <para>When the run stores an imprint for a row it records WHICH pocket entry that was: the character
/// (<c>CharSerialize.charId</c>), the season, the entry's <c>uuid</c> + <c>entryId</c>, and the value text. The store
/// reply carries only <c>ret</c> (world_proxy.lua:13897-13944), so the uuid is learned by diffing the pocket
/// (<c>seasonTalentReforgeMap[sid].storeEntries</c>, map of {uuid, entryId}) from just before the store against the first
/// state read after it: the one new uuid holding the stored entryId.</para>
/// <para>A row keeps a COPY per store (<see cref="MarkCopy"/>): "Keep better rolls" (spec ⑦) stores a higher value for
/// a row that already has one and never removes the old copy, so the mark must follow every copy. The badge shows the
/// best copy still in the pocket; removing it falls back to the next; none left → the row is waiting again.</para>
/// <para>On every state read (≤ 1 Hz idle, per run step) the copies are checked against the CURRENT pocket, and only
/// when the pocket text, character or season moved (one string compare otherwise, no allocation —
/// Plugin-Performance-Rules §2):
/// <list type="bullet">
/// <item>copy of this character + season, uuid still in the pocket with the same entryId → active.</item>
/// <item>copy of this character + season, uuid gone (the player removed or used the imprint) → dropped + saved.</item>
/// <item>copy of ANOTHER character or season (config is per install, not per character) → inactive but KEPT: we can't
/// see that pocket, so we can't know the imprint is gone. Switching back shows it again. uuids are per character +
/// season counters (<c>nextUuid</c>), so the charId + season part is what keeps two characters' uuid 3 apart.</item>
/// <item>copy stored this session whose uuid is not resolved yet → active for this session only (never saved).</item>
/// </list>
/// Removing a row drops its mark; changing a row's minimum clears it (Plugin.Config.cs).</para>
/// </summary>
public sealed partial class Plugin
{
    private int _pendingMark = -1;   // target index stored this run whose newest copy's uuid is still unresolved
    private ReforgeState? _pocketBefore;   // the state read just before that store (its pocket = the "before" side)
    private string? _marksPocket;          // validation key: pocket text / character / season / marks version
    private string _marksChar = string.Empty;
    private int _marksSeason = -1, _marksVer, _marksCheckedVer = -1;

    private static bool KnownChar(string id) => id.Length > 0 && id != "0";

    // Called from AwaitStoreReply on ret == 0: a new copy for the row (the first one, or a better roll — spec ⑦); its
    // uuid follows from the next pocket read.
    private void MarkStored(int index, int entryId, ReforgeState before)
    {
        if (index < 0 || index >= _targets.Count) return;
        var t = _targets[index];
        t.Copies.Add(new MarkCopy
        {
            Text = ValueOf(entryId), Entry = entryId, Char = before.CharId, Season = before.SeasonId, Uuid = 0, Active = true,
        });
        t.RecomputeBest(_catalog);
        _pendingMark = index;
        _pocketBefore = before;
        MarksChanged();
    }

    private static MarkCopy? PendingCopy(Target t)
    {
        for (int i = t.Copies.Count - 1; i >= 0; i--) if (t.Copies[i].Uuid == 0) return t.Copies[i];
        return null;
    }

    // The first pocket after the store that holds a NEW uuid with the stored entryId names the entry. Unresolved after
    // the run (sync never showed it) → the copy stays for this session only and is not saved.
    private void ResolvePendingMark(ReforgeState st)
    {
        if (_pendingMark < 0 || _pocketBefore == null || !st.HasPocket) return;
        var c = _pendingMark < _targets.Count ? PendingCopy(_targets[_pendingMark]) : null;
        if (c == null) { _pendingMark = -1; _pocketBefore = null; return; }
        for (int i = 0; i < st.PocketUuid.Length; i++)
        {
            int uuid = st.PocketUuid[i];
            if (st.PocketEntry[i] != c.Entry || uuid == 0 || _pocketBefore.PocketHasUuid(uuid) || UuidMarked(uuid)) continue;
            c.Uuid = uuid;
            if (KnownChar(st.CharId)) c.Char = st.CharId;
            _services.Log.Info($"[AutoManifest] stored mark: target #{_pendingMark + 1} entry={c.Entry} uuid={uuid} "
                               + $"char={c.Char} season={c.Season} copies={_targets[_pendingMark].Copies.Count}");
            _pendingMark = -1;
            _pocketBefore = null;
            SaveTargets();
            MarksChanged();
            return;
        }
    }

    private bool UuidMarked(int uuid)
    {
        foreach (var t in _targets)
            foreach (var c in t.Copies)
                if (c.Uuid == uuid) return true;
        return false;
    }

    // State-read hook (RefreshMaterialsFromState, before UpdateReach).
    private void ValidateMarks()
    {
        if (!_st.Valid || !_st.HasPocket) return;
        ResolvePendingMark(_st);
        if (_marksCheckedVer == _marksVer && _marksSeason == _st.SeasonId && _marksChar == _st.CharId
            && string.Equals(_marksPocket, _st.PocketSrc, StringComparison.Ordinal)) return;
        _marksPocket = _st.PocketSrc; _marksChar = _st.CharId; _marksSeason = _st.SeasonId;

        bool flipped = false, cleared = false;
        bool here = KnownChar(_st.CharId);
        for (int i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            if (!t.HasMark) continue;
            for (int k = t.Copies.Count - 1; k >= 0; k--)
            {
                var c = t.Copies[k];
                bool active;
                if (c.Uuid == 0) active = true;   // stored this session, uuid not resolved yet
                else if (!here || c.Char != _st.CharId || c.Season != _st.SeasonId) active = false;
                else if (_st.PocketHolds(c.Uuid, c.Entry)) active = true;
                else
                {
                    _services.Log.Info($"[AutoManifest] stored mark cleared: target #{i + 1} uuid={c.Uuid} entry={c.Entry} "
                                       + $"is no longer in the pocket ({t.Copies.Count - 1} copies left)");
                    t.Copies.RemoveAt(k);
                    cleared = true;
                    continue;
                }
                c.Active = active;
            }
            var was = t.Best;
            if (t.RecomputeBest(_catalog))
            {
                flipped = true;
                if (was != null && t.Best != null)
                    _services.Log.Info($"[AutoManifest] stored mark: target #{i + 1} now shows entry={t.Best.Entry} "
                                       + $"uuid={t.Best.Uuid} (was entry={was.Entry} uuid={was.Uuid})");
            }
        }
        if (cleared) { SaveTargets(); flipped = true; }
        if (flipped) MarksChanged();
        _marksCheckedVer = _marksVer;
    }

    // A mark appeared / went / flipped: re-run reachability (stored rows leave the gate) and redraw the badges.
    private void MarksChanged()
    {
        _marksVer++;
        _targetsVer++;
        UpdateReach();
        MarkMain();
    }

    // Logout / character switch: nothing is confirmed until the next character's pocket is read.
    private void SuspendMarks()
    {
        foreach (var t in _targets)
        {
            t.Copies.RemoveAll(c => c.Uuid == 0);   // never resolved → can't be checked later
            foreach (var c in t.Copies) c.Active = false;
            t.Best = null;
        }
        _pendingMark = -1;
        _pocketBefore = null;
        _marksPocket = null;
        _marksVer++;
        _targetsVer++;
    }

    // ── config: "group:step" followed by one "char:season:uuid:entry:text" block per copy (text URL-escaped: no ':' /
    // ','). One copy = the original single-mark format "group:step:char:season:uuid:entry:text", so old configs load
    // unchanged (and an older build reading a new config still gets the first copy).
    private const int MarkFields = 5;

    private static void AppendMark(StringBuilder sb, Target t)
    {
        foreach (var c in t.Copies)
        {
            if (c.Uuid == 0) continue;   // unresolved copies are session-only
            sb.Append(':').Append(c.Char).Append(':').Append(c.Season.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(c.Uuid.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(c.Entry.ToString(CultureInfo.InvariantCulture))
              .Append(':').Append(Uri.EscapeDataString(c.Text));
        }
    }

    private static void ReadMark(string[] kv, Target t)
    {
        for (int i = 2; i + MarkFields <= kv.Length; i += MarkFields)
        {
            if (!KnownChar(kv[i])) continue;
            if (!int.TryParse(kv[i + 1], out var season) || !int.TryParse(kv[i + 2], out var uuid) || uuid == 0
                || !int.TryParse(kv[i + 3], out var entry)) continue;
            string text;
            try { text = Uri.UnescapeDataString(kv[i + 4]); } catch (UriFormatException) { continue; }
            // Active = false: confirmed against the pocket on the first state read.
            t.Copies.Add(new MarkCopy { Char = kv[i], Season = season, Uuid = uuid, Entry = entry, Text = text });
        }
    }
}
