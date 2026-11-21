using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Stellar.AutoManifest;

/// <summary>One rollable value of an imprint: the table row (<c>entryId</c>), its raw value and quality, and the text
/// the game shows for it.</summary>
internal sealed class ImprintStep
{
    public int EntryId;
    public double Raw;          // AttrEffect[1][3] (attribute) or BuffPar[1][1] (class buff) — what matching compares
    public int Quality;         // AffixQuality: 2 Superior, 3 Excellent, 4 Epic, 5 Immortal
    public string ValueText = string.Empty;
}

/// <summary>One imprint type = one <c>Group</c> of <c>SeasonTalentFactorAttrLibTable</c> with its stepped values.</summary>
internal sealed class Imprint
{
    public int Group;
    public int AffixType;       // 1 attribute, 2 class
    public int AttrLibId;       // 101 Secondary, 102 Primary, 103 Polarity, 104 Resilience, 105 Element, 1001 Class
    public int Profession;      // 0 = every class
    public bool IsBuff;         // AttrEffect type 3: value lives in BuffPar, shown as raw/100 %
    public string Name = string.Empty;
    public string Kind = string.Empty;       // class imprints: the effect text around the quoted name ("Twin Striker's … Oath DMG")
    public string NameLower = string.Empty;
    public string RangeText = string.Empty;
    public ImprintStep[] Steps = Array.Empty<ImprintStep>();   // ascending by entry id (= ascending value)
    public string[] MinOptions = Array.Empty<string>();         // "≥ v" per step — the row dropdown's options (cached)
}

/// <summary>
/// The imprint catalogue, read from the game's own tables at runtime (never per frame): a compact row dump first
/// (<see cref="ParseRows"/>), then the localized names / value texts in slices (<see cref="ApplyNames"/>), so every
/// name follows the client language. Pure managed data — no game objects are held.
/// </summary>
internal sealed class ImprintCatalog
{
    public readonly List<Imprint> All = new();
    public readonly Dictionary<int, Imprint> ByGroup = new();
    public readonly Dictionary<int, (Imprint Imp, int Step)> ByEntry = new();

    // Row dump: "id,group,affixType,attrLibId,profession,quality,effectType,value,extremum;…" (Lua M.rows).
    public static ImprintCatalog ParseRows(string dump)
    {
        var cat = new ImprintCatalog();
        var tmp = new Dictionary<int, List<(int id, double v, int q)>>();
        foreach (var rec in dump.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Split(',');
            if (f.Length < 8) continue;
            if (!int.TryParse(f[0], out var id) || !int.TryParse(f[1], out var group)) continue;
            int.TryParse(f[2], out var affix);
            int.TryParse(f[3], out var lib);
            int.TryParse(f[4], out var prof);
            int.TryParse(f[5], out var quality);
            int.TryParse(f[6], out var effectType);
            double.TryParse(f[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var value);
            if (!cat.ByGroup.TryGetValue(group, out var imp))
            {
                imp = new Imprint { Group = group, AffixType = affix, AttrLibId = lib, Profession = prof, IsBuff = effectType == 3 };
                cat.ByGroup[group] = imp;
                cat.All.Add(imp);
                tmp[group] = new List<(int, double, int)>();
            }
            tmp[group].Add((id, value, quality));
        }
        foreach (var imp in cat.All)
        {
            var rows = tmp[imp.Group];
            rows.Sort((a, b) => a.id.CompareTo(b.id));
            imp.Steps = new ImprintStep[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                imp.Steps[i] = new ImprintStep { EntryId = rows[i].id, Raw = rows[i].v, Quality = rows[i].q,
                                                 ValueText = imp.IsBuff ? BuffValueText(rows[i].v) : FormatRaw(rows[i].v) };
                cat.ByEntry[rows[i].id] = (imp, i);
            }
            imp.Name = "#" + imp.Group;   // until the localized names arrive
            imp.NameLower = imp.Name;
            imp.RebuildTexts();
        }
        cat.All.Sort((a, b) => a.Group.CompareTo(b.Group));
        return cat;
    }

    // Names dump for a slice: records split by \x1E, fields by \x1F: group, name, valueText per step (Lua M.names).
    // Class (buff) imprints keep the plugin's own value text: the game's buff value text is a whole effect sentence.
    public void ApplyNames(string dump)
    {
        foreach (var rec in dump.Split('\x1E', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = rec.Split('\x1F');
            if (f.Length < 3 || !int.TryParse(f[0], out var group) || !ByGroup.TryGetValue(group, out var imp)) continue;
            var name = StripTags(f[1]).Trim();
            if (name.Length > 0) { imp.Name = name; imp.NameLower = name.ToLowerInvariant(); }
            imp.Kind = StripTags(f[2]).Trim();
            if (!imp.IsBuff)
                for (int i = 0; i < imp.Steps.Length && i + 3 < f.Length; i++)
                {
                    var v = StripTags(f[i + 3]).Trim();
                    if (v.Length > 0) imp.Steps[i].ValueText = v;
                }
            imp.RebuildTexts();
        }
    }

    // The ids of every group in [from, to) as "group:id,id,…;" — the argument of Lua M.names for one slice.
    public string NamesSpec(int from, int to)
    {
        var sb = new StringBuilder();
        for (int g = from; g < to && g < All.Count; g++)
        {
            var imp = All[g];
            sb.Append(imp.Group).Append(':');
            for (int i = 0; i < imp.Steps.Length; i++) { if (i > 0) sb.Append(','); sb.Append(imp.Steps[i].EntryId); }
            sb.Append(';');
        }
        return sb.ToString();
    }

    // Class imprint values are stored ×100 ("unmarkpercent"): 840 → 8.40 %.
    private static string BuffValueText(double raw) => (raw / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";

    private static string FormatRaw(double raw) => raw.ToString("0.##", CultureInfo.InvariantCulture);

    // The game wraps names in quality <style=…> / <color=…> tags; the plugin colours by quality itself.
    internal static string StripTags(string s)
    {
        if (s.IndexOf('<') < 0) return s;
        var sb = new StringBuilder(s.Length);
        bool inTag = false;
        foreach (var ch in s)
        {
            if (ch == '<') { inTag = true; continue; }
            if (ch == '>' && inTag) { inTag = false; continue; }
            if (!inTag) sb.Append(ch);
        }
        return sb.ToString();
    }
}

internal static class ImprintText
{
    public const string AtLeast = "≥ ";

    public static void RebuildTexts(this Imprint imp)
    {
        if (imp.Steps.Length == 0) { imp.RangeText = string.Empty; imp.MinOptions = Array.Empty<string>(); return; }
        string lo = imp.Steps[0].ValueText, hi = imp.Steps[imp.Steps.Length - 1].ValueText;
        // "1.28 – 9.60%": the unit once, at the end, when both ends carry it.
        if (lo.EndsWith("%", StringComparison.Ordinal) && hi.EndsWith("%", StringComparison.Ordinal)) lo = lo.Substring(0, lo.Length - 1);
        imp.RangeText = lo + " – " + hi;
        var opts = new string[imp.Steps.Length];
        for (int i = 0; i < opts.Length; i++) opts[i] = AtLeast + imp.Steps[i].ValueText;
        imp.MinOptions = opts;
    }
}
