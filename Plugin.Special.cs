using System;
using System.Collections.Generic;
using System.Text;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>One special reforge item (spec ⑬): a <c>SeasonTalentReforgeItemTable</c> row that is not the Blessing.</summary>
internal sealed class SpecialItem
{
    // E.SeasonTalentReforgeItemType.AttrLib (enum_define.lua:1393): "all imprints of the next pull are <category>".
    public const int TypeCategory = 1;
    // 1220401 "…guaranteed to be Primary Attribute Imprints" ships an EMPTY Param, while every other category row's Param
    // is its category's AttrLibId (1220402 Secondary = 101, 1220403 Polarity = 103, 1220404 Resilience = 104,
    // 1220405 Element = 105, 1220406 Class = 1001). The imprint table's AttrLibIds are exactly {101, 102, 103, 104, 105,
    // 1001}, so the one left over — 102 — is Primary (KB Matrix-Manifestation §4). The client never reads Param itself.
    public const int PrimaryLibId = 102;

    public int Id, Type;
    public int Lib;   // the forced category's AttrLibId (TypeEnum 1 only), else 0 = rolls every category
    public string Name = string.Empty;

    // Lua M.specials(): "id,type,param1" US name, records RS-separated; param1 = 0 when the row has no Param.
    public static SpecialItem[] ParseAll(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<SpecialItem>();
        var recs = s.Split('\x1E', StringSplitOptions.RemoveEmptyEntries);
        var list = new List<SpecialItem>(recs.Length);
        foreach (var rec in recs)
        {
            int us = rec.IndexOf('\x1F');
            var head = (us >= 0 ? rec.Substring(0, us) : rec).Split(',');
            if (head.Length < 3 || !int.TryParse(head[0], out var id) || id == 0) continue;
            int.TryParse(head[1], out var type);
            int.TryParse(head[2], out var p);
            var name = us >= 0 ? ImprintCatalog.StripTags(rec.Substring(us + 1)).Trim() : string.Empty;
            list.Add(new SpecialItem
            {
                Id = id, Type = type, Lib = type == TypeCategory ? (p != 0 ? p : PrimaryLibId) : 0,
                Name = name.Length > 0 ? name : "#" + id,
            });
        }
        return list.ToArray();
    }
}

/// <summary>
/// "Special item" (approved spec ⑬, run-out behaviour A = stop): one special reforge item per pull, sent next to the
/// Blessing row in <c>bless_items</c>. RUN row = label + muted sub-line + a pill dropdown listing "None" and only the items
/// the game would let you use now (owned &gt; 0 AND their <c>Condition</c> passes), each "&lt;game item name&gt; · &lt;count&gt;".
/// The pick is saved by item id; a saved item that is unavailable now shows (and acts) as None, so nothing is ever spent
/// unless picked AND usable. A category item (TypeEnum 1) only rolls one imprint category: waiting targets of other
/// categories get the amber "not with item" badge, the footer an amber hint, the confirm card amber lines, and the run
/// stops when every still-waiting target is blocked. Before each pull the run stops when owned &lt; pull size — it never
/// falls back to pulling without the item.
/// <para>Perf: counts come in the existing state read (≤ 1 Hz idle / per run step); the option captions are rebuilt only
/// when that raw count string (same instance while unchanged) or the language moves. Every polled Func is allocation-free.</para>
/// </summary>
public sealed partial class Plugin
{
    // Pill width: room for "Lineage Grating · 23" on the closed pill (caption room = width − 24 − 14); the longest game names
    // ("Purification Grating - Excellent · 4") ellipsize on the pill, in full in the open list.
    private const float SpecialDropdownW = 220f;

    private SpecialItem[] _specials = Array.Empty<SpecialItem>();
    private int _specialId;        // saved pick (config "specialItem"), 0 = None
    private int _runSpecialId;     // the item the current / last run uses (fixed at Start), 0 = none
    private int _runSpecialUsed;   // items spent by this run (1 per pull of every accepted roll)
    private int _endSpecialId, _endSpecialUsed;
    private long _endSpecialLeft;
    private int _endSpecialTimes;   // the pull size the run-out check failed for
    private string _endBlockedNames = string.Empty;
    // The idle footer's amber item hint yields to the end line of the last run, until the item or the targets change.
    private bool _endYield;

    private SpecialItem? SpecialById(int id)
    {
        if (id == 0) return null;
        foreach (var s in _specials) if (s.Id == id) return s;
        return null;
    }

    // The item the next pull uses: the run's own while running; idle = the saved pick only when the game lets you use it.
    private SpecialItem? PickedSpecial => Running ? SpecialById(_runSpecialId)
        : _specialId != 0 && _st.Valid && _st.SpecialUsable(_specialId) ? SpecialById(_specialId) : null;

