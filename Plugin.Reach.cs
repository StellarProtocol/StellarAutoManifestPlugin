using System;
using System.Text;
using Stellar.Abstractions.Domain;

namespace Stellar.AutoManifest;

/// <summary>
/// Reachability (approved spec ⑥, option B): which qualities can roll at the CURRENT Matrix level, and for each target
/// the first level where its minimum can roll. Source = the level table row of (season, level): <c>PrimeWeight</c> when
/// the next pull uses Blessing, <c>NormalWeight</c> otherwise (KB Matrix-Manifestation §5). A target's minimum in quality
/// Q is reachable when any quality ≥ Q has weight &gt; 0 (a higher quality's every value beats the minimum).
/// <para>Recomputed ONLY when one of its inputs moves — season, level, effective Blessing, the level-gate mask, the
/// target list, the catalogue — checked after every state read (≤ 1 Hz idle, per run step) with one tuple compare, no
/// allocation (Plugin-Performance-Rules §1/§2). The UI Funcs read the cached fields below.</para>
/// Used by: target badges "needs Lv N" / "locked", the Start gate + amber footer, the confirm card's amber line, the
/// footer's level-up line during a run, the end line's " · 1 target needs Lv 11", and the picker (Plugin.Picker.Reach.cs).
/// </summary>
public sealed partial class Plugin
{
    private MatrixLevels? _levels;
    private int _targetsVer;   // bumped on every target add / remove / minimum change / rebind
    private int _reachVer;     // bumped whenever the result changes — text caches key on it
    private (int season, int level, bool bless, string mask, int targets, bool known) _reachKey = (-1, -1, false, string.Empty, -1, false);
    private readonly bool[] _rollNow = { true, true, true, true };   // quality 2..5 can roll at the current level
    private int _reachableTargets, _unreachTargets;                  // known targets that can / can't roll now

    // Blessing as the next pull really uses it: the setting AND the game's gate (no Blessing at max level / when the next
    // level is locked) — the run applies the same rule per pull (Plugin.Run.cs Cycle).
    private bool ReachBless => _useBlessing && _st.BlessAllowed;

    private bool RollNow(int quality) => _rollNow[Math.Clamp(quality - 2, 0, 3)];

    // Start is gated only when the answer is KNOWN and every known target can't roll now.
    private bool ReachBlocksStart() => _st.Valid && _reachableTargets == 0 && _unreachTargets > 0;

    private void UpdateReach()
    {
        if (!_st.Valid) return;
        if (_levels == null || _levels.SeasonId != _st.SeasonId)
        {
            _levels = ReadLevels();   // static data: once per season (null → retried on the next state read)
            if (_levels != null)
                _services.Log.Info($"[AutoManifest] level table: season {_levels.SeasonId}, {_levels.Rows.Length} levels, "
                                   + $"{_levels.ExpPerBlessPull} EXP per Blessing pull");
        }
        bool known = _levels != null && _catalogReady && _catalog != null;
        var key = (_st.SeasonId, _st.Level, ReachBless, _st.LevelMask, _targetsVer, known);
        if (key == _reachKey) return;
        var prev = _reachKey;
        _reachKey = key;
        _reachVer++;

        int lv = _st.Level;
        bool prime = key.Item3;
        for (int q = 0; q < 4; q++) _rollNow[q] = !known || _levels!.CanRoll(lv, q + 2, prime);

        bool levelUp = Running && known && prev.known && prev.season == _st.SeasonId && prev.level > 0 && lv > prev.level;
        StringBuilder? now = null;
        int reachable = 0, unreach = 0;
        foreach (var t in _targets)
        {
            int before = t.Need;
            t.Need = known ? NeedFor(t, lv, prime) : 0;
            if (!known || !_catalog!.ByGroup.ContainsKey(t.Group)) continue;
            if (t.Need == 0) reachable++; else unreach++;
            if (!levelUp || before == 0 || t.Need != 0 || t.Stored) continue;
            now ??= new StringBuilder();
            if (now.Length > 0) now.Append(", ");
            now.Append(NameOfGroup(t.Group));
        }
        _reachableTargets = reachable;
        _unreachTargets = unreach;
        if (levelUp) OnMatrixLevelUp(prev.level, lv, now?.ToString() ?? string.Empty);
        MarkMain();
        _pickerWindow?.MarkDirty();
    }

    // 0 = the minimum can roll now; N = the first level where it can (under the effective Blessing setting); -1 = locked
    // (no level in the table reaches it, or a level on the way there fails its UnlockCondition).
    private int NeedFor(Target t, int lv, bool prime)
    {
        if (_catalog == null || !_catalog.ByGroup.TryGetValue(t.Group, out var imp) || imp.Steps.Length == 0) return 0;
        return NeedLevel(lv, imp.Steps[Math.Clamp(t.MinStep, 0, imp.Steps.Length - 1)].Quality, prime);
    }

    private int NeedLevel(int lv, int quality, bool prime)
    {
        if (_levels == null || _levels.CanRoll(lv, quality, prime)) return 0;
        int n = _levels.FirstLevel(lv + 1, quality, prime);
        return n > 0 && PathOpen(lv, n) ? n : -1;
    }

