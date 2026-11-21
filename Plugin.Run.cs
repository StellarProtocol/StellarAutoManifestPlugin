using System;
using System.Text;
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

    private enum RunPhase : byte { Idle, Cycle, Rolling, AwaitResults, Matching, Storing, AwaitStore, Cooldown }
    private enum EndReason : byte
    {
        None, AllStored, PocketFull, SpendLimit, NoRemnants, NoPrism, Refused, StoreRefused, Timeout, UserStop, LeftWorld,
    }

    private RunPhase _phase;
    private bool Running => _phase != RunPhase.Idle;
    private bool _stopRequested;
    private float _phaseAt, _nextRollAt, _lastReplyAt;
    private int _runPulls, _rollTimes, _rollCost, _storedBefore;
    private long _runSpent;
    private bool _rollBless;
    private string _sigBefore = string.Empty;
    private ReforgeState _st = new();
    private ReforgeResults _res = new();
    private StorePlan _plan;

    // ── start / stop ──────────────────────────────────────────────────────────────────────────────────────────────
    // Startable with at least one target the live table knows (a finished run's "stored" rows wait again on the next start),
    // and not when NONE of them can roll at the current level (spec ⑥ — the footer then says why, Plugin.Reach.cs).
    private bool CanStart() => !Running && _catalogReady && _st.Valid && InWorld() && KnownTargetCount() > 0 && !ReachBlocksStart();

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
        if (!CanStart()) return;
        ClosePicker();
        _confirm.Show(new ConfirmRequest(_loc.T(KeyConfirmTitle), ConfirmBody(), BeginRun)
        {
            ConfirmLabel = _loc.T(KeyStart), CancelLabel = _loc.T(KeyCancel), Severity = ConfirmSeverity.Primary,
            Emphasis = ConfirmReachLine(),   // amber "2 targets can't roll at Lv 2 yet: …" (null = all can roll now)
        });
    }

    // "Spends up to 240 Lightchase Factor Remnant and 20 Dawn Prism. …" — counted in WHOLE batches of the chosen pull
    // size, like the run (Cycle stops before a batch that would pass the limit): batches = limit / (cost × size), e.g.
    // 300 / (12 × 10) = 2 → 240 Remnants, 20 Prism (one per pull). "Up to": the cost per pull can rise if the level
    // goes up mid-run, which only lowers these. The Prism part only when Blessing is on and usable.
    private string ConfirmBody()
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
        foreach (var t in _targets) t.StoredText = null;   // a new run: every row waits again
        _runPulls = 0; _runSpent = 0; _stopRequested = false;
        _lastReplyAt = float.NegativeInfinity;
        _endReason = EndReason.None;
        SetFooter(FooterEvent.Starting);
        _services.Log.Info($"[AutoManifest] run start: targets={DescribeTargets()} pullSize={PullTimes} blessing={_useBlessing}"
                           + $"(allowed={_st.BlessAllowed}) spendLimit={SpendLimit} keepFree={KeepFree} level={_st.Level} "
                           + $"pocket={_st.Stored}/{_st.StoreLimit} remnants={_st.Remnants} prisms={_st.Prisms} exp={_st.Exp} "
                           + $"rollNow={_rollNowTargets} notYet={_unreachTargets} reachable={_reachableTargets}");
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
        if (_runSpent + cost > SpendLimit) { End(EndReason.SpendLimit); return; }
        if (times == 10 ? !_st.ConsumeOk10 : !_st.ConsumeOk1) { End(EndReason.NoRemnants); return; }
        if (bless && _st.Prisms < times) { End(EndReason.NoPrism); return; }

        _res = ReadResults();
        _sigBefore = _res.Signature;
        _rollTimes = times; _rollCost = cost; _rollBless = bless;
        _services.Log.Info($"[AutoManifest] roll → times={times} bless={bless} level={_st.Level} cost={cost} "
                           + $"spent={_runSpent}/{SpendLimit} pocket={_st.Stored}/{_st.StoreLimit} remnants={_st.Remnants} prisms={_st.Prisms}");
        if (!StartRoll(bless, times)) { End(EndReason.LeftWorld); return; }
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
        var plan = _catalog == null ? null : Matcher.FirstPlan(_catalog, _targets, _res, _st.RowUnlocked);
        if (plan is not { } p)
        {
            if (WaitingCount() == 0) { End(EndReason.AllStored); return; }
            _nextRollAt = _lastReplyAt + RollGapS;
            Enter(RunPhase.Cooldown);
            return;
        }
        if (_st.Stored >= _st.StoreLimit - KeepFree) { End(EndReason.PocketFull); return; }   // re-checked before EACH store
        _plan = p;
        _sigBefore = _res.Signature;
        _storedBefore = _st.Stored;
        _services.Log.Info($"[AutoManifest] store → pull={p.Pull + 1} row={p.Row + 1} (idx {p.Pull},{p.Row}) entry={p.EntryId} "
                           + $"{EntryText(p.EntryId)} for target #{p.TargetIndex + 1} pocket={_st.Stored}/{_st.StoreLimit}");
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
        if (_plan.TargetIndex < _targets.Count) _targets[_plan.TargetIndex].StoredText = ValueOf(_plan.EntryId);
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
        SetFooter(FooterEvent.Stored, _plan.EntryId);
        _services.Log.Info($"[AutoManifest] stored entry={_plan.EntryId}: pocket {_storedBefore} → {st.Stored}/{st.StoreLimit} "
                           + $"(results sig changed={_res.Signature != _sigBefore}); results now: {DescribeResults(_res)}");
        Enter(RunPhase.Matching);
        Match(now);
    }

    // ── end ───────────────────────────────────────────────────────────────────────────────────────────────────────
    private EndReason _endReason;
    private int _endPulls, _endStored, _endLimit, _endWaiting;
    private long _endSpent;

    private void End(EndReason reason)
    {
        if (!Running) return;
        _phase = RunPhase.Idle;
        _stopRequested = false;
        _endReason = reason;
        _endPulls = _runPulls; _endSpent = _runSpent;
        _endStored = _st.Stored; _endLimit = _st.StoreLimit; _endWaiting = WaitingCount();
        CaptureEndReach();
        _endComposed.Invalidate();
        _services.Log.Info($"[AutoManifest] run end: {reason} pulls={_runPulls} spent={_runSpent} pocket={_st.Stored}/{_st.StoreLimit} "
                           + $"waiting={_endWaiting} targets={DescribeTargets()}");
        MarkMain();
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────────────────────
    private string ErrSuffix(int ret)
    {
        if (ret >= 0) return string.Empty;
        var err = LuaError();
        return err.Length > 0 ? " (lua: " + err + ")" : string.Empty;
    }

    private string ValueOf(int entryId)
        => _catalog != null && _catalog.ByEntry.TryGetValue(entryId, out var hit) ? hit.Imp.Steps[hit.Step].ValueText : "?";

    private string EntryText(int entryId)
        => _catalog != null && _catalog.ByEntry.TryGetValue(entryId, out var hit)
            ? $"'{hit.Imp.Name} {hit.Imp.Steps[hit.Step].ValueText}'" : "'?'";

    private string DescribeTargets()
    {
        var sb = new StringBuilder();
        foreach (var t in _targets)
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(t.Group).Append(":step").Append(t.MinStep + 1).Append('+').Append(t.Stored ? "(stored " + t.StoredText + ")" : "");
        }
        return sb.Length > 0 ? sb.ToString() : "-";
    }

    private static string DescribeResults(ReforgeResults r)
    {
        var sb = new StringBuilder();
        sb.Append(r.Pulls.Length).Append(" pulls");
        for (int p = 0; p < r.Pulls.Length; p++) sb.Append(" | ").Append(p + 1).Append(": ").Append(string.Join(",", r.Pulls[p]));
        int hash = r.Signature.IndexOf('#');
        if (hash > 0) sb.Append(" | time=").Append(r.Signature, 0, hash);
        return sb.ToString();
    }
}
