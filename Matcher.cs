using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.AutoManifest;

/// <summary>One pocket entry the plugin stored for a target row: character + season + the entry's uuid + entryId +
/// value text. Persisted (Plugin.StoredMarks.cs); <see cref="Active"/> = confirmed in the CURRENT character's pocket.</summary>
internal sealed class MarkCopy
{
    public string Text = string.Empty;       // the stored value text ("2.72%")
    public string Char = string.Empty;       // CharSerialize.charId of the pocket it went into
    public int Season, Uuid, Entry;          // Uuid 0 = stored this session, uuid not resolved yet
    public int Step = -1;                    // value step in the live catalogue (-1 = entry unknown to it)
    public bool Active;
}

/// <summary>One target row: "imprint <see cref="Group"/> at value step <see cref="MinStep"/> or higher". Two rows with
/// the same imprint = two stores. The stored MARK (persisted with the row, Plugin.StoredMarks.cs) is the list of EVERY
/// pocket entry the plugin stored for it (more than one with "Keep better rolls", spec ⑦; with a copy cap the run
/// discards the lowest one past the cap, spec ⑫). The row counts as stored (<see cref="Stored"/>) while at least one copy is active; the badge shows the
/// <see cref="Best"/> active copy, so removing the best one falls back to the next.</summary>
internal sealed class Target
{
    public int Group;
    public int MinStep;             // 0-based index into Imprint.Steps
    public int Need;                // reachability (Plugin.Reach.cs): 0 = can roll now, N = first Matrix level, -1 = locked

    public readonly List<MarkCopy> Copies = new(1);
    public MarkCopy? Best;          // highest-value active copy (the later one on a tie); null = none in this pocket
    // Lowest-value active copy whose value is known (the OLDER one on a tie) — the one a capped "Keep better rolls"
    // swaps out (spec ⑫). Equals Best with one copy. ActiveCount = active copies = what counts toward the cap.
    public MarkCopy? Lowest;
    public int ActiveCount;

    public bool HasMark => Copies.Count > 0;
    public bool Stored => Best != null;
    public string? StoredText => Best?.Text;
    public int BestStep => Best?.Step ?? -1;
    public int LowestStep => Lowest?.Step ?? -1;

    public void ClearMark()
    {
        Copies.Clear();
        Best = null;
        Lowest = null;
        ActiveCount = 0;
    }

    // Re-derive every copy's value step from the live catalogue and pick the best / lowest active copy. Called on change
    // only (store, pocket validation, catalogue load, a run's discard). True when the best copy moved.
    public bool RecomputeBest(ImprintCatalog? cat)
    {
        MarkCopy? best = null, lowest = null;
        int active = 0;
        foreach (var c in Copies)
        {
            c.Step = cat != null && cat.ByEntry.TryGetValue(c.Entry, out var hit) && hit.Imp.Group == Group ? hit.Step : -1;
            if (!c.Active) continue;
            active++;
            if (best == null || c.Step >= best.Step) best = c;
            if (c.Step >= 0 && (lowest == null || c.Step < lowest.Step)) lowest = c;   // strict: the older copy on a tie
        }
        bool moved = !ReferenceEquals(best, Best);
        Best = best;
        Lowest = lowest;
        ActiveCount = active;
        return moved;
    }
}