    private void LoadSpecialConfig() => _specialId = Math.Max(0, _cfg.Get("specialItem", 0));

    // ── dropdown ──────────────────────────────────────────────────────────────────────────────────────────────────
    private string? _specialOptsSrc;   // the state's SpecialSrc instance the options were built from
    private int _specialOptsGen = -1;
    private string[] _specialOpts = Array.Empty<string>();
    private int[] _specialOptIds = { 0 };

    private void RefreshSpecialOptions()
    {
        var src = _st.Valid ? _st.SpecialSrc : null;
        if (ReferenceEquals(src, _specialOptsSrc) && _specialOptsGen == _langGen && _specialOpts.Length > 0) return;
        _specialOptsSrc = src; _specialOptsGen = _langGen;
        var names = new List<string> { _loc.T(KeySpecialNone) };
        var ids = new List<int> { 0 };
        if (_st.Valid)
            foreach (var s in _specials)
            {
                if (!_st.SpecialUsable(s.Id)) continue;
                names.Add(_loc.TFormat(KeySpecialOption, s.Name, N0(_st.SpecialCount(s.Id))));
                ids.Add(s.Id);
            }
        _specialOpts = names.ToArray();
        _specialOptIds = ids.ToArray();
    }

    private IReadOnlyList<string> SpecialOptions() { RefreshSpecialOptions(); return _specialOpts; }

    private int SpecialSelected()
    {
        RefreshSpecialOptions();
        int i = Array.IndexOf(_specialOptIds, _specialId);
        return i < 0 ? 0 : i;
    }

    private void SetSpecialIndex(int i)
    {
        if (Running || i < 0 || i >= _specialOptIds.Length) return;
        int id = _specialOptIds[i];
        if (id == _specialId) return;
        _specialId = id; _cfg.Set("specialItem", id); _cfg.Save();
        _endYield = true;
        MarkMain();
    }

    // While running: the same caption on a recessed, inert pill (the toolkit's dropdown has no disabled look). The run's
    // own item, with its live count, even when the count reaches 0 (the option list would drop it to "None").
    private long _runCapCount = -1;
    private int _runCapId = -1, _runCapGen = -1;
    private string _runCap = string.Empty;

    private string SpecialRunCaption()
    {
        var it = SpecialById(_runSpecialId);
        if (it == null) return _loc.T(KeySpecialNone);
        long n = _st.SpecialCount(it.Id);
        if (n == _runCapCount && it.Id == _runCapId && _langGen == _runCapGen) return _runCap;
        _runCapCount = n; _runCapId = it.Id; _runCapGen = _langGen;
        return _runCap = _loc.TFormat(KeySpecialOption, it.Name, N0(n));
    }

    private HudElement SpecialControl() => new CellElement(new ConditionalElement(() => Running,
        PillButton(SpecialRunCaption, PillKind.Neutral, () => { }, SpecialDropdownW, PanelColor, () => false),
        new DropdownElement(SpecialSelected, SpecialOptions, SetSpecialIndex, Width: SpecialDropdownW)
            { Style = DropdownStyle.Pill, CornerRadius = 13f, MaxVisibleRows = 11 }), Width: SpecialDropdownW);

    // MATERIALS third row: the picked item's live count. The slot is always there (an empty strut with no item), so
    // picking an item never changes the window height.
    private readonly CountText _matSpecialText = new();

    private HudElement SpecialMaterialRow() => new ConditionalElement(() => PickedSpecial != null,
        MaterialRow(() => PickedSpecial?.Name ?? string.Empty,
            () => _matSpecialText.Get(PickedSpecial is { } s && _st.Valid ? _st.SpecialCount(s.Id) : -1)),
        FixedLine(MaterialRowH, new SpacerElement(0f, MaterialRowH)));

    // ── category block ────────────────────────────────────────────────────────────────────────────────────────────
    // A waiting, known target the picked category item can't roll. Stored rows keep their "stored" badge.
    private bool TargetBlocked(Target t)
    {
        if (t.Stored || PickedSpecial is not { Lib: > 0 } it || _catalog == null
            || !_catalog.ByGroup.TryGetValue(t.Group, out var imp)) return false;
        return imp.AttrLibId != it.Lib;
    }

    private bool TargetBlocked(int i) => TargetAt(i) is { } t && TargetBlocked(t);

    private int BlockedWaitingCount()
    {
        int n = 0;
        foreach (var t in _targets) if (TargetBlocked(t)) n++;
        return n;
    }

    // Every still-waiting target is blocked → nothing the item can still roll is wanted (run stop, spec ⑬).
    private bool SpecialBlocksAll()
    {
        int waiting = WaitingCount();
        return waiting > 0 && BlockedWaitingCount() == waiting;
    }