    // Every level after `from` up to `to` passes its gate now (Lv 11-16 share [[9,10430]] in S4; checking each is the
    // safe superset of "the needed level's UnlockCondition").
    private bool PathOpen(int from, int to)
    {
        for (int l = from + 1; l <= to; l++) if (!_st.LevelOpen(l)) return false;
        return true;
    }

    private string NameOfGroup(int group) => _catalog != null && _catalog.ByGroup.TryGetValue(group, out var imp) ? imp.Name : "#" + group;

    // ── target badge: "needs Lv 4" / "locked" (amber) ─────────────────────────────────────────────────────────────
    private readonly int[] _needLabelVal = new int[MaxTargets];
    private readonly int[] _needLabelGen = new int[MaxTargets];
    private readonly string[] _needLabel = new string[MaxTargets];

    private int TargetNeed(int i) => TargetAt(i)?.Need ?? 0;

    private string NeedLabel(int i)
    {
        int need = TargetNeed(i);
        if (need == 0) return string.Empty;
        if (need == _needLabelVal[i] && _needLabelGen[i] == _langGen && _needLabel[i] != null) return _needLabel[i];
        _needLabelVal[i] = need; _needLabelGen[i] = _langGen;
        return _needLabel[i] = need > 0 ? _loc.TFormat(KeyNeedsLv, need) : _loc.T(KeyLocked);
    }

    // ── Start gate footer: "None of your targets can roll at Lv 2. Turn on Blessing, or lower a value." ───────────
    private int _gateVer = -1, _gateGen = -1;
    private string _gateText = string.Empty;
    private readonly ContrastCache _gateInk = new();

    private ColorRgba GateColor() => _gateInk.Get(CautionColor(), WindowBg(), PillMinContrast);

    private string GateText()
    {
        if (_gateVer == _reachVer && _gateGen == _langGen) return _gateText;
        _gateVer = _reachVer; _gateGen = _langGen;
        // "Turn on Blessing" only when that would change the answer's inputs: off now, and the game allows it.
        return _gateText = !_useBlessing && _st.BlessAllowed
            ? _loc.TFormat(KeyReachNone, _st.Level)
            : _loc.TFormat(KeyReachNoneBless, _st.Level);
    }

    // ── confirm card: "2 targets can't roll at Lv 2 yet: Soaring Flame (Lv 4), Fire Attack (Lv 11). …" ──────────
    // Built once per Start click. The card's emphasis line is bold TMP text, so a <color> span makes it amber
    // (ConfirmRequest has no colour option in SDK 2.33).
    private string? ConfirmReachLine()
    {
        if (_unreachTargets == 0 || _catalog == null) return null;
        var list = new StringBuilder();
        foreach (var t in _targets)
        {
            if (t.Need == 0 || !_catalog.ByGroup.ContainsKey(t.Group)) continue;
            if (list.Length > 0) list.Append(", ");
            list.Append(t.Need > 0 ? _loc.TFormat(KeyReachItemLv, NameOfGroup(t.Group), t.Need)
                                   : _loc.TFormat(KeyReachItemLocked, NameOfGroup(t.Group)));
        }
        var line = _unreachTargets == 1
            ? _loc.TFormat(KeyReachConfirmOne, _st.Level, list.ToString())
            : _loc.TFormat(KeyReachConfirmMany, _unreachTargets, _st.Level, list.ToString());
        var ink = EnsureContrast(CautionColor(), TileColor(), PillMinContrast);
        return "<color=#" + HexOf(ink) + ">" + line + "</color>";
    }

    // ── live: level-up during a run ───────────────────────────────────────────────────────────────────────────────
    private void OnMatrixLevelUp(int from, int to, string nowReachable)
    {
        _services.Log.Info($"[AutoManifest] matrix level {from} -> {to}"
                           + (nowReachable.Length > 0 ? " · can roll now: " + nowReachable : string.Empty)
                           + $" · still unreachable: {_unreachTargets}");
        SetFooterLevelUp(from, to, nowReachable);
    }

    // ── end line suffix: " · 1 target needs Lv 11" ────────────────────────────────────────────────────────────────
    private int _endNeedCount, _endNeedMin, _endLockedCount;

    private void CaptureEndReach()
    {
        _endNeedCount = 0; _endNeedMin = int.MaxValue; _endLockedCount = 0;
        if (_catalog == null) return;
        foreach (var t in _targets)
        {
            if (t.Stored || t.Need == 0 || !_catalog.ByGroup.ContainsKey(t.Group)) continue;
            if (t.Need > 0) { _endNeedCount++; _endNeedMin = Math.Min(_endNeedMin, t.Need); }
            else _endLockedCount++;
        }
    }

    private string EndReachSuffix()
    {
        if (_endReason == EndReason.AllStored) return string.Empty;
        if (_endNeedCount > 0)
            return _endNeedCount == 1 ? _loc.TFormat(KeyEndNeedOne, _endNeedMin) : _loc.TFormat(KeyEndNeedMany, _endNeedCount, _endNeedMin);
        if (_endLockedCount > 0)
            return _endLockedCount == 1 ? _loc.T(KeyEndLockedOne) : _loc.TFormat(KeyEndLockedMany, _endLockedCount);
        return string.Empty;
    }
}
