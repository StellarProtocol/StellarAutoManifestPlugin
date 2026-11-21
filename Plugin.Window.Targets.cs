using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The TARGETS list (approved ①, spec ⑧): one raised tile per target row in a fixed 7-row viewport that scrolls past 7, with
/// reserved empty slots below the real rows so the list keeps its height (house rules 14/15). A row is
///   [4 px quality bar | imprint name | "v+" pill dropdown | waiting / stored / needs Lv N / locked badge | icon-only ✕]
/// The quality bar takes the colour of the chosen minimum's quality; the dropdown lists exactly the game's rollable
/// steps; ✕ removes at once. Rows are built ONCE into a fixed pool (<see cref="MaxTargets"/>) and read live. Under the
/// list: "+ Add target" (left) and "Pocket N/M" (right, spec ⑨ — opens the pocket window, Plugin.Pocket.cs).
/// <para>⚠️ Toolkit-forced size: the approved row is a 26 px tile, but the framework's Pill dropdown trigger is a fixed
/// 28 px, so the tile is 30 px (1 px above / below the trigger) and the pitch 32.</para>
/// </summary>
public sealed partial class Plugin
{
    private const int   TargetVisibleRows = 7;   // spec ⑧ (was 5)
    private const float TargetTileH = 30f;
    private const float TargetGap   = 2f;
    private const float TargetListH = TargetVisibleRows * (TargetTileH + TargetGap) - TargetGap;   // 222
    private const float TargetMinW  = 96f;
    private const float TargetBadgeW = 96f;
    private const float AddTargetW  = 132f;
    private const float PocketPillW = 132f;
    private const float QualityBarW = 4f;

    private static readonly string[] NoOptions = { "—" };

    private readonly HoverSlots _rowHover = new(MaxTargets);

    // Per-pool-slot ✕ hover; reset when the rows shift under the pointer (a remove).
    private sealed class HoverSlots
    {
        public readonly bool[] Remove;
        public HoverSlots(int n) => Remove = new bool[n];
        public void Reset() => Array.Clear(Remove, 0, Remove.Length);
    }

    private HudElement BuildTargetsPanel() => SectionPanel(KeyTargetsCaption, () => _loc.T(KeyTargetsHint), new HudElement[]
    {
        new ScrollElement(new ListElement(TargetSlotCount, BuildTargetPool(), Columns: 1) { Gap = TargetGap },
            Height: TargetListH) { FixedHeight = true },
        // spec ⑨: "Pocket N/M" right-aligned on the same row; opens the pocket window (viewable during a run too — only its
        // ✕ lock then).
        new RowElement(new HudElement[]
        {
            PillButton(() => _loc.T(KeyAddTarget), PillKind.Neutral, OpenPicker, AddTargetW, PanelColor,
                () => !Running && _catalogReady && _targets.Count < MaxTargets),
            new SpacerElement(0f),
            PillButton(PocketPillLabel, PillKind.Neutral, OpenPocket, PocketPillW, PanelColor, CanOpenPocket),
        }, Gap: 0.01f),
    });

    // "Pocket 3/5" — rebuilt only when the count, the limit or the language moves.
    private int _pocketPillN = -1, _pocketPillM = -1, _pocketPillGen = -1;
    private string _pocketPill = string.Empty;

    private string PocketPillLabel()
    {
        int n = _st.Valid ? _st.Stored : -1, m = _st.Valid ? _st.StoreLimit : -1;
        if (n == _pocketPillN && m == _pocketPillM && _langGen == _pocketPillGen && _pocketPill.Length > 0) return _pocketPill;
        _pocketPillN = n; _pocketPillM = m; _pocketPillGen = _langGen;
        return _pocketPill = n < 0 ? _loc.TFormat(KeyPocketPill, "—", "—") : _loc.TFormat(KeyPocketPill, n, m);
    }

    // Real rows + empty slots up to the 7-row viewport; 0 while hidden (the Prebuild fill binds nothing).
    private int TargetSlotCount() => _mainWindow?.IsShown == true ? Math.Min(MaxTargets, Math.Max(TargetVisibleRows, _targets.Count)) : 0;

    private HudElement[] BuildTargetPool()
    {
        var pool = new HudElement[MaxTargets];
        for (int i = 0; i < MaxTargets; i++)
        {
            int idx = i;
            // Two COMPLETE tiles swapped around the bubble (never a Conditional inside a bubble's measured child).
            pool[i] = new ConditionalElement(() => idx < _targets.Count, BuildTargetRow(idx), BuildEmptySlot());
        }
        return pool;
    }

    // An empty, reserved slot: no fill, a faint 1 px rim (the prototype's empty/dashed slot — the toolkit has no dash).
    private HudElement BuildEmptySlot() => new BubbleElement(new SpacerElement(0f, TargetTileH - 2f))
    {
        FillColorFn = () => Transparent,
        OutlineColorFn = () => Fade(MutedColor() with { A = 0.22f }), OutlineWidth = 1f,
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 1f,
    };

