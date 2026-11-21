using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The pocket window's list (spec ⑨): a <see cref="VirtualListElement"/> over the sorted pocket (26 px tiles + 2 px gap =
/// 28 pitch, 6 rows visible, scrolls past 6, fixed height). A row is
///   [4 px quality bar | name (+ muted "· effect" for class imprints, same text as the target rows) | value |
///    green "target &lt;name&gt;" tag when the copy is one Auto Manifest stored for a target row | icon-only ✕ (discard)]
/// then one faint "free slot" tile per free unlocked slot (the toolkit has no dashed outline — same faint 1 px rim as the
/// main window's reserved target slots).
/// <para>The sorted view (and every row text) is rebuilt in <see cref="RefreshPocketView"/> only when the pocket text,
/// the slot limit, the marks / targets, the catalogue, the language or the muted colour moved — at most once per state
/// read; the polled Funcs only index cached arrays (Plugin-Performance-Rules §2).</para>
/// </summary>
public sealed partial class Plugin
{
    private const int   MaxPocket     = 30;    // SeasonTalentReforgeStorageTable: 30 slots at most (1-5 free, 6-30 unlocked)
    private const int   PocketPool    = 9;
    private const float PocketTileH   = 26f;
    private const float PocketPitch   = 28f;
    private const float PocketListH   = 6 * PocketPitch - 2f;   // 166
    private const float PocketValueW  = 64f;
    private const float PocketTagW    = 140f;

    // Sorted view, index = list position. Real entries first [0, _pkCount), then free slots up to _pkSlots.
    private readonly int[] _pkUuid = new int[MaxPocket], _pkEntry = new int[MaxPocket], _pkTarget = new int[MaxPocket];
    private readonly int[] _pkStepOf = new int[MaxPocket];
    private readonly Imprint?[] _pkImp = new Imprint?[MaxPocket];
    private readonly string[] _pkName = new string[MaxPocket], _pkValue = new string[MaxPocket], _pkTag = new string[MaxPocket];
    private int _pkCount, _pkSlots, _pocketOffset;
    private readonly bool[] _pocketHover = new bool[PocketPool];

    // Rebuild key.
    private string? _pkSrc;
    private int _pkLimit = -1, _pkMarksVer = -1, _pkTargetsVer = -1, _pkGen = -1;
    private bool _pkCatReady;
    private ColorRgba _pkMuted;

    private void RefreshPocketView()
    {
        if (!_pocketOpen) return;   // rebuilt on open (OpenPocket clears the key)
        string src = _st.Valid && _st.HasPocket ? _st.PocketSrc : string.Empty;
        int limit = _st.Valid ? _st.StoreLimit : 0;
        var muted = MutedColor();
        if (string.Equals(src, _pkSrc, StringComparison.Ordinal) && limit == _pkLimit && _marksVer == _pkMarksVer
            && _targetsVer == _pkTargetsVer && _langGen == _pkGen && _catalogReady == _pkCatReady && muted.Equals(_pkMuted)) return;
        _pkSrc = src; _pkLimit = limit; _pkMarksVer = _marksVer; _pkTargetsVer = _targetsVer; _pkGen = _langGen;
        _pkCatReady = _catalogReady; _pkMuted = muted;

        int n = src.Length == 0 ? 0 : Math.Min(MaxPocket, _st.PocketUuid.Length);
        _pkCount = 0;
        for (int i = 0; i < n; i++)
        {
            int uuid = _st.PocketUuid[i], entry = _st.PocketEntry[i];
            if (entry == 0) continue;
            Imprint? imp = null;
            int step = -1;
            if (_catalog != null && _catalog.ByEntry.TryGetValue(entry, out var hit)) { imp = hit.Imp; step = hit.Step; }
            // Insertion sort (≤ 30 rows, on change only): best first.
            int k = _pkCount++;
            while (k > 0 && Better(imp, step, uuid, _pkImp[k - 1], _pkStepOf[k - 1], _pkUuid[k - 1]))
            {
                _pkUuid[k] = _pkUuid[k - 1]; _pkEntry[k] = _pkEntry[k - 1]; _pkImp[k] = _pkImp[k - 1]; _pkStepOf[k] = _pkStepOf[k - 1];
                k--;
            }
            _pkUuid[k] = uuid; _pkEntry[k] = entry; _pkImp[k] = imp; _pkStepOf[k] = step;
        }
        _pkSlots = Math.Min(MaxPocket, Math.Max(_pkCount, limit));

        for (int i = 0; i < _pkCount; i++)
        {
            var imp = _pkImp[i];
            _pkName[i] = imp == null ? "#" + _pkEntry[i] : ImprintLine(imp, muted, withCategory: false);
            _pkValue[i] = imp == null ? string.Empty : imp.Steps[_pkStepOf[i]].ValueText;
            int t = TargetOfUuid(_pkUuid[i]);
            _pkTarget[i] = t;
            _pkTag[i] = t < 0 ? string.Empty : _loc.TFormat(KeyPocketTarget, TargetPlainName(t));
        }
        Array.Clear(_pocketHover, 0, _pocketHover.Length);   // rows may have moved under the pointer
        _pocketWindow?.MarkDirty();
    }

