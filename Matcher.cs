using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.AutoManifest;

/// <summary>One target row: "imprint <see cref="Group"/> at value step <see cref="MinStep"/> or higher". Two rows with
/// the same imprint = two stores. The stored MARK (persisted with the row, Plugin.StoredMarks.cs) names the pocket entry
/// the plugin stored for it: character + season + the entry's uuid + entryId + value text. It counts as stored
/// (<see cref="Stored"/>) only while <see cref="Active"/> — i.e. the CURRENT character's pocket still holds that uuid.</summary>
internal sealed class Target
{
    public int Group;
    public int MinStep;             // 0-based index into Imprint.Steps
    public int Need;                // reachability (Plugin.Reach.cs): 0 = can roll now, N = first Matrix level, -1 = locked

    public string? MarkText;        // the stored value text ("2.72%"); null = no mark
    public string MarkChar = string.Empty;   // CharSerialize.charId of the pocket it went into
    public int MarkSeason, MarkUuid, MarkEntry;   // MarkUuid 0 = stored this session, uuid not resolved yet
    public bool Active;             // the mark is confirmed in the current character's pocket (or pending this session)

    public bool HasMark => MarkText != null;
    public bool Stored => Active && MarkText != null;
    public string? StoredText => Stored ? MarkText : null;

    public void ClearMark()
    {
        MarkText = null; MarkChar = string.Empty; MarkSeason = MarkUuid = MarkEntry = 0; Active = false;
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
