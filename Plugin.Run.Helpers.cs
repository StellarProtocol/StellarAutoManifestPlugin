using System.Text;

namespace Stellar.AutoManifest;

/// <summary>
/// Run helpers split out of <c>Plugin.Run.cs</c> (500-LoC cap): the "Keep better rolls" slot rule and the log text
/// builders the run loop uses.
/// </summary>
public sealed partial class Plugin
{
    // ── "Keep better rolls" slot rule (spec ⑦) ────────────────────────────────────────────────────────────────────
    // An upgrade takes a slot only while one stays free for EVERY waiting row: (limit − pocket − keep-free) > waiting.
    // Otherwise the amber "skipped … last free slot is kept for <first waiting target>" line, and the run rolls on.
    private bool UpgradeFits(StorePlan p, int waiting)
    {
        int free = _st.StoreLimit - _st.Stored - KeepFree;
        if (free > waiting) return true;
        int keptFor = FirstWaitingIndex();
        var t = _targets[p.TargetIndex];
        _services.Log.Info($"[AutoManifest] better roll skipped: pull={p.Pull + 1} row={p.Row + 1} entry={p.EntryId} "
                           + $"{EntryText(p.EntryId)} for target #{p.TargetIndex + 1} (best entry={t.Best?.Entry ?? 0} "
                           + $"uuid={t.Best?.Uuid ?? 0}) · free={free} (pocket={_st.Stored}/{_st.StoreLimit} keepFree={KeepFree}) "
                           + $"waiting={waiting} · slot kept for target #{keptFor + 1}");
        SetFooter(FooterEvent.Skipped, p.EntryId, keptFor >= 0 ? _targets[keptFor].Group : 0);
        return false;
    }

    private int FirstWaitingIndex()
    {
        for (int i = 0; i < _targets.Count; i++)
            if (!_targets[i].Stored && _catalog != null && _catalog.ByGroup.ContainsKey(_targets[i].Group)) return i;
        return -1;
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
            if (t.Copies.Count > 1) sb.Append("[").Append(t.Copies.Count).Append(" copies]");
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
