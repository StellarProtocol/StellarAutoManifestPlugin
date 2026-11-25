using System;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The run loop. A C# state machine driven from the plugin tick at ≤10 Hz (Plugin-Performance-Rules §1); each server
/// request runs in a game coroutine (Lua <c>M.roll</c> / <c>M.store</c>) and parks its <c>ret</c> in a global that the
/// tick polls — no Lua→C# callback. One cycle:
/// <list type="number">
/// <item>Pre-checks (all stored · pocket ≥ limit − keep-free · next pull over the spend limit · Remnants short · Blessing
/// on and Dawn Prism short) → end with the matching line.</item>
/// <item>Roll <c>AsyncSeasonTalentReforge</c>; ret ≠ 0 → Error.</item>
/// <item>Unknown #1: the reply carries no results → wait until the results signature (lastReforgeTime + every entryId)
/// differs from the one read before the roll, ~5 s timeout.</item>
/// <item>Match (pure <see cref="Matcher"/>) and store ONE plan; unknown #2: after every store wait for the results /
/// pocket to change, then RE-READ and re-match from scratch (indices may shift).</item>
/// <item>≥ 1000 ms after the last reply before the next roll (the game's own button CD is 800 ms and the VM call
/// bypasses it).</item>
/// </list>
/// Stop (button, window close) takes effect only between requests, never with one in flight. Leaving the world, a
/// loading screen or logout ends the run at once (nothing can be sent then anyway). Every roll and store is logged with
/// the <c>[AutoManifest]</c> prefix — that log is how the two unknowns get validated in game.
/// </summary>
public sealed partial class Plugin
{
    private const float RequestTimeoutS = 15f;
    private const float SyncTimeoutS    = 5f;
    private const float RollGapS        = 1.0f;
    private const int   StorageFullCode = 7580;

    private enum RunPhase : byte { Idle, Cycle, Rolling, AwaitResults, Matching, Storing, AwaitStore, Cooldown, AwaitMark, Discarding, AwaitDiscard }
    private enum EndReason : byte
    {
        None, AllStored, PocketFull, SpendLimit, NoRemnants, NoPrism, Refused, StoreRefused, Timeout, UserStop, LeftWorld,
        DiscardFailed,   // a capped "Keep better rolls" swap could not discard the old copy (spec ⑫)
        NoSpecial,       // fewer special items than the next pull needs (spec ⑬, Plugin.Special.cs)
        SpecialBlocked,  // every still-waiting target is of a category the picked item can't roll (spec ⑬)
    }

    private RunPhase _phase;
    private bool Running => _phase != RunPhase.Idle;
    private bool _stopRequested;
    private float _phaseAt, _nextRollAt, _lastReplyAt;
    private int _runPulls, _rollTimes, _rollCost, _storedBefore;
    private int _runBetter;      // "Keep better rolls" stores this run (spec ⑦)
    private int _planWasEntry;   // upgrade plan: the entryId of the best copy it beats (footer "was 2.72%")
    private long _runSpent;
    private long _runStartRemnants;   // Remnants owned at Start: the progress bar's whole when there is no spend limit (spec ⑪)
    private bool _rollBless;
    private string _sigBefore = string.Empty;
    private ReforgeState _st = new();
    private ReforgeState _storeBefore = new();
    private ReforgeResults _res = new();
    private StorePlan _plan;

    // ── start / stop ──────────────────────────────────────────────────────────────────────────────────────────────
    // Startable with at least one WAITING target the live table knows (a "stored" row stays stored across runs and restarts
    // while the pocket still holds its imprint — Plugin.StoredMarks.cs),
    // and not when NONE of them can roll at the current level (spec ⑥ — the footer then says why, Plugin.Reach.cs).
    // Not while a pocket discard is in flight either: it parks its reply in the same __stam_ret the run polls.
    private bool CanStart() => !Running && !_discardBusy && _catalogReady && _st.Valid && InWorld() && WaitingCount() > 0
                               && !ReachBlocksStart();

