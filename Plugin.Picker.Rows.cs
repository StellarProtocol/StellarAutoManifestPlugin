using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The picker's imprint list (a <see cref="VirtualListElement"/> over the filtered imprints: 26 px tiles + 2 px gap =
/// 28 pitch, 6 rows visible) and its MINIMUM VALUE step chips (one per rollable step, coloured by quality, the selected
/// one ringed in the accent). The list row reads "Name · category" with the category muted (rich-text colour span,
/// rebuilt per slot only when the imprint, the language or the muted colour changes) and the full range right-aligned.
/// </summary>
public sealed partial class Plugin
{
    private const int   PickerPool     = 9;
    private const float PickerTileH    = 26f;
    private const float PickerPitch    = 28f;
    // 6 rows again (spec ⑧): cut to 4 when the odds panel (spec ⑥) arrived, restored once the user allowed more height.
    private const float PickerListH    = 6 * PickerPitch - 2f;   // 166
    private const float PickerRangeW   = 120f;
    private const int   MaxStepChips   = 11;
    private const float StepChipW      = 48f;                    // 11 × 48 + 10 × 2 = 548 (content 556)

    private int _pickOffset;
    private readonly bool[] _pickRowHover = new bool[PickerPool];
    private readonly bool[] _stepHover = new bool[MaxStepChips];

    // 0 while hidden: the Prebuild fill binds nothing.
    private int PickerCount() => _pickerWindow?.IsShown == true ? _pickFiltered.Count : 0;

    private Imprint? PickAt(int slot)
    {
        int i = _pickOffset + slot;
        return i >= 0 && i < _pickFiltered.Count ? _pickFiltered[i] : null;
    }

    private HudElement BuildPickerList()
    {
        var pool = new HudElement[PickerPool];
        for (int i = 0; i < PickerPool; i++) pool[i] = BuildPickerRow(i);
        return new ConditionalElement(() => _pickFiltered.Count > 0,
            new VirtualListElement(PickerCount, PickerPitch, pool, first => _pickOffset = first, Height: PickerListH)
            {
                ResetScroll = () => { if (!_pickResetScroll) return false; _pickResetScroll = false; return true; },
            },
            FixedLine(PickerListH, new TextElement(() => _loc.T(_catalogReady ? KeyPickerEmpty : KeyPickerLoading),
                Color: () => MutedColor(), Align: TextAlign.Center, NoWrap: true) { Ellipsize = true }));
    }

    // Per-slot composed line: "Name <color=#muted>· Category</color>".
    private readonly Imprint?[] _lineImp = new Imprint?[PickerPool];
    private readonly int[] _lineGen = new int[PickerPool];
    private readonly ColorRgba[] _lineMuted = new ColorRgba[PickerPool];
    private readonly string[] _lineText = new string[PickerPool];

    private string PickerLine(int slot)
    {
        var imp = PickAt(slot);
        if (imp == null) return string.Empty;
        var muted = MutedColor();
        if (ReferenceEquals(imp, _lineImp[slot]) && _lineGen[slot] == _langGen && muted.Equals(_lineMuted[slot]) && _lineText[slot] != null)
            return _lineText[slot];
        _lineImp[slot] = imp; _lineGen[slot] = _langGen; _lineMuted[slot] = muted;
        return _lineText[slot] = ImprintLine(imp, muted, withCategory: true);
    }

    // "Name <color=#muted>· kind</color>". kind = a class imprint's effect wording ("Lucky Strike · Oath DMG", parsed
    // in Lua M.names), else (withCategory) the category label. The picker AND the main-window target rows use it.
    private string ImprintLine(Imprint imp, ColorRgba muted, bool withCategory)
    {
        var cat = imp.Kind.Length > 0 ? imp.Kind : withCategory ? CategoryNameForLib(imp.AttrLibId) : string.Empty;
        return cat.Length == 0 ? imp.Name : imp.Name + " <color=#" + HexOf(muted) + ">· " + cat + "</color>";
    }

