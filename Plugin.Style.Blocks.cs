using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

// More house-kit building blocks (Stellar Design Language §4; ported from LoadoutSwitcher / AutoFishing
// Plugin.Style.Blocks.cs): embedded art + the per-size header glyph, the accent icon tile, the 28 px pill button, the
// 24 px chips (On/Off, choice, non-clickable badge) and the icon-only ✕.
public sealed partial class Plugin
{
    // ── embedded PNGs ─────────────────────────────────────────────────────────────────────────────────────────────
    // Read once per resource (csproj LogicalName "Stellar.AutoManifest.<file>") and kept: ImageElement Funcs are polled.
    private static readonly Dictionary<string, byte[]?> _pngCache = new();

    private static byte[]? CachedPng(string file)
    {
        if (_pngCache.TryGetValue(file, out var png)) return png;
        try
        {
            using var s = typeof(Plugin).Assembly.GetManifestResourceStream("Stellar.AutoManifest." + file);
            if (s != null) { using var ms = new System.IO.MemoryStream(); s.CopyTo(ms); png = ms.ToArray(); }
        }
        catch { png = null; }
        _pngCache[file] = png;
        return png;
    }

    // One pixel-hinted bitmap per size (house icon rule: never a master scaled down) — keep in sync with Resources/icons.
    private static readonly int[] HeaderIconSizes = { 12, 16, 18, 24, 32, 40, 64, 128 };

    // `units` canvas units × the live canvas→screen scale. Same rule as the framework's IconSizePicker: an exact size
    // (±0.05 px) draws 1:1, else the smallest LARGER drawing, else the largest. Cached until the scale changes, so the
    // per-refresh Func hands back the SAME array (ImageElement.Live compares by reference).
    private float  _iconScale = -1f;
    private byte[]? _iconPicked;

    private byte[]? HeaderIconPng(int units)
    {
        var fw = _services.Framework;
        float scale = fw.CanvasWidth > 1 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        if (scale == _iconScale && _iconPicked != null) return _iconPicked;
        _iconScale = scale;
        return _iconPicked = CachedPng("automanifest-" + PickIconSize(units * scale) + ".png") ?? _iconPicked;
    }

    private static int PickIconSize(float want)
    {
        for (int i = 0; i < HeaderIconSizes.Length; i++)
            if (HeaderIconSizes[i] >= want - 0.05f) return HeaderIconSizes[i];
        return HeaderIconSizes[HeaderIconSizes.Length - 1];
    }

    // Launcher tile: ONE fixed image the launcher scales into its 32-unit slot → pick the bitmap for 32 units × the UI
    // scale at registration (KB Stellar-Design-Language "Launcher tile icon").
    private byte[]? LauncherIconPng()
    {
        var fw = _services.Framework;
        float scale = fw.CanvasWidth > 1 && fw.ScreenWidth > 0 ? (float)fw.ScreenWidth / fw.CanvasWidth : 1f;
        return CachedPng("automanifest-" + PickIconSize(32f * scale) + ".png") ?? CachedPng("automanifest-128.png");
    }

    // ── accent icon tile ──────────────────────────────────────────────────────────────────────────────────────────
    // A rounded tile in the LIVE theme accent with the flat white glyph centred (art can't follow the theme, so the colour
    // lives in the tile). 40 px tile → 24 px glyph; radius ≈ 22 % of the side.
    private const float ToolIconSize  = 40f;
    private const int   ToolGlyphSize = 24;