    private int KnownTargetCount()
    {
        int n = 0;
        foreach (var t in _targets) if (_catalog != null && _catalog.ByGroup.ContainsKey(t.Group)) n++;
        return n;
    }

    private int WaitingCount()
    {
        int n = 0;
        foreach (var t in _targets) if (!t.Stored && _catalog != null && _catalog.ByGroup.ContainsKey(t.Group)) n++;
        return n;
    }

    private void OnStartClicked()
    {
        CommitSpendDraft();   // a typed limit still in the field counts (the Start click can beat the focus-loss tick)
        if (!CanStart()) return;
        ClosePicker();
        _confirm.Show(new ConfirmRequest(_loc.T(KeyConfirmTitle), ConfirmBody(), BeginRun)
        {
            ConfirmLabel = _loc.T(KeyStart), CancelLabel = _loc.T(KeyCancel), Severity = ConfirmSeverity.Primary,
            // amber "2 targets can't roll at Lv 2 yet: …" and/or the capped "Keep better rolls" line (spec ⑫); null = neither
            // + the special item lines first (spec ⑬, Plugin.Special.cs)
            Emphasis = JoinParagraphs(ConfirmSpecialLines(), JoinParagraphs(ConfirmReachLine(), ConfirmCapLine())),
        });
    }

    // "Spends up to 240 Lightchase Factor Remnant and 20 Dawn Prism. …" — counted in WHOLE batches of the chosen pull
    // size, like the run (Cycle stops before a batch that would pass the limit): batches = limit / (cost × size), e.g.
    // 300 / (12 × 10) = 2 → 240 Remnants, 20 Prism (one per pull). "Up to": the cost per pull can rise if the level
    // goes up mid-run, which only lowers these. The Prism part only when Blessing is on and usable.
    // No spend limit (0, spec ⑪): its own sentence instead — there is nothing to count.
    private string ConfirmBody()
    {
        var body = SpendLimit > 0 ? ConfirmSpendLine() : _loc.TFormat(KeyConfirmNoLimit, RemnantName());
        // spec ⑦: its own muted paragraph, only with "No cap" (spec ⑫ — a set cap gets its amber line in the Emphasis
        // instead, Off gets nothing; the card's Message is the muted text). The Message is a plain wrapping uGUI Text,
        // so "\n" does break — but one break read as the same paragraph (in-game 2026-11-22); a blank line separates it.
        return KeepCap == Matcher.NoCap ? body + "\n\n" + _loc.T(KeyConfirmBetter) : body;
    }

    private string ConfirmSpendLine()
    {
        int size = PullTimes;
        int batchCost = _st.CostPerPull * size;
        int batches = batchCost > 0 ? SpendLimit / batchCost : 0;
        long remnants = (long)batches * batchCost;
        long prisms = (long)batches * size;
        return _useBlessing && _st.BlessAllowed
            ? _loc.TFormat(KeyConfirmBless, N0(remnants), RemnantName(), N0(prisms), PrismName())
            : _loc.TFormat(KeyConfirmBody, N0(remnants), RemnantName());
    }

    private void BeginRun()
    {
        if (!CanStart()) return;
        _runPulls = 0; _runSpent = 0; _runBetter = 0; _runDiscarded = 0; _stopRequested = false;
        _swapVictim = null;
        _runStartRemnants = _st.Remnants;
        BeginSpecial();   // fixes the special item for the whole run (spec ⑬) — while the phase is still Idle
        _lastReplyAt = float.NegativeInfinity;
        _endReason = EndReason.None;
        SetFooter(FooterEvent.Starting);
        _services.Log.Info($"[AutoManifest] run start: targets={DescribeTargets()} pullSize={PullTimes} keepCopies={KeepCopies} blessing={_useBlessing}"
                           + $"(allowed={_st.BlessAllowed}) spendLimit={SpendLimit} keepFree={KeepFree} level={_st.Level} "
                           + $"pocket={_st.Stored}/{_st.StoreLimit} remnants={_st.Remnants} prisms={_st.Prisms} exp={_st.Exp} "
                           + $"rollNow={_rollNowTargets} notYet={_unreachTargets} reachable={_reachableTargets} "
                           + $"special={_runSpecialId}(have={_st.SpecialCount(_runSpecialId)} lib={SpecialById(_runSpecialId)?.Lib ?? 0} "
                           + $"blocked={BlockedWaitingCount()})");
        Enter(RunPhase.Cycle);
    }

