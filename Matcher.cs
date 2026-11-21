using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.AutoManifest;

/// <summary>One target row: "imprint <see cref="Group"/> at value step <see cref="MinStep"/> or higher". Two rows with
/// the same imprint = two stores. <see cref="StoredText"/> is this run's stored value (null = waiting).</summary>
internal sealed class Target
{
    public int Group;
    public int MinStep;             // 0-based index into Imprint.Steps
    public string? StoredText;      // set when this run stored an imprint for this row
    public bool Stored => StoredText != null;
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

    public static ReforgeState Parse(string? s)
    {
        var st = new ReforgeState();
        if (string.IsNullOrEmpty(s)) return st;
        var f = s.Split('|');
        if (f.Length < 14 || f[0] != "1") return st;
        st.SeasonId = I(f[1]); st.Level = I(f[2]); st.StoreLimit = I(f[3]); st.Stored = I(f[4]);
        st.Remnants = L(f[5]); st.Prisms = L(f[6]); st.CostPerPull = I(f[7]);
        st.ConsumeOk1 = f[8] == "1"; st.ConsumeOk10 = f[9] == "1"; st.BlessAllowed = f[10] == "1";
        st.Profession = I(f[11]);
        var mask = f[12];
        st.RowUnlocked = new bool[mask.Length];
        for (int i = 0; i < mask.Length; i++) st.RowUnlocked[i] = mask[i] == '1';
        st.Valid = true;
        return st;
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

/// <summary>A planned store: 0-based pull / row (the server's own indices) for target row <see cref="TargetIndex"/>.</summary>
internal readonly record struct StorePlan(int Pull, int Row, int TargetIndex, int EntryId);

/// <summary>
/// Pure matching (no game state): which unstored result satisfies which waiting target. Rules from the brief:
/// only unlocked rows with entryId ≠ 0; a target matches an entry of the same Group whose value ≥ the target's
/// minimum; each waiting target takes the best (highest value) matching entry; one entry serves at most one target.
/// Targets are served highest-minimum first so a low "≥" row can't take the only entry a high one could use.
/// The run stores only the FIRST plan, then re-reads the results and calls this again (indices may shift after a store).
/// </summary>
internal static class Matcher
{
    public static StorePlan? FirstPlan(ImprintCatalog cat, IReadOnlyList<Target> targets, ReforgeResults res, bool[] unlocked)
    {
        var plans = Plan(cat, targets, res, unlocked);
        return plans.Count > 0 ? plans[0] : null;
    }

    public static List<StorePlan> Plan(ImprintCatalog cat, IReadOnlyList<Target> targets, ReforgeResults res, bool[] unlocked)
    {
        var plans = new List<StorePlan>();
        var order = new List<int>();
        for (int t = 0; t < targets.Count; t++)
            if (!targets[t].Stored && cat.ByGroup.ContainsKey(targets[t].Group)) order.Add(t);
        order.Sort((a, b) =>
        {
            int c = MinRaw(cat, targets[b]).CompareTo(MinRaw(cat, targets[a]));
            return c != 0 ? c : a.CompareTo(b);
        });

        var taken = new HashSet<(int, int)>();
        foreach (var t in order)
        {
            var target = targets[t];
            double min = MinRaw(cat, target);
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
                    if (v < min || v <= bestV) continue;
                    bestV = v; bestP = p; bestR = k; bestId = id;
                }
            }
            if (bestP < 0) continue;
            taken.Add((bestP, bestR));
            plans.Add(new StorePlan(bestP, bestR, t, bestId));
        }
        return plans;
    }

    private static double MinRaw(ImprintCatalog cat, Target t)
    {
        if (!cat.ByGroup.TryGetValue(t.Group, out var imp) || imp.Steps.Length == 0) return double.MaxValue;
        return imp.Steps[Math.Clamp(t.MinStep, 0, imp.Steps.Length - 1)].Raw;
    }
}
