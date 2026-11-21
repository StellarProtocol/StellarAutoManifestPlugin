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
/// <para>On every state read (≤ 1 Hz idle, per run step) the marks are checked against the CURRENT pocket, and only when
/// the pocket text, character or season moved (one string compare otherwise, no allocation — Plugin-Performance-Rules §2):
/// <list type="bullet">
/// <item>mark of this character + season, uuid still in the pocket with the same entryId → shows "stored".</item>
/// <item>mark of this character + season, uuid gone (the player removed or used the imprint) → cleared + saved.</item>
/// <item>mark of ANOTHER character or season (config is per install, not per character) → shows "waiting" but is KEPT:
/// we can't see that pocket, so we can't know the imprint is gone. Switching back shows it again. uuids are per
/// character + season counters (<c>nextUuid</c>), so the charId + season part is what keeps two characters' uuid 3
/// apart.</item>
/// </list>
/// Removing a row drops its mark; changing a row's minimum clears it (Plugin.Config.cs).</para>
/// </summary>
public sealed partial class Plugin
{
    private int _pendingMark = -1;   // target index stored this run whose uuid is still unresolved
    private ReforgeState? _pocketBefore;   // the state read just before that store (its pocket = the "before" side)
    private string? _marksPocket;          // validation key: pocket text / character / season / marks version
    private string _marksChar = string.Empty;
    private int _marksSeason = -1, _marksVer, _marksCheckedVer = -1;

    private static bool KnownChar(string id) => id.Length > 0 && id != "0";

    // Called from AwaitStoreReply on ret == 0: the row is stored now; the uuid follows from the next pocket read.
    private void MarkStored(int index, int entryId, ReforgeState before)
    {
        if (index < 0 || index >= _targets.Count) return;
        var t = _targets[index];
        t.MarkText = ValueOf(entryId);
        t.MarkEntry = entryId;
        t.MarkChar = before.CharId;
        t.MarkSeason = before.SeasonId;
        t.MarkUuid = 0;
        t.Active = true;
        _pendingMark = index;
        _pocketBefore = before;
        MarksChanged();
    }

    // The first pocket after the store that holds a NEW uuid with the stored entryId names the entry. Unresolved after
    // the run (sync never showed it) → the mark stays for this session only and is not saved.
    private void ResolvePendingMark(ReforgeState st)
    {
        if (_pendingMark < 0 || _pocketBefore == null || !st.HasPocket) return;
        if (_pendingMark >= _targets.Count) { _pendingMark = -1; _pocketBefore = null; return; }
        var t = _targets[_pendingMark];
        if (!t.HasMark || t.MarkUuid != 0) { _pendingMark = -1; _pocketBefore = null; return; }
        for (int i = 0; i < st.PocketUuid.Length; i++)
        {
            int uuid = st.PocketUuid[i];
            if (st.PocketEntry[i] != t.MarkEntry || uuid == 0 || _pocketBefore.PocketHasUuid(uuid) || UuidMarked(uuid)) continue;
            t.MarkUuid = uuid;
            if (KnownChar(st.CharId)) t.MarkChar = st.CharId;
            _services.Log.Info($"[AutoManifest] stored mark: target #{_pendingMark + 1} entry={t.MarkEntry} uuid={uuid} "
                               + $"char={t.MarkChar} season={t.MarkSeason}");
            _pendingMark = -1;
            _pocketBefore = null;
            SaveTargets();
            MarksChanged();
            return;
        }
    }

    private bool UuidMarked(int uuid)
    {
        foreach (var t in _targets) if (t.HasMark && t.MarkUuid == uuid) return true;
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
        for (int i = 0; i < _targets.Count; i++)
        {
            var t = _targets[i];
            if (!t.HasMark) continue;
            bool active;
            if (t.MarkUuid == 0) active = i == _pendingMark;   // stored this session, uuid not resolved yet
            else if (!KnownChar(_st.CharId) || t.MarkChar != _st.CharId || t.MarkSeason != _st.SeasonId) active = false;
            else if (_st.PocketHolds(t.MarkUuid, t.MarkEntry)) active = true;
            else
            {
                _services.Log.Info($"[AutoManifest] stored mark cleared: target #{i + 1} uuid={t.MarkUuid} entry={t.MarkEntry} "
                                   + "is no longer in the pocket");
                t.ClearMark();
                cleared = flipped = true;
                continue;
            }
            if (t.Active != active) { t.Active = active; flipped = true; }
        }
        if (cleared) SaveTargets();
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
            if (t.HasMark && t.MarkUuid == 0) t.ClearMark();   // never resolved → can't be checked later
            t.Active = false;
        }
        _pendingMark = -1;
        _pocketBefore = null;
        _marksPocket = null;
        _marksVer++;
        _targetsVer++;
    }

    // ── config: "group:step" or "group:step:char:season:uuid:entry:text" (text URL-escaped: no ':' / ',') ──────────
    private static void AppendMark(StringBuilder sb, Target t)
    {
        if (!t.HasMark || t.MarkUuid == 0) return;   // unresolved marks are session-only
        sb.Append(':').Append(t.MarkChar).Append(':').Append(t.MarkSeason.ToString(CultureInfo.InvariantCulture))
          .Append(':').Append(t.MarkUuid.ToString(CultureInfo.InvariantCulture))
          .Append(':').Append(t.MarkEntry.ToString(CultureInfo.InvariantCulture))
          .Append(':').Append(Uri.EscapeDataString(t.MarkText!));
    }

    private static void ReadMark(string[] kv, Target t)
    {
        if (kv.Length < 7 || !KnownChar(kv[2])) return;
        if (!int.TryParse(kv[3], out var season) || !int.TryParse(kv[4], out var uuid) || uuid == 0
            || !int.TryParse(kv[5], out var entry)) return;
        string text;
        try { text = Uri.UnescapeDataString(kv[6]); } catch (UriFormatException) { return; }
        t.MarkChar = kv[2]; t.MarkSeason = season; t.MarkUuid = uuid; t.MarkEntry = entry; t.MarkText = text;
        t.Active = false;   // confirmed against the pocket on the first state read
    }
}