    private void OnStopClicked()
    {
        if (!Running || _stopRequested) return;
        _stopRequested = true;
        SetFooter(FooterEvent.Stopping);
        _services.Log.Info($"[AutoManifest] stop requested (phase {_phase})");
    }

    private void Enter(RunPhase p)
    {
        _phase = p;
        _phaseAt = FwNow;
        MarkMain();
    }

    // ── tick (≤10 Hz, from Plugin.OnUpdate) ───────────────────────────────────────────────────────────────────────
    private void RunTick(float now)
    {
        if (!InWorld()) { End(EndReason.LeftWorld); return; }
        switch (_phase)
        {
            case RunPhase.Cycle:        Cycle(now); break;
            case RunPhase.Rolling:      AwaitRoll(now); break;
            case RunPhase.AwaitResults: AwaitResults(now); break;
            case RunPhase.Matching:     Match(now); break;
            case RunPhase.Storing:      AwaitStoreReply(now); break;
            case RunPhase.AwaitStore:   AwaitStoreSync(now); break;
            case RunPhase.AwaitMark:    AwaitSwapMark(now); break;      // capped swap (spec ⑫, Plugin.Run.Replace.cs)
            case RunPhase.Discarding:   AwaitSwapDiscard(now); break;
            case RunPhase.AwaitDiscard: AwaitSwapDiscardSync(now); break;
            case RunPhase.Cooldown:
                if (_stopRequested) End(EndReason.UserStop);
                else if (now >= _nextRollAt) Cycle(now);
                break;
        }
    }

    private void Cycle(float now)
    {
        if (_stopRequested) { End(EndReason.UserStop); return; }
        if (now < _lastReplyAt + RollGapS) { _nextRollAt = _lastReplyAt + RollGapS; Enter(RunPhase.Cooldown); return; }
        _st = ReadState();
        if (!_st.Valid) { _services.Log.Warning("[AutoManifest] reforge data unreadable before a roll"); End(EndReason.Refused); return; }
        RefreshMaterialsFromState();
        if (WaitingCount() == 0) { End(EndReason.AllStored); return; }
        if (_st.Stored >= _st.StoreLimit - KeepFree) { End(EndReason.PocketFull); return; }

        int times = PullTimes;
        bool bless = _useBlessing && _st.BlessAllowed;   // re-evaluated per pull: the level moves during a run
        int cost = _st.CostPerPull * times;              // re-read every pull: the level (and cost) changes with Blessing
        if (SpendLimit > 0 && _runSpent + cost > SpendLimit) { End(EndReason.SpendLimit); return; }   // 0 = no limit (spec ⑪)
        if (times == 10 ? !_st.ConsumeOk10 : !_st.ConsumeOk1) { End(EndReason.NoRemnants); return; }
        if (bless && _st.Prisms < times) { End(EndReason.NoPrism); return; }
        // Special item (spec ⑬): never a pull without it — out of items or nothing it can roll is wanted → stop.
        var sp = SpecialPreCheck(times);
        if (sp != EndReason.None) { End(sp); return; }

        _res = ReadResults();
        _sigBefore = _res.Signature;
        _rollTimes = times; _rollCost = cost; _rollBless = bless;
        _services.Log.Info($"[AutoManifest] roll → times={times} bless={bless} level={_st.Level} cost={cost} "
                           + $"spent={_runSpent}/{SpendLimit} pocket={_st.Stored}/{_st.StoreLimit} remnants={_st.Remnants} prisms={_st.Prisms}"
                           + (_runSpecialId != 0 ? $" special={_runSpecialId} have={_st.SpecialCount(_runSpecialId)}" : string.Empty));
        if (!StartRoll(bless, times, _runSpecialId)) { End(EndReason.LeftWorld); return; }
        Enter(RunPhase.Rolling);
    }

