using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// Persisted choices (shared plugin config, section "automanifest"): the target rows (imprint Group + minimum step)
/// and the RUN settings. Cached in fields after the first read — the window polls them every refresh — and saved on
/// change only (never inside a drag / per frame).
/// </summary>
public sealed partial class Plugin
{
    private const int MaxTargets = 12;   // row pool size; "+ Add target" recesses when full

    // "Spend at most (Remnants)": a typed number (spec ⑪, Plugin.SpendInput.cs), approved default 1,200, 0 = no limit.
    // "Keep pocket slots free" 0..5 (default 0).
    private const int DefaultSpendLimit = 1200;
    private static readonly int[] KeepFreeChoices = { 0, 1, 2, 3, 4, 5 };
    private static readonly string[] KeepFreeOptions = Array.ConvertAll(KeepFreeChoices, v => v.ToString(CultureInfo.InvariantCulture));

    private IConfigSection _cfg = null!;
    private readonly List<Target> _targets = new();
    private bool _pullTen = true;     // approved default: 10-pull
    private bool _useBlessing = true; // approved default: On
    private int _keepCopiesIndex = KeepCopiesDefault;   // "Keep better rolls" copy cap (spec ⑫), index into KeepCopiesChoices
    private int _spendLimit = DefaultSpendLimit;
    private int _keepFreeIndex;

    // "Keep better rolls" (spec ⑫, replaces ⑦'s On/Off): copies kept per target INCLUDING the first — Off · Best only
    // (swap) · Up to 2 · Up to 3 · No cap (never discard). Saved as that number under a NEW key "keepCopies" (0 = Off,
    // -1 = No cap) so the old bool "keepBetter" can migrate: true → No cap (⑦ never discarded), false → Off, unset →
    // the default Best only.
    private static readonly int[] KeepCopiesChoices = { 0, 1, 2, 3, -1 };
    private const int KeepCopiesDefault = 1;   // Best only
    private int KeepCopies => KeepCopiesChoices[Math.Clamp(_keepCopiesIndex, 0, KeepCopiesChoices.Length - 1)];
    // The Matcher's cap: 0 = Off, Matcher.NoCap = never discard.
    private int KeepCap => KeepCopies < 0 ? Matcher.NoCap : KeepCopies;

    private int SpendLimit => _spendLimit;   // 0 = no limit (spec ⑪)
    private int KeepFree => KeepFreeChoices[Math.Clamp(_keepFreeIndex, 0, KeepFreeChoices.Length - 1)];
    private int PullTimes => _pullTen ? 10 : 1;

    private void LoadConfig()
    {
        _cfg = _services.Config.GetSection("automanifest");
        _pullTen       = _cfg.Get("pullTen", true);
        _useBlessing   = _cfg.Get("useBlessing", true);
        _keepCopiesIndex = LoadKeepCopiesIndex();
        // Same key the old dropdown wrote (an int), so a saved choice carries over as the typed value.
        _spendLimit    = Math.Max(0, _cfg.Get("spendLimit", DefaultSpendLimit));
        _keepFreeIndex = Math.Clamp(_cfg.Get("keepFree", 0), 0, KeepFreeChoices.Length - 1);

        // "group:step[:mark],…" — a stale group (another season's table) is kept and simply never matches; it shows
        // as "#<group>" until removed.
        _targets.Clear();
        foreach (var part in (_cfg.Get("targets", string.Empty) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length >= 2 && int.TryParse(kv[0], out var g) && int.TryParse(kv[1], out var s) && _targets.Count < MaxTargets)
            {
                var t = new Target { Group = g, MinStep = Math.Max(0, s) };
                ReadMark(kv, t);   // optional stored mark (Plugin.StoredMarks.cs), checked against the pocket later
                _targets.Add(t);
            }
        }
    }

