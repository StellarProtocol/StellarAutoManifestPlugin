namespace Stellar.AutoManifest;

/// <summary>
/// "Keep better rolls" at a copy cap (approved ⑫): when a row already holds its cap of plugin copies, a better roll is
/// stored and then its LOWEST plugin copy is discarded. Order, one request at a time (the run's shared
/// <c>__stam_ret</c>):
/// <list type="number">
/// <item>Store the new copy (the normal store path, Plugin.Run.cs) and wait for the store sync.</item>
/// <item><see cref="RunPhase.AwaitMark"/>: wait until the new copy's uuid is confirmed by the pocket diff
/// (Plugin.StoredMarks.cs). Not confirmed in time → no discard (an extra copy is harmless; discarding blind is not).</item>
/// <item>Re-verify the old copy on a FRESH state read: still one of this row's marks, uuid known, not the row's best,
/// this character + season, and its uuid still in the live <c>storeEntries</c> with the same entryId. Any miss → no
/// discard. Only copies the plugin stored for this row are ever candidates (the Matcher picked
/// <see cref="Target.Lowest"/>), so pre-existing / user-stored imprints are never touched.</item>
/// <item><see cref="RunPhase.Discarding"/>: <c>M.discard(uuid)</c> = the pocket window's own call
/// (<c>AsyncRemoveStoredSeasonTalentReforgeEntry</c>). ret ≠ 0 or no reply → the run ends with an Error line.</item>
/// <item><see cref="RunPhase.AwaitDiscard"/>: the copy leaves the mark at once; wait for the pocket sync so the slot
/// count is right for the next store, then re-match.</item>
/// </list>
/// A Stop pressed mid-swap is honoured right after it (Match checks it): a store is never left without its discard.
/// </summary>
public sealed partial class Plugin
{
    private MarkCopy? _swapVictim;   // the plugin copy this store swaps out (set in Match from Target.Lowest)
    private MarkCopy? _swapNew;      // the copy the store just added (its uuid resolves from the pocket diff)
    private int _runDiscarded;       // copies discarded by swaps this run (end-line suffix)
    private int _endDiscardEntry;    // DiscardFailed: the copy that couldn't be discarded ("Couldn't discard <name> <value>")
    private string _endDiscardText = string.Empty;

    // Called from AwaitStoreReply right after MarkStored: the newest copy of the row is the one just stored.
    private void NoteSwapCopy()
    {
        var copies = _targets[_plan.TargetIndex].Copies;
        _swapNew = copies.Count > 0 ? copies[copies.Count - 1] : null;
    }

    private void AwaitSwapMark(float now)
    {
        // A fresh read every step: it names the new uuid (ValidateMarks → ResolvePendingMark) and is the "live
        // storeEntries" the old copy is re-checked against. An unreadable state = not confirmed yet.
        var st = ReadState();
        if (st.Valid) { _st = st; RefreshMaterialsFromState(); }
        if (!st.Valid || _swapNew is not { Uuid: not 0 })
        {
            if (now - _phaseAt <= SyncTimeoutS) return;
            LogSwapSkip(st.Valid ? "the new copy's uuid was not confirmed by the pocket" : "reforge data unreadable");
            FinishSwap(now, discarded: false);
            return;
        }
        var skip = SwapVictimProblem();
        if (skip != null) { LogSwapSkip(skip); FinishSwap(now, discarded: false); return; }

        var v = _swapVictim!;
        var t = _targets[_plan.TargetIndex];
        _services.Log.Info($"[AutoManifest] discard → uuid={v.Uuid} entry={v.Entry} {EntryText(v.Entry)} value={v.Text} "
                           + $"for target #{_plan.TargetIndex + 1} · reason: at cap {KeepCopies} (active copies={t.ActiveCount}), "
                           + $"lowest plugin copy, replaced by uuid={_swapNew.Uuid} entry={_swapNew.Entry} {EntryText(_swapNew.Entry)} "
                           + $"pocket={_st.Stored}/{_st.StoreLimit}");
        if (!StartDiscard(v.Uuid)) { End(EndReason.LeftWorld); return; }
        Enter(RunPhase.Discarding);
    }

    // null = safe to discard; otherwise why not (logged).
    private string? SwapVictimProblem()
    {
        var v = _swapVictim;
        if (v == null) return "no copy picked";
        if (_plan.TargetIndex < 0 || _plan.TargetIndex >= _targets.Count) return "target row is gone";
        var t = _targets[_plan.TargetIndex];
        if (!t.Copies.Contains(v)) return "the copy is no longer marked for this row";
        if (v.Uuid == 0) return "the copy's uuid is unknown";
        if (ReferenceEquals(v, t.Best) || ReferenceEquals(v, _swapNew)) return "the copy is the row's best";
        if (v.Char != _st.CharId || v.Season != _st.SeasonId) return "the copy belongs to another character or season";
        if (!_st.Valid || !_st.HasPocket || !_st.PocketHolds(v.Uuid, v.Entry)) return "the uuid is not in the live pocket";
        return null;
    }