/// <summary>The live Manifestation state, parsed from Lua <c>M.state()</c> (one '|'-separated line).</summary>
internal sealed class ReforgeState
{
    public bool Valid;
    public int SeasonId, Level, StoreLimit, Stored;
    public long Remnants, Prisms;
    public int CostPerPull;         // Remnants per single pull at the CURRENT level (DefaultConsume of item 1220006)
    public bool ConsumeOk1, ConsumeOk10;   // every DefaultConsume item covers 1 / 10 pulls
    public bool BlessAllowed;       // mirrors the game's canUseBlessingMaterial
    public int Profession;          // looting class (GetProfessionDropId)
    public bool[] RowUnlocked = Array.Empty<bool>();
    public int Exp;                 // Matrix EXP inside the current level (out of that level's UpgradeExp)
    public string LevelMask = string.Empty;   // per level from Lv 1: '1' = at or below the level, or its UnlockCondition passes
    public string CharId = string.Empty;      // CharSerialize.charId ("" / "0" = unknown)
    public bool HasPocket;                    // the pocket list below was read (script v4+)
    public string PocketSrc = string.Empty;   // raw "uuid:entryId,…" — reused arrays when unchanged
    public int[] PocketUuid = Array.Empty<int>(), PocketEntry = Array.Empty<int>();   // stored entries (entryId ≠ 0)

    public bool PocketHolds(int uuid, int entryId)
    {
        for (int i = 0; i < PocketUuid.Length; i++) if (PocketUuid[i] == uuid && PocketEntry[i] == entryId) return true;
        return false;
    }

    public bool PocketHasUuid(int uuid)
    {
        for (int i = 0; i < PocketUuid.Length; i++) if (PocketUuid[i] == uuid) return true;
        return false;
    }

    // Level `level` can be reached now as far as its gate goes (false past the table).
    public bool LevelOpen(int level) => level >= 1 && level <= LevelMask.Length && LevelMask[level - 1] == '1';

    // `prev` = the last parsed state: its pocket arrays are reused when the pocket text is unchanged (the common case at
    // the 1 Hz idle read — Plugin-Performance-Rules §2).
    public static ReforgeState Parse(string? s, ReforgeState? prev = null)
    {
        var st = new ReforgeState();
        if (string.IsNullOrEmpty(s)) return st;
        var f = s.Split('|');
        if (f.Length < 16 || f[0] != "1") return st;
        st.SeasonId = I(f[1]); st.Level = I(f[2]); st.StoreLimit = I(f[3]); st.Stored = I(f[4]);
        st.Remnants = L(f[5]); st.Prisms = L(f[6]); st.CostPerPull = I(f[7]);
        st.ConsumeOk1 = f[8] == "1"; st.ConsumeOk10 = f[9] == "1"; st.BlessAllowed = f[10] == "1";
        st.Profession = I(f[11]);
        var mask = f[12];
        st.RowUnlocked = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++) st.RowUnlocked[i] = mask[i] == '1';
        st.Exp = I(f[13]);
        st.LevelMask = f[14];
        if (f.Length >= 18)
        {
            st.CharId = f[15];
            st.HasPocket = true;
            if (prev != null && prev.HasPocket && prev.PocketSrc == f[16])
            {
                st.PocketSrc = prev.PocketSrc; st.PocketUuid = prev.PocketUuid; st.PocketEntry = prev.PocketEntry;
            }
            else ParsePocket(st, f[16]);
        }
        st.Valid = true;
        return st;
    }

    private static void ParsePocket(ReforgeState st, string src)
    {
        st.PocketSrc = src;
        var parts = src.Split(',', StringSplitOptions.RemoveEmptyEntries);
        st.PocketUuid = new int[parts.Length];
        st.PocketEntry = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            int c = parts[i].IndexOf(':');
            if (c <= 0) continue;
            st.PocketUuid[i] = I(parts[i].Substring(0, c));
            st.PocketEntry[i] = I(parts[i].Substring(c + 1));
        }
    }

    private static int I(string s) => (int)L(s);

    private static long L(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : 0;
}

/// <summary>The last Manifestation results: "time#e,e,…;e,e,…" (Lua <c>M.results()</c>). The raw string doubles as
/// the change signature the run waits on after a roll or a store.</summary>
internal sealed class ReforgeResults
{
    public string Signature = string.Empty;
    public int[][] Pulls = Array.Empty<int[]>();   // [pull][row] entryId, 0 = already stored