    private HudElement IconBox(Func<byte[]?> png, float size, int glyph)
    {
        float pad = (size - glyph) / 2f;
        return new CellElement(new BubbleElement(
            new RowElement(new HudElement[] { new ImageElement(png, glyph, glyph) { Live = true } }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(AccentColor()), CornerRadius = MathF.Round(size * 0.22f), PaddingX = pad, PaddingY = pad,
        }, Width: size);
    }

    // ── 28 px pill button ─────────────────────────────────────────────────────────────────────────────────────────
    // Round Bubble (fill + hover) → Bare Selectable → Row[28 px strut, bold 13 label in a fixed box]. MaxWidth pins the
    // whole pill so a label change never moves a neighbour (rule 10). `enabled` false → RECESSED (rule 22), click
    // swallowed, same size. `surface` = what the pill sits on (for the recessed label contrast).
    private const float PillH = 28f;

    private enum PillKind : byte { Primary, Neutral, Danger }

    private HudElement PillButton(Func<string> label, PillKind kind, Action onClick, float width, Func<ColorRgba> surface,
        Func<bool>? enabled = null)
    {
        var hover = new HoverFlag();
        var solid = new SolidLabelCache();
        var tinted = new ContrastCache();
        var disabled = new DisabledLabelCache();
        bool En() => enabled == null || enabled();
        ColorRgba Fill() => kind switch
        {
            PillKind.Primary => PrimaryFill(),
            PillKind.Danger  => DangerFill(),
            _                => NeutralFill(),
        };
        ColorRgba Label()
        {
            if (!En()) return DisabledLabel(surface(), disabled);
            return kind == PillKind.Neutral ? tinted.Get(TextColor(), NeutralFill(), PillMinContrast) : solid.Get(Fill());
        }
        const float padX = 8f;
        return new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(padX, PillH),
                new TextElement(label, Color: () => Label(), Width: width - 2f * padX, Align: TextAlign.Center, NoWrap: true,
                    FontSize: 13) { Bold = true, Ellipsize = true, CenterCaps = true },
                new SpacerElement(padX, PillH),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => !En() ? DisabledShade : Fade(hover.On ? HoverFill(Fill()) : Fill()),
            CornerRadius = RoundRadius(PillH), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };
    }

    // ── 24 px chips ───────────────────────────────────────────────────────────────────────────────────────────────
    // Cell → round Bubble (fill + hover) → Bare Selectable → Row[24 px strut, label]. COMPLETE chips swapped by a
    // ConditionalElement wrapping the whole chip, all the SAME width (MaxWidth pins it: a MaxWidth-0 bubble under a
    // Conditional caps itself at 100 px — MinimalNameplate, KB "Clickable on/off chip").
    private const float ChipHeight = 24f;
    private const float OnOffChipW = 64f;