    private void AwaitRoll(float now)
    {
        if (!TryTakeRet(out var ret))
        {
            if (now - _phaseAt > RequestTimeoutS) { _services.Log.Warning("[AutoManifest] roll: no reply"); End(EndReason.Timeout); }
            return;
        }
        _lastReplyAt = now;
        _services.Log.Info($"[AutoManifest] roll ← ret={ret}{ErrSuffix(ret)} after {(now - _phaseAt) * 1000f:0} ms");
        if (ret != 0) { End(EndReason.Refused); return; }
        _runSpent += _rollCost;
        _runPulls += _rollTimes;
        if (_runSpecialId != 0) _runSpecialUsed += _rollTimes;   // 1 per pull (checkReforgeItemEnough: times ≤ owned)
        SetFooter(FooterEvent.Pull);
        Enter(RunPhase.AwaitResults);
        AwaitResults(now);   // the container sync may already have landed with the reply
    }

    // Unknown #1: results arrive via the container sync, not in the reply.
    private void AwaitResults(float now)
    {
        _res = ReadResults();
        if (_res.Signature == _sigBefore)
        {
            if (now - _phaseAt > SyncTimeoutS)
            {
                _services.Log.Warning($"[AutoManifest] results did not change within {SyncTimeoutS:0} s after the roll (sig {_sigBefore})");
                End(EndReason.Timeout);
            }
            return;
        }
        _services.Log.Info($"[AutoManifest] results after {(now - _phaseAt) * 1000f:0} ms: {DescribeResults(_res)}");
        Enter(RunPhase.Matching);
        Match(now);
    }

    private void Match(float now)
    {
        if (_stopRequested) { End(EndReason.UserStop); return; }
        _st = ReadState();
        RefreshMaterialsFromState();
        // Waiting rows are always planned first; an upgrade ("Keep better rolls", spec ⑦) comes back only when no waiting
        // row has a match in these results.
        var plan = _catalog == null ? null : Matcher.FirstPlan(_catalog, _targets, _res, _st.RowUnlocked, KeepCap);
        int waiting = WaitingCount();
        // Every row stored → Done, even with upgrades left in these results: no pulling or storing just for upgrades.
        if (plan is not { } p || (p.Upgrade && waiting == 0))
        {
            if (waiting == 0) { End(EndReason.AllStored); return; }
            _nextRollAt = _lastReplyAt + RollGapS;
            Enter(RunPhase.Cooldown);
            return;
        }
        if (_st.Stored >= _st.StoreLimit - KeepFree) { End(EndReason.PocketFull); return; }   // re-checked before EACH store
        if (p.Upgrade && !UpgradeFits(p, waiting))
        {
            _nextRollAt = _lastReplyAt + RollGapS;   // the rest of these results only holds upgrades: roll again
            Enter(RunPhase.Cooldown);
            return;
        }
        _plan = p;
        _planWasEntry = p.Upgrade ? _targets[p.TargetIndex].Best?.Entry ?? 0 : 0;
        _swapVictim = p.Replace ? _targets[p.TargetIndex].Lowest : null;   // the copy it swaps out (spec ⑫), picked NOW
        _sigBefore = _res.Signature;
        _storedBefore = _st.Stored;
        _storeBefore = _st;   // its pocket = the "before" side of the uuid diff (Plugin.StoredMarks.cs)
        _services.Log.Info($"[AutoManifest] store → pull={p.Pull + 1} row={p.Row + 1} (idx {p.Pull},{p.Row}) entry={p.EntryId} "
                           + $"{EntryText(p.EntryId)} for target #{p.TargetIndex + 1} pocket={_st.Stored}/{_st.StoreLimit}"
                           + (p.Upgrade ? $" · better roll (was entry={_planWasEntry} {EntryText(_planWasEntry)} "
                                          + $"uuid={_targets[p.TargetIndex].Best?.Uuid ?? 0}, copies={_targets[p.TargetIndex].Copies.Count}"
                                          + $" active={_targets[p.TargetIndex].ActiveCount} cap={KeepCopies})"
                                        : string.Empty)
                           + (_swapVictim != null ? $" · at cap: swaps out uuid={_swapVictim.Uuid} entry={_swapVictim.Entry} "
                                                    + $"{EntryText(_swapVictim.Entry)}" : string.Empty));
        if (!StartStore(p.Pull, p.Row)) { End(EndReason.LeftWorld); return; }
        Enter(RunPhase.Storing);
    }