    private void LogSwapSkip(string reason)
    {
        var v = _swapVictim;
        _services.Log.Info($"[AutoManifest] discard skipped: uuid={v?.Uuid ?? 0} entry={v?.Entry ?? 0} "
                           + $"{EntryText(v?.Entry ?? 0)} value={v?.Text ?? "?"} for target #{_plan.TargetIndex + 1} · reason: {reason}");
    }

    private void AwaitSwapDiscard(float now)
    {
        var v = _swapVictim!;
        if (!TryTakeRet(out var ret))
        {
            if (now - _phaseAt <= RequestTimeoutS) return;
            _services.Log.Warning($"[AutoManifest] discard: no reply within {RequestTimeoutS:0} s (uuid={v.Uuid} entry={v.Entry})");
            FailDiscard(v);
            return;
        }
        _lastReplyAt = now;
        _services.Log.Info($"[AutoManifest] discard ← ret={ret}{ErrSuffix(ret)} uuid={v.Uuid} entry={v.Entry} {EntryText(v.Entry)} "
                           + $"for target #{_plan.TargetIndex + 1} after {(now - _phaseAt) * 1000f:0} ms");
        if (ret != 0) { FailDiscard(v); return; }
        _runDiscarded++;
        // The mark follows at once (the pocket sync is waited for below): the badge, the pocket window's "target" tag
        // and the cap count no longer see the copy.
        var t = _targets[_plan.TargetIndex];
        t.Copies.Remove(v);
        t.RecomputeBest(_catalog);
        SaveTargets();
        MarksChanged();
        Enter(RunPhase.AwaitDiscard);
        AwaitSwapDiscardSync(now);
    }

    // The reply was ok: wait for the pocket to drop the uuid so the next store's slot check counts right.
    private void AwaitSwapDiscardSync(float now)
    {
        var v = _swapVictim!;
        var st = ReadState();
        bool gone = st.Valid && st.HasPocket && !st.PocketHasUuid(v.Uuid);
        if (!gone && now - _phaseAt <= SyncTimeoutS) return;
        if (!gone) _services.Log.Warning($"[AutoManifest] pocket still lists uuid={v.Uuid} {SyncTimeoutS:0} s after its discard");
        if (st.Valid) { _st = st; RefreshMaterialsFromState(); }
        FinishSwap(now, discarded: true);
    }

    private void FailDiscard(MarkCopy v)
    {
        _endDiscardEntry = v.Entry;
        _endDiscardText = v.Text;
        End(EndReason.DiscardFailed);
    }

    // Footer, then back to matching: "Pull 77: better <name> 5.12% · discarded 2.72% · Pocket 3/5", or ⑦'s
    // "(was 2.72%)" line when nothing was discarded.
    private void FinishSwap(float now, bool discarded)
    {
        if (discarded) SetFooter(FooterEvent.Swapped, _plan.EntryId, _swapVictim!.Entry);
        else SetFooter(FooterEvent.Better, _plan.EntryId, _planWasEntry);
        _swapVictim = null;
        _swapNew = null;
        Enter(RunPhase.Matching);
        Match(now);
    }

    // ── text ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // Confirm card, amber (spec ⑫), only with a cap set: Best only has its own wording; Up to N names N.
    private string? ConfirmCapLine()
    {
        int cap = KeepCopies;
        if (cap <= 0) return null;   // Off (nothing) or No cap (⑦'s muted line in the Message)
        var line = cap == 1 ? _loc.T(KeyConfirmBetterBest) : _loc.TFormat(KeyConfirmBetterCap, cap);
        var ink = EnsureContrast(CautionColor(), TileColor(), PillMinContrast);
        return "<color=#" + HexOf(ink) + ">" + line + "</color>";
    }

    private static string? JoinParagraphs(string? a, string? b)
        => a == null ? b : b == null ? a : a + "\n\n" + b;

    // " · 2 discarded" (spec ⑫), only when a swap discarded at least one copy.
    private string EndDiscardSuffix() => _endDiscarded > 0 ? _loc.TFormat(KeyEndDiscarded, _endDiscarded) : string.Empty;

    // "Couldn't discard Great Crimson Lotus 2.72% — check your pocket"
    private string EndDiscardFailedText() => _loc.TFormat(KeyEndDiscardFailed, EntryName(_endDiscardEntry),
        _endDiscardText.Length > 0 ? _endDiscardText : ValueOf(_endDiscardEntry));

    // RUN-row dropdown captions, in KeepCopiesChoices order; rebuilt on a language switch only.
    private string[] _keepCopiesOptions = System.Array.Empty<string>();
    private int _keepCopiesOptionsGen = -1;

    private string[] KeepCopiesOptions()
    {
        if (_keepCopiesOptionsGen == _langGen && _keepCopiesOptions.Length > 0) return _keepCopiesOptions;
        _keepCopiesOptionsGen = _langGen;
        var o = new string[KeepCopiesChoices.Length];
        for (int i = 0; i < o.Length; i++)
            o[i] = KeepCopiesChoices[i] switch
            {
                0  => _loc.T(KeyOff),
                1  => _loc.T(KeyKeepBest),
                -1 => _loc.T(KeyKeepNoCap),
                var n => _loc.TFormat(KeyKeepUpTo, n),
            };
        return _keepCopiesOptions = o;
    }
}