    public static ReforgeResults Parse(string? s)
    {
        var r = new ReforgeResults { Signature = s ?? string.Empty };
        if (string.IsNullOrEmpty(s)) return r;
        int hash = s.IndexOf('#');
        var body = hash >= 0 ? s.Substring(hash + 1) : s;
        if (body.Length == 0) return r;
        var pulls = body.Split(';');
        r.Pulls = new int[pulls.Length][];
        for (int p = 0; p < pulls.Length; p++)
        {
            var ids = pulls[p].Split(',', StringSplitOptions.RemoveEmptyEntries);
            var row = new int[ids.Length];
            for (int k = 0; k < ids.Length; k++)
                row[k] = double.TryParse(ids[k], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;
            r.Pulls[p] = row;
        }
        return r;
    }
}

/// <summary>A planned store: 0-based pull / row (the server's own indices) for target row <see cref="TargetIndex"/>.
/// <see cref="Upgrade"/> = a "Keep better rolls" store for a row that already has a copy in the pocket (spec ⑦).
/// <see cref="Replace"/> = that row is AT its copy cap (spec ⑫): after the store, its lowest plugin copy is discarded.</summary>
internal readonly record struct StorePlan(int Pull, int Row, int TargetIndex, int EntryId, bool Upgrade = false, bool Replace = false);

/// <summary>
/// Pure matching (no game state): which unstored result satisfies which target. Rules from the brief:
/// only unlocked rows with entryId ≠ 0; a target matches an entry of the same Group whose value ≥ the target's
/// minimum; each waiting target takes the best (highest value) matching entry; one entry serves at most one target.
/// Targets are served highest-minimum first so a low "≥" row can't take the only entry a high one could use.
/// <para>With a copy cap ("Keep better rolls", spec ⑦ / ⑫; <c>cap</c> = copies kept per row INCLUDING the first,
/// 0 = Off, <see cref="NoCap"/> = never discard) a SECOND pass runs over the entries the waiting rows left. A stored row
/// UNDER its cap takes an entry STRICTLY higher than its best copy (equal is skipped); a row AT its cap takes one
/// strictly higher than its LOWEST copy, which the run then discards (<see cref="StorePlan.Replace"/>). Only the copies
/// the plugin stored for that row count (<see cref="Target.ActiveCount"/>), never other pocket entries. Waiting plans
/// always come first in the list, so <see cref="FirstPlan"/> never picks an upgrade while a waiting row has a match.
/// The pocket-slot rule and the discard are the run's (Plugin.Run*.cs), not this pure pass.</para>
/// The run stores only the FIRST plan, then re-reads the results and calls this again (indices may shift after a store).
/// </summary>
internal static class Matcher
{
    public const int NoCap = int.MaxValue;

    public static StorePlan? FirstPlan(ImprintCatalog cat, IReadOnlyList<Target> targets, ReforgeResults res, bool[] unlocked,
                                       int cap = 0)
    {
        var plans = Plan(cat, targets, res, unlocked, cap);
        return plans.Count > 0 ? plans[0] : null;
    }

    public static List<StorePlan> Plan(ImprintCatalog cat, IReadOnlyList<Target> targets, ReforgeResults res, bool[] unlocked,
                                       int cap = 0)
    {
        var plans = new List<StorePlan>();
        var taken = new HashSet<(int, int)>();
        Pass(cat, targets, res, unlocked, taken, plans, cap: 0);
        if (cap > 0) Pass(cat, targets, res, unlocked, taken, plans, cap);
        return plans;
    }