    // Quality desc, then the value step desc. "Value" is the step inside the imprint's own ladder, not the raw number:
    // raw values of different imprints aren't comparable (a flat stat vs a percent). Unknown entries last; ties by uuid.
    private static bool Better(Imprint? a, int stepA, int uuidA, Imprint? b, int stepB, int uuidB)
    {
        if ((a == null) != (b == null)) return a != null;
        if (a == null || b == null) return uuidA < uuidB;
        int qa = a.Steps[stepA].Quality, qb = b.Steps[stepB].Quality;
        if (qa != qb) return qa > qb;
        if (stepA != stepB) return stepA > stepB;
        int byName = string.CompareOrdinal(a.Name, b.Name);
        return byName != 0 ? byName < 0 : uuidA < uuidB;
    }

    // The target row holding an ACTIVE copy with this uuid (a copy confirmed in THIS character's pocket), else -1.
    private int TargetOfUuid(int uuid)
    {
        if (uuid == 0) return -1;
        for (int t = 0; t < _targets.Count; t++)
            foreach (var c in _targets[t].Copies)
                if (c.Active && c.Uuid == uuid) return t;
        return -1;
    }

    private string TargetPlainName(int t)
        => _catalog != null && _catalog.ByGroup.TryGetValue(_targets[t].Group, out var imp) ? imp.Name : "#" + _targets[t].Group;

    // ── list ──────────────────────────────────────────────────────────────────────────────────────────────────────
    // 0 while hidden: the Prebuild fill binds nothing.
    private int PocketSlotCount() => _pocketWindow?.IsShown == true ? _pkSlots : 0;

    private int PocketIndex(int slot) => _pocketOffset + slot;

    private bool PocketReal(int slot) => PocketIndex(slot) < _pkCount;

    private HudElement BuildPocketList()
    {
        var pool = new HudElement[PocketPool];
        for (int i = 0; i < PocketPool; i++)
        {
            int slot = i;
            // Two COMPLETE tiles swapped around the bubble; R15: the VirtualList slot stretches its child, so the 2 px gap
            // is a spacer inside the slot.
            pool[i] = new ColumnElement(new HudElement[]
            {
                new ConditionalElement(() => PocketReal(slot), BuildPocketRow(slot), BuildFreeSlot()),
                new SpacerElement(0f, PocketPitch - PocketTileH),
            }, Gap: 0.01f);
        }
        return new VirtualListElement(PocketSlotCount, PocketPitch, pool, first => _pocketOffset = first, Height: PocketListH)
        {
            ResetScroll = () => { if (!_pocketResetScroll) return false; _pocketResetScroll = false; return true; },
        };
    }

    private string PocketText(string[] col, int slot)
    {
        int i = PocketIndex(slot);
        return i >= 0 && i < _pkCount ? col[i] ?? string.Empty : string.Empty;
    }

    private ColorRgba PocketQualityColor(int slot)
    {
        int i = PocketIndex(slot);
        return i < _pkCount && _pkImp[i] is { } imp ? QualityColor(imp.Steps[_pkStepOf[i]].Quality) : MutedColor();
    }

    private bool PocketIsTarget(int slot)
    {
        int i = PocketIndex(slot);
        return i < _pkCount && _pkTarget[i] >= 0;
    }

    private HudElement BuildPocketRow(int slot)
    {
        var qualityBar = new BubbleElement(new SpacerElement(QualityBarW, PocketTileH - 10f))
        {
            FillColorFn = () => Fade(PocketQualityColor(slot)),
            CornerRadius = RoundRadius(QualityBarW), PaddingX = 0f, PaddingY = 0f,
        };
        var name = new CellElement(new TextElement(() => PocketText(_pkName, slot), Color: () => TextColor(), NoWrap: true)
            { Ellipsize = true }, Weight: 1f);
        var value = new TextElement(() => PocketText(_pkValue, slot), Color: () => TextColor(), Width: PocketValueW,
            Align: TextAlign.Right, NoWrap: true) { Ellipsize = true };
        var tag = new CellElement(new ConditionalElement(() => PocketIsTarget(slot),
            Badge(() => PocketText(_pkTag, slot), SuccessColor, TileColor, PocketTagW),
            new SpacerElement(PocketTagW, ChipHeight)), Width: PocketTagW);
        var discard = CrossButton(() => _pocketHover[slot], over => _pocketHover[slot] = over,
            () => OnDiscardClicked(PocketIndex(slot)), TileColor, danger: true, enabled: CanDiscard, dimDisabled: true);

        return new BubbleElement(new RowElement(new HudElement[]
        {
            new SpacerElement(2f, PocketTileH - 2f),   // lead-in + height strut (tile = 24 + 2 × 1 px padding)
            qualityBar, name, value, tag, discard,
            new SpacerElement(1f, 1f),
        }, Gap: 6f))
        {
            FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = 2f, PaddingY = 1f,
        };
    }

    // A free unlocked slot: no fill, the faint 1 px rim + a muted "free slot".
    private HudElement BuildFreeSlot() => new BubbleElement(new RowElement(new HudElement[]
    {
        new SpacerElement(8f, PocketTileH - 2f),
        new CellElement(SmallText(() => _loc.T(KeyPocketFree), MutedColor), Weight: 1f),
    }, Gap: 6f))
    {
        FillColorFn = () => Transparent,
        OutlineColorFn = () => Fade(MutedColor() with { A = 0.22f }), OutlineWidth = 1f,
        CornerRadius = TileRadius, PaddingX = 0f, PaddingY = 1f,
    };
}