    private string BlockedNames()
    {
        var sb = new StringBuilder();
        foreach (var t in _targets)
        {
            if (!TargetBlocked(t)) continue;
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(NameOfGroup(t.Group));
        }
        return sb.ToString();
    }

    // Footer (idle): "Lineage Grating only rolls Class imprints. 1 target can't roll with it." (amber)
    private bool SpecialHintShown() => !Running && BlockedWaitingCount() > 0 && (_endReason == EndReason.None || _endYield);

    private (SpecialItem? it, int n, int gen) _hintKey;
    private string _hintText = string.Empty;

    private string SpecialHintText()
    {
        var it = PickedSpecial;
        int n = BlockedWaitingCount();
        if (it == null || n == 0) return string.Empty;
        if (ReferenceEquals(it, _hintKey.it) && n == _hintKey.n && _langGen == _hintKey.gen) return _hintText;
        _hintKey = (it, n, _langGen);
        var cat = CategoryNameForLib(it.Lib);
        return _hintText = n == 1 ? _loc.TFormat(KeySpecialHintOne, it.Name, cat) : _loc.TFormat(KeySpecialHintMany, it.Name, cat, n);
    }

    // ── confirm card (amber) ──────────────────────────────────────────────────────────────────────────────────────
    // "Uses 1 Lineage Grating per pull, 10 per 10-pull (you have 23). The run stops when they run out." + "Fire Attack
    // can't roll while Lineage Grating is used." Built once per Start click.
    private string? ConfirmSpecialLines()
    {
        var it = PickedSpecial;
        if (it == null) return null;
        var have = N0(_st.SpecialCount(it.Id));
        var uses = Amber(PullTimes == 10 ? _loc.TFormat(KeyConfirmSpecial10, it.Name, have) : _loc.TFormat(KeyConfirmSpecial1, it.Name, have));
        var names = BlockedNames();
        return names.Length == 0 ? uses : JoinParagraphs(uses, Amber(_loc.TFormat(KeyConfirmSpecialBlocked, names, it.Name)));
    }

    private string Amber(string line)
        => "<color=#" + HexOf(EnsureContrast(CautionColor(), TileColor(), PillMinContrast)) + ">" + line + "</color>";

    // ── run ───────────────────────────────────────────────────────────────────────────────────────────────────────
    private void BeginSpecial()
    {
        _runSpecialId = PickedSpecial?.Id ?? 0;   // idle rule: usable now, else none (called before the phase leaves Idle)
        _runSpecialUsed = 0;
    }

    // Before each pull (Cycle): every waiting target blocked → stop; fewer items than the pull size → stop.
    private EndReason SpecialPreCheck(int times)
    {
        if (_runSpecialId == 0) return EndReason.None;
        if (SpecialBlocksAll()) return EndReason.SpecialBlocked;
        long have = _st.SpecialCount(_runSpecialId);
        if (have < times) { _endSpecialLeft = have; _endSpecialTimes = times; return EndReason.NoSpecial; }
        return EndReason.None;
    }

    private void CaptureEndSpecial()
    {
        _endSpecialId = _runSpecialId; _endSpecialUsed = _runSpecialUsed;
        _endBlockedNames = _endReason == EndReason.SpecialBlocked ? BlockedNames() : string.Empty;
        _endYield = false;
    }

    // "Out of Lineage Grating (3 left, a 10-pull needs 10) · 1 target still waiting · 240 Remnants used"
    private string EndNoSpecialText()
    {
        var name = SpecialById(_endSpecialId)?.Name ?? string.Empty;
        var head = _endSpecialTimes == 10 ? _loc.TFormat(KeyEndNoSpecial10, name, N0(_endSpecialLeft)) : _loc.TFormat(KeyEndNoSpecial1, name);
        var waiting = _endWaiting switch
        {
            <= 0 => string.Empty,
            1    => _loc.T(KeyEndWaitingOne),
            _    => _loc.TFormat(KeyEndWaitingMany, _endWaiting),
        };
        return head + waiting + _loc.TFormat(KeyEndSpent, N0(_endSpent));
    }

    // "Fire Attack can't roll with Lineage Grating, and every other target is stored"
    private string EndSpecialBlockedText()
    {
        var name = SpecialById(_endSpecialId)?.Name ?? string.Empty;
        return _endBlockedNames.Contains(", ")
            ? _loc.TFormat(KeyEndSpecialBlockedMany, _endBlockedNames, name)
            : _loc.TFormat(KeyEndSpecialBlockedOne, _endBlockedNames, name);
    }

    // Done line: " · 14 Lineage Grating used".
    private string EndSpecialSuffix()
        => _endReason == EndReason.AllStored && _endSpecialUsed > 0 && SpecialById(_endSpecialId) is { } it
            ? _loc.TFormat(KeyEndSpecialUsed, N0(_endSpecialUsed), it.Name) : string.Empty;
}