    private static string HexOf(ColorRgba c)
        => ((int)(Math.Clamp(c.R, 0f, 1f) * 255f + 0.5f)).ToString("X2") + ((int)(Math.Clamp(c.G, 0f, 1f) * 255f + 0.5f)).ToString("X2")
         + ((int)(Math.Clamp(c.B, 0f, 1f) * 255f + 0.5f)).ToString("X2");

    private HudElement BuildPickerRow(int slot)
    {
        bool Selected() => PickAt(slot) is { } imp && ReferenceEquals(imp, _pickSelected);
        ColorRgba Fill() => _pickRowHover[slot] ? RowHoverColor(TileColor()) : TileColor();
        var tile = new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, PickerTileH),
                new CellElement(new TextElement(() => PickerLine(slot), Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
                new TextElement(() => PickAt(slot)?.RangeText ?? string.Empty, Color: () => MutedColor(), Width: PickerRangeW,
                    Align: TextAlign.Right, NoWrap: true, FontSize: 12) { RichEmoji = true, Ellipsize = true },
            }, Gap: 8f),
            OnClick: () => { if (PickAt(slot) is { } imp) SelectImprint(imp); })
            { Bare = true, OnHover = over => _pickRowHover[slot] = over })
        {
            FillColorFn = () => Fade(Fill()),
            OutlineColorFn = () => Selected() ? Fade(AccentColor()) : Transparent, OutlineWidth = 2f,
            CornerRadius = TileRadius, PaddingX = 8f, PaddingY = 0f,
        };
        // R15: the VirtualList slot stretches its child, so the 2 px gap is a spacer inside the slot.
        return new ColumnElement(new HudElement[] { tile, new SpacerElement(0f, PickerPitch - PickerTileH) }, Gap: 0.01f);
    }

    // ── MINIMUM VALUE chips ───────────────────────────────────────────────────────────────────────────────────────
    private ImprintStep? StepAt(int k)
        => _pickSelected is { } imp && k < imp.Steps.Length ? imp.Steps[k] : null;

    private HudElement BuildStepChips()
    {
        var chips = new HudElement[MaxStepChips];
        for (int i = 0; i < MaxStepChips; i++)
        {
            int k = i;
            var ink = new ContrastCache();
            ColorRgba Col() => QualityColor(StepAt(k)?.Quality ?? 2);
            ColorRgba Fill() => _stepHover[k] ? HoverFill(Wash(Col(), WindowBg())) : Wash(Col(), WindowBg());
            var chip = new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
                {
                    new SpacerElement(0f, ChipHeight),
                    new TextElement(() => StepAt(k)?.ValueText ?? string.Empty,
                        Color: () => FadeIfUnreachable(k, ink.Get(Col(), Wash(Col(), WindowBg()), PillMinContrast)),   // can't roll now → faded
                        Width: StepChipW - 4f, Align: TextAlign.Center, NoWrap: true, FontSize: 11) { Bold = true, Ellipsize = true, CenterCaps = true },
                }, Gap: 0.01f, Justify: RowJustify.Center),
                OnClick: () => { if (StepAt(k) != null) { _pickStep = k; _pickerWindow.MarkDirty(); } })
                { Bare = true, OnHover = over => _stepHover[k] = over })
            {
                FillColorFn = () => Fade(Fill()),
                OutlineColorFn = () => _pickStep == k ? Fade(AccentColor()) : Transparent, OutlineWidth = 2f,
                CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = StepChipW,
            };
            // A step the selected imprint doesn't have (or nothing selected) keeps its slot as an empty strut.
            chips[i] = new CellElement(new ConditionalElement(() => StepAt(k) != null, chip, new SpacerElement(StepChipW, ChipHeight)),
                Width: StepChipW);
        }
        return new RowElement(chips, Gap: 2f);
    }
}