    // cap 0 = the waiting pass; cap > 0 = the upgrade pass with that many copies per row.
    private static void Pass(ImprintCatalog cat, IReadOnlyList<Target> targets, ReforgeResults res, bool[] unlocked,
                             HashSet<(int, int)> taken, List<StorePlan> plans, int cap)
    {
        bool upgrade = cap > 0;
        var order = new List<int>();
        for (int t = 0; t < targets.Count; t++)
        {
            var tg = targets[t];
            if (!cat.ByGroup.TryGetValue(tg.Group, out var imp)) continue;
            // Upgrade pass: stored rows whose compared copy's value is known (an entry the live table no longer has can't
            // be compared, so that row is left alone). At the cap the compared copy is the lowest (CapFloorStep), else the best.
            int cmp = AtCap(tg, cap) ? CapFloorStep(tg, cap) : tg.BestStep;
            if (upgrade ? tg.Stored && cmp >= 0 && cmp < imp.Steps.Length : !tg.Stored) order.Add(t);
        }
        order.Sort((a, b) =>
        {
            int c = MinRaw(cat, targets[b]).CompareTo(MinRaw(cat, targets[a]));
            return c != 0 ? c : a.CompareTo(b);
        });

        foreach (var t in order)
        {
            var target = targets[t];
            // Waiting: value ≥ the minimum. Upgrade: value > the best copy still in the pocket (under the cap) or > the
            // lowest one (at the cap — it gets swapped out; CapFloorStep).
            bool replace = upgrade && AtCap(target, cap);
            double floor = upgrade ? cat.ByGroup[target.Group].Steps[replace ? CapFloorStep(target, cap) : target.BestStep].Raw
                                   : MinRaw(cat, target);
            int bestP = -1, bestR = -1, bestId = 0;
            double bestV = double.NegativeInfinity;
            for (int p = 0; p < res.Pulls.Length; p++)
            {
                var row = res.Pulls[p];
                for (int k = 0; k < row.Length; k++)
                {
                    int id = row[k];
                    if (id == 0 || k >= unlocked.Length || !unlocked[k] || taken.Contains((p, k))) continue;
                    if (!cat.ByEntry.TryGetValue(id, out var hit) || hit.Imp.Group != target.Group) continue;
                    double v = hit.Imp.Steps[hit.Step].Raw;
                    if ((upgrade ? v <= floor : v < floor) || v <= bestV) continue;
                    bestV = v; bestP = p; bestR = k; bestId = id;
                }
            }
            if (bestP < 0) continue;
            taken.Add((bestP, bestR));
            plans.Add(new StorePlan(bestP, bestR, t, bestId, upgrade, replace));
        }
    }

    // A finite cap the row's plugin copies already fill (or pass, after the cap was lowered — one swap per upgrade then).
    private static bool AtCap(Target t, int cap) => cap > 0 && cap != NoCap && t.ActiveCount >= cap;

    // The bar a roll must beat at the cap: the cap-th highest known copy. Exactly AT the cap that is the lowest copy
    // (spec ⑫); PAST it (the cap was lowered, e.g. No cap → Best only with 3 copies) only a roll that would make the top
    // `cap` counts — otherwise Best only would keep swapping the bottom copy for one that still isn't the best. The
    // discard is always the lowest copy (Target.Lowest), one per swap. -1 = no known copy (row left alone).
    // On change only (each Match, ≤ a few copies): the O(n²) count allocates nothing.
    private static int CapFloorStep(Target t, int cap)
    {
        foreach (var c in t.Copies)
        {
            if (!c.Active || c.Step < 0) continue;
            int higher = 0, atLeast = 0;
            foreach (var d in t.Copies)
            {
                if (!d.Active || d.Step < 0) continue;
                if (d.Step > c.Step) higher++;
                if (d.Step >= c.Step) atLeast++;
            }
            if (higher < cap && atLeast >= cap) return c.Step;
        }
        return t.LowestStep;   // fewer known copies than the cap (some entries unknown to the live table)
    }

    private static double MinRaw(ImprintCatalog cat, Target t)
    {
        if (!cat.ByGroup.TryGetValue(t.Group, out var imp) || imp.Steps.Length == 0) return double.MaxValue;
        return imp.Steps[Math.Clamp(t.MinStep, 0, imp.Steps.Length - 1)].Raw;
    }
}