    private Target? TargetAt(int i) => i < _targets.Count ? _targets[i] : null;

    private Imprint? ImprintAt(int i)
        => TargetAt(i) is { } t && _catalog != null && _catalog.ByGroup.TryGetValue(t.Group, out var imp) ? imp : null;

    // "Lucky Strike · Oath DMG" for a class imprint (same wording as the picker row), the bare name otherwise. Cached per
    // slot; rebuilt only when that slot's name / kind strings or the muted colour change (names re-arrive on a language
    // switch as new strings, so reference checks cover it).
    private readonly string?[] _tlName = new string?[MaxTargets], _tlKind = new string?[MaxTargets];
    private readonly ColorRgba[] _tlMuted = new ColorRgba[MaxTargets];
    private readonly string[] _tlText = new string[MaxTargets];

    private string TargetName(int i)
    {
        if (ImprintAt(i) is not { } imp) return TargetAt(i) is { } t ? "#" + t.Group : string.Empty;
        if (imp.Kind.Length == 0) return imp.Name;
        var muted = MutedColor();
        if (ReferenceEquals(imp.Name, _tlName[i]) && ReferenceEquals(imp.Kind, _tlKind[i]) && muted.Equals(_tlMuted[i]) && _tlText[i] != null)
            return _tlText[i];
        _tlName[i] = imp.Name; _tlKind[i] = imp.Kind; _tlMuted[i] = muted;
        return _tlText[i] = ImprintLine(imp, muted, withCategory: false);
    }

    private int TargetMin(int i) => TargetAt(i)?.MinStep ?? 0;

    private System.Collections.Generic.IReadOnlyList<string> TargetOptions(int i)
        => ImprintAt(i) is { MinOptions.Length: > 0 } imp ? imp.MinOptions : NoOptions;

    private ColorRgba TargetQualityColor(int i)
    {
        var imp = ImprintAt(i);
        if (imp == null || imp.Steps.Length == 0) return MutedColor();
        return QualityColor(imp.Steps[Math.Clamp(TargetMin(i), 0, imp.Steps.Length - 1)].Quality);
    }

    // "stored 2.72%" per slot, rebuilt only when that row's stored value or the language changes.
    private readonly string?[] _storedLabelSrc = new string?[MaxTargets];
    private readonly string[] _storedLabel = new string[MaxTargets];
    private readonly int[] _storedLabelGen = new int[MaxTargets];

    private string StoredLabel(int i)
    {
        var src = TargetAt(i)?.StoredText;
        if (src == null) return string.Empty;
        if (ReferenceEquals(src, _storedLabelSrc[i]) && _storedLabelGen[i] == _langGen && _storedLabel[i] != null) return _storedLabel[i];
        _storedLabelSrc[i] = src; _storedLabelGen[i] = _langGen;
        return _storedLabel[i] = _loc.TFormat(KeyStored, src);
    }

    private HudElement BuildTargetRow(int idx)
    {
        var qualityBar = new BubbleElement(new SpacerElement(QualityBarW, TargetTileH - 10f))
        {
            FillColorFn = () => Fade(TargetQualityColor(idx)),
            CornerRadius = RoundRadius(QualityBarW), PaddingX = 0f, PaddingY = 0f,
        };

        var name = new CellElement(new TextElement(() => TargetName(idx), Color: () => TextColor(), NoWrap: true)
            { Ellipsize = true }, Weight: 1f);

        // Changing the minimum is ignored while a run is going (SetTargetMin) — the dropdown has no disabled look.
        var min = new CellElement(new DropdownElement(() => TargetMin(idx), () => TargetOptions(idx), s => SetTargetMin(idx, s),
            Width: TargetMinW) { Style = DropdownStyle.Pill, CornerRadius = 13f, MaxVisibleRows = 11 }, Width: TargetMinW);

        var badge = new CellElement(new ConditionalElement(() => TargetAt(idx)?.Stored == true,
            Badge(() => StoredLabel(idx), SuccessColor, TileColor, TargetBadgeW),
            new ConditionalElement(() => TargetNeed(idx) != 0,
                Badge(() => NeedLabel(idx), CautionColor, TileColor, TargetBadgeW),   // "needs Lv 4" / "locked" (Plugin.Reach.cs)
                Badge(() => _loc.T(KeyWaiting), MutedColor, TileColor, TargetBadgeW, muted: true))), Width: TargetBadgeW);

        var remove = CrossButton(() => _rowHover.Remove[idx], over => _rowHover.Remove[idx] = over,
            () => RemoveTarget(idx), TileColor, danger: true, enabled: () => !Running);

        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(2f, TargetTileH - 2f),   // lead-in + height strut (tile = 28 + 2 × 1 px padding)
            qualityBar, name, min, badge, remove,
            new SpacerElement(1f, 1f),
        }, Gap: 6f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 2f, PaddingY = 1f,
        };
    }
}