    private void AwaitStoreReply(float now)
    {
        if (!TryTakeRet(out var ret))
        {
            if (now - _phaseAt > RequestTimeoutS) { _services.Log.Warning("[AutoManifest] store: no reply"); End(EndReason.Timeout); }
            return;
        }
        _lastReplyAt = now;
        _services.Log.Info($"[AutoManifest] store ← ret={ret}{ErrSuffix(ret)} after {(now - _phaseAt) * 1000f:0} ms");
        if (ret == StorageFullCode) { End(EndReason.PocketFull); return; }
        if (ret != 0) { End(EndReason.StoreRefused); return; }
        MarkStored(_plan.TargetIndex, _plan.EntryId, _storeBefore);
        if (_swapVictim != null) NoteSwapCopy();   // the copy whose uuid must be confirmed before the swap's discard
        if (_plan.Upgrade) _runBetter++;
        Enter(RunPhase.AwaitStore);
        AwaitStoreSync(now);
    }

    // Unknown #2: never reuse indices from before the store — wait for the server's view, then re-read and re-match.
    private void AwaitStoreSync(float now)
    {
        _res = ReadResults();
        var st = ReadState();
        bool moved = _res.Signature != _sigBefore || (st.Valid && st.Stored > _storedBefore);
        if (!moved)
        {
            if (now - _phaseAt > SyncTimeoutS)
            {
                _services.Log.Warning($"[AutoManifest] results/pocket did not change within {SyncTimeoutS:0} s after a store");
                End(EndReason.Timeout);
            }
            return;
        }
        _st = st;
        RefreshMaterialsFromState();
        _services.Log.Info($"[AutoManifest] stored entry={_plan.EntryId}: pocket {_storedBefore} → {st.Stored}/{st.StoreLimit} "
                           + $"(results sig changed={_res.Signature != _sigBefore}); results now: {DescribeResults(_res)}");
        // A swap at the cap (spec ⑫): confirm the new copy's uuid, THEN discard the old one (Plugin.Run.Replace.cs).
        if (_swapVictim != null) { Enter(RunPhase.AwaitMark); AwaitSwapMark(now); return; }
        if (_plan.Upgrade) SetFooter(FooterEvent.Better, _plan.EntryId, _planWasEntry);
        else SetFooter(FooterEvent.Stored, _plan.EntryId);
        Enter(RunPhase.Matching);
        Match(now);
    }

    // ── end ───────────────────────────────────────────────────────────────────────────────────────────────────────
    private EndReason _endReason;
    private int _endPulls, _endStored, _endLimit, _endWaiting, _endBetter, _endDiscarded;
    private long _endSpent;

    private void End(EndReason reason)
    {
        if (!Running) return;
        _phase = RunPhase.Idle;
        _stopRequested = false;
        _endReason = reason;
        _endPulls = _runPulls; _endSpent = _runSpent;
        _endStored = _st.Stored; _endLimit = _st.StoreLimit; _endWaiting = WaitingCount();
        _endBetter = _runBetter; _endDiscarded = _runDiscarded;
        _swapVictim = null;
        CaptureEndReach();
        CaptureEndSpecial();
        _endComposed.Invalidate();
        _services.Log.Info($"[AutoManifest] run end: {reason} pulls={_runPulls} spent={_runSpent} pocket={_st.Stored}/{_st.StoreLimit} "
                           + $"waiting={_endWaiting} better={_runBetter} discarded={_runDiscarded} targets={DescribeTargets()}"
                           + (_runSpecialId != 0 ? $" special={_runSpecialId} used={_runSpecialUsed} left={_st.SpecialCount(_runSpecialId)}" : string.Empty));
        MarkMain();
    }
}