    private HudElement ChipShell(HudElement label, Func<ColorRgba> fill, HoverFlag hover, Action onClick, float width)
        => new BubbleElement(new SelectableElement(
            new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),   // height strut = the side (house rule 4)
                label,
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: onClick) { Bare = true, OnHover = over => hover.On = over })
        {
            FillColorFn = () => fill(),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        };

    private TextElement ChipLabel(Func<string> text, Func<ColorRgba> color, float width, bool bold, int size = 12)
        => new(text, Color: () => color(), Width: width - 8f, Align: TextAlign.Center, NoWrap: true, FontSize: size)
            { Bold = bold, RichEmoji = !bold, Ellipsize = true, CenterCaps = bold };

    // On/Off: On = bold status green on a 22 % green wash; Off = muted on a faint text wash; `recessed` = sunken,
    // NON-clickable, showing "Off" (the saved setting underneath is not touched).
    private HudElement OnOffChip(Func<bool> get, Action<bool> set, Func<bool> recessed)
    {
        var hover = new HoverFlag();
        var onText = new ContrastCache();
        var offLabel = new DisabledLabelCache();
        void Click() => set(!get());
        ColorRgba OnFill()  => Wash(SuccessColor(), PanelColor());
        ColorRgba OffFill() => Over(TextColor() with { A = OffChipTextMix }, PanelColor());
        ColorRgba Hovered(ColorRgba c) => Fade(hover.On ? HoverFill(c) : c);
        var on = ChipShell(ChipLabel(() => _loc.T(KeyOn), () => onText.Get(SuccessColor(), OnFill(), SolidMinContrast), OnOffChipW, true, 13),
            () => Hovered(OnFill()), hover, Click, OnOffChipW);
        var off = ChipShell(ChipLabel(() => _loc.T(KeyOff), MutedColor, OnOffChipW, true, 13),
            () => Hovered(OffFill()), hover, Click, OnOffChipW);
        var dis = ChipShell(ChipLabel(() => _loc.T(KeyOff), () => DisabledLabel(PanelColor(), offLabel), OnOffChipW, true, 13),
            () => DisabledShade, new HoverFlag(), () => { }, OnOffChipW);
        return new CellElement(new ConditionalElement(recessed, dis, new ConditionalElement(get, on, off)), Width: OnOffChipW);
    }

    // Choice chip (one of a set, e.g. "1 pull" / "10-pull", the picker categories): selected = bold accent-tinted;
    // unselected = muted on a faint wash; `locked` = recessed and inert (e.g. while a run is going).
    private HudElement ChoiceChip(Func<string> text, Func<bool> selected, Action onClick, float width, Func<ColorRgba> surface,
        Func<bool>? locked = null)
    {
        var hover = new HoverFlag();
        var selInk = new ContrastCache();
        var disabled = new DisabledLabelCache();
        bool Locked() => locked != null && locked();
        ColorRgba SelFill() => Over(AccentColor() with { A = 0.30f }, surface());
        ColorRgba OffFill() => Over(TextColor() with { A = OffChipTextMix }, surface());
        ColorRgba Hovered(ColorRgba c) => Fade(hover.On ? HoverFill(c) : c);
        var sel = ChipShell(ChipLabel(text, () => selInk.Get(TextColor(), SelFill(), PillMinContrast), width, true),
            () => Hovered(SelFill()), hover, onClick, width);
        var off = ChipShell(ChipLabel(text, MutedColor, width, true), () => Hovered(OffFill()), hover, onClick, width);
        var dis = ChipShell(ChipLabel(text, () => DisabledLabel(surface(), disabled), width, true),
            () => DisabledShade, new HoverFlag(), () => { }, width);
        return new CellElement(new ConditionalElement(Locked, dis, new ConditionalElement(selected, sel, off)), Width: width);
    }

    // Non-clickable status badge (target "waiting" / "stored", the end-line Done / Stopped / Error): a colour wash with a
    // bold label in that colour, fixed width; `color` null-equivalent muted look when `muted` is true.
    private HudElement Badge(Func<string> text, Func<ColorRgba> color, Func<ColorRgba> surface, float width, bool muted = false)
    {
        var ink = new ContrastCache();
        ColorRgba Fill() => muted ? Over(TextColor() with { A = OffChipTextMix }, surface()) : Wash(color(), surface());
        return new CellElement(new BubbleElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),
                muted ? ChipLabel(text, MutedColor, width, false)
                      : ChipLabel(text, () => ink.Get(color(), Fill(), PillMinContrast), width, true),
            }, Gap: 0.01f, Justify: RowJustify.Center))
        {
            FillColorFn = () => Fade(Fill()),
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = width,
        }, Width: width);
    }

    // ── icon-only ✕ ───────────────────────────────────────────────────────────────────────────────────────────────
    // A round 24 px icon button: a muted PLAIN "✕" at rest (a bold ✕ can draw blank — KB §6). `danger` = the house
    // "Remove hover" (soft red wash + red glyph); otherwise a soft text wash (close). `enabled` false = no hover, no click;
    // `dimDisabled` also draws it recessed then (sunken circle + faint glyph — the pocket ✕ during a run, spec ⑨).
    private static readonly ColorRgba RemoveGlyph = new(1f, 0x8a / 255f, 0x8b / 255f);   // #ff8a8b

    private HudElement CrossButton(Func<bool> hover, Action<bool> setHover, Action onClick, Func<ColorRgba> surface,
        bool danger, Func<bool>? enabled = null, bool dimDisabled = false)
    {
        var glyph = new ContrastCache();
        var dim = new DisabledLabelCache();
        bool En() => enabled == null || enabled();
        bool Hot() => hover() && En();
        ColorRgba HotFill() => danger ? Over(DangerColor() with { A = 0.22f }, surface()) : Over(TextColor() with { A = HoverMix }, surface());
        ColorRgba Ink() => dimDisabled && !En() ? DisabledLabel(surface(), dim)
                         : !Hot() ? MutedColor() : danger ? glyph.Get(RemoveGlyph, HotFill(), SolidMinContrast) : TextColor();
        return new BubbleElement(new SelectableElement(new RowElement(new HudElement[]
            {
                new SpacerElement(0f, ChipHeight),
                new TextElement(() => "✕", Color: () => Ink(), Width: ChipHeight, Align: TextAlign.Center, NoWrap: true),
            }, Gap: 0.01f, Justify: RowJustify.Center),
            OnClick: () => { if (En()) onClick(); })
            { Bare = true, OnHover = setHover })
        {
            // dimDisabled + disabled = RECESSED (house rule 22): the sunken circle, drawn translucent like the pills'.
            FillColorFn = () => dimDisabled && !En() ? DisabledShade : Hot() ? Fade(HotFill()) : Transparent,
            CornerRadius = RoundRadius(ChipHeight), PaddingX = 0f, PaddingY = 0f, MaxWidth = ChipHeight,
        };
    }
}