    private void SaveTargets()
    {
        var sb = new StringBuilder();
        foreach (var t in _targets)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(t.Group).Append(':').Append(t.MinStep);
            AppendMark(sb, t);
        }
        _cfg.Set("targets", sb.ToString());
        _cfg.Save();
    }

    private void SetPullTen(bool v)       { if (Running || _pullTen == v) return; _pullTen = v; _cfg.Set("pullTen", v); _cfg.Save(); MarkMain(); }
    private void SetUseBlessing(bool v)   { if (Running) return; _useBlessing = v; _cfg.Set("useBlessing", v); _cfg.Save(); UpdateReach(); MarkMain(); }
    private void SetKeepCopiesIndex(int i)
    {
        if (Running || i < 0 || i >= KeepCopiesChoices.Length || i == _keepCopiesIndex) return;
        _keepCopiesIndex = i; _cfg.Set("keepCopies", KeepCopiesChoices[i]); _cfg.Save(); MarkMain();
    }

    // "keepCopies" wins. Without it, the ⑦ bool: Get can't say "absent", so read it with both defaults — they only
    // differ when nothing is saved.
    private int LoadKeepCopiesIndex()
    {
        const int unset = int.MinValue;
        int saved = _cfg.Get("keepCopies", unset);
        if (saved == unset)
        {
            bool a = _cfg.Get("keepBetter", true), b = _cfg.Get("keepBetter", false);
            if (a != b) return KeepCopiesDefault;   // never saved → Best only
            saved = a ? -1 : 0;                    // On → No cap (⑦ never discarded), Off → Off
        }
        int i = Array.IndexOf(KeepCopiesChoices, saved);
        return i >= 0 ? i : KeepCopiesDefault;
    }

    private void SetSpendLimit(int v)
    {
        v = Math.Max(0, v);
        if (Running || v == _spendLimit) return;
        _spendLimit = v; _cfg.Set("spendLimit", v); _cfg.Save(); MarkMain();
    }

    private void SetKeepFreeIndex(int i)
    {
        if (Running || i < 0 || i >= KeepFreeChoices.Length) return;
        _keepFreeIndex = i; _cfg.Set("keepFree", KeepFreeChoices[i]); _cfg.Save(); MarkMain();
    }

    // ── target rows ───────────────────────────────────────────────────────────────────────────────────────────────
    private void AddTarget(int group, int minStep)
    {
        if (Running || _targets.Count >= MaxTargets) return;
        _targets.Add(new Target { Group = group, MinStep = minStep });
        SaveTargets();
        TargetsChanged();
        MarkMain();
    }

    private void RemoveTarget(int index)
    {
        if (Running || index < 0 || index >= _targets.Count) return;
        _targets.RemoveAt(index);   // its stored mark goes with it
        if (_pendingMark == index) { _pendingMark = -1; _pocketBefore = null; }
        else if (_pendingMark > index) _pendingMark--;
        _rowHover.Reset();   // the next row slides under the pointer — it must not inherit the hover
        SaveTargets();
        TargetsChanged();
        MarkMain();
    }

    private void SetTargetMin(int index, int step)
    {
        if (Running || index < 0 || index >= _targets.Count) return;
        var t = _targets[index];
        if (t.MinStep == step) return;
        t.MinStep = step;
        if (t.HasMark)   // a new minimum = a new goal: the stored imprint no longer answers it
        {
            t.ClearMark();
            if (_pendingMark == index) { _pendingMark = -1; _pocketBefore = null; }
            _marksVer++;
        }
        SaveTargets();
        TargetsChanged();
        MarkMain();
    }

    // Any change to the target list re-runs the reachability pass (Plugin.Reach.cs) — on change, never per frame.
    private void TargetsChanged()
    {
        _targetsVer++;
        UpdateReach();
    }

    // Clamp saved steps to the live table once it is loaded (a season change can shorten a ladder).
    private void RebindTargets()
    {
        if (_catalog == null) return;
        foreach (var t in _targets)
            if (_catalog.ByGroup.TryGetValue(t.Group, out var imp) && imp.Steps.Length > 0)
                t.MinStep = Math.Clamp(t.MinStep, 0, imp.Steps.Length - 1);
        foreach (var t in _targets) t.RecomputeBest(_catalog);   // copies' value steps come from the live table
        TargetsChanged();
    }
}
