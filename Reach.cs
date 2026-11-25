using System;
using System.Collections.Generic;
using System.Globalization;

namespace Stellar.AutoManifest;

/// <summary>One Matrix level of <c>SeasonTalentReforgeLevelTable</c> (current season): the EXP to the next level and the
/// quality weights per mille, Superior..Immortal (index 0..3 = AffixQuality 2..5).</summary>
internal sealed class MatrixLevel
{
    public int Level;
    public int UpgradeExp;                       // EXP from this level to the next (the in-level bar's max)
    public readonly int[] Normal = new int[4];   // NormalWeight — a pull without Blessing
    public readonly int[] Prime = new int[4];    // PrimeWeight  — a pull with Blessing

    public int[] Weights(bool prime) => prime ? Prime : Normal;
}

/// <summary>
/// The level table as pure data (read once per season, Lua <c>M.levels</c>) and the reachability maths of spec ⑥:
/// a quality Q "can roll" at a level when ANY quality ≥ Q has weight &gt; 0 there — a higher quality's every value is
/// above a lower quality's, so it satisfies a "v or higher" minimum too.
/// </summary>
internal sealed class MatrixLevels
{
    public int SeasonId;
    public int ExpPerBlessPull = 10;   // ExpAdd of the Blessing item (live-confirmed +10 in S4)
    public MatrixLevel[] Rows = Array.Empty<MatrixLevel>();   // ascending by level, Rows[i].Level == i + 1 when complete

    // "season,expAdd;lv,exp,n2,n3,n4,n5,p2,p3,p4,p5;…"
    public static MatrixLevels? Parse(string? dump)
    {
        if (string.IsNullOrEmpty(dump)) return null;
        var recs = dump.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (recs.Length < 2) return null;
        var head = recs[0].Split(',');
        var t = new MatrixLevels { SeasonId = I(head[0]) };
        if (head.Length > 1 && I(head[1]) > 0) t.ExpPerBlessPull = I(head[1]);
        var rows = new List<MatrixLevel>(recs.Length - 1);
        for (int r = 1; r < recs.Length; r++)
        {
            var f = recs[r].Split(',');
            if (f.Length < 10) continue;
            var row = new MatrixLevel { Level = I(f[0]), UpgradeExp = I(f[1]) };
            for (int q = 0; q < 4; q++) { row.Normal[q] = I(f[2 + q]); row.Prime[q] = I(f[6 + q]); }
            rows.Add(row);
        }
        if (rows.Count == 0) return null;
        rows.Sort((a, b) => a.Level.CompareTo(b.Level));
        t.Rows = rows.ToArray();
        return t;
    }

    public MatrixLevel? Row(int level)
    {
        // Rows are 1..N in order; fall back to a scan if the table has a gap.
        int i = level - 1;
        if (i >= 0 && i < Rows.Length && Rows[i].Level == level) return Rows[i];
        foreach (var r in Rows) if (r.Level == level) return r;
        return null;
    }

    public int MaxLevel => Rows.Length > 0 ? Rows[Rows.Length - 1].Level : 0;

    // Quality (2..5) can roll at `level`: some quality ≥ it has weight > 0.
    public bool CanRoll(int level, int quality, bool prime)
    {
        var row = Row(level);
        if (row == null) return false;
        var w = row.Weights(prime);
        for (int q = Math.Clamp(quality - 2, 0, 3); q < 4; q++) if (w[q] > 0) return true;
        return false;
    }

    // The first level ≥ `from` where `quality` can roll; -1 when no level in the table reaches it.
    public int FirstLevel(int from, int quality, bool prime)
    {
        foreach (var r in Rows)
            if (r.Level >= from && CanRoll(r.Level, quality, prime)) return r.Level;
        return -1;
    }

    // EXP still needed from (level, exp inside it) to arrive at `target`.
    public long ExpTo(int level, int exp, int target)
    {
        long need = 0;
        for (int l = level; l < target; l++)
        {
            var r = Row(l);
            if (r == null) return -1;
            need += r.UpgradeExp;
        }
        return Math.Max(0, need - exp);
    }

    private static int I(string s)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : 0;
}
