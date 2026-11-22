using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

// House style (Stellar Design Language, Knowledge Base\Stellar-Design-Language.md) — the palette, colour maths and the
// panel kit the Auto Manifestation window and its Add-target picker build from. Ported from StellarLoadoutSwitcherPlugin
// Plugin.Style.cs (Maestro / AutoFishing kit + the uniform window-opacity fade + the rule-22 recessed disabled look),
// trimmed to what this plugin uses. Needs SDK >= 2.33.0 (theme status colours 2.30, TextElement.CenterCaps 2.33).
//
// Toolkit limits behind some choices (WindowBuilder-Patterns.md):
//   • Plain TextElement ignores FontSize on Menu windows (fixed 14px) — only the styled path (Bold/Emphasis) or
//     RichEmoji (the regular TMP face) honours it, so the 12 px hints / status lines are RichEmoji text.
//   • Row/Column Gap 0 means the DEFAULT gap (8/12), never zero — every container here passes an explicit Gap.
//   • A Bubble hugs its child's height, so every round control pins its height with a 0-width SpacerElement strut.
//   • Bold can't be toggled live → a bold/regular variant is two complete elements swapped by a ConditionalElement.
public sealed partial class Plugin
{
    // ── palette: LIVE from the current framework theme ────────────────────────────────────────────────────────────
    private static readonly ColorRgba PanelShade = new(0f, 0f, 0f, 0.20f);   // panel = background darkened
    private const float TileTextMix      = 0.08f;   // raised tile = panel + 8% MenuText …
    private const float TileAccentMix    = 0.06f;   // … + a 6% accent tint (teal on teal, pink on pink)
    private const float NeutralMix       = 0.22f;   // neutral fill = panel + 22% MenuMuted …
    private const float NeutralAccentMix = 0.08f;   // … + an 8% accent tint, like the tiles
    private const float RowHoverMix      = 0.05f;   // hovered row = tile + 5% MenuText
    private const float PillMinContrast  = 4.5f;    // tinted fills (Neutral, chips) and body text keep 4.5:1
    private const float SolidMinContrast = 3.0f;    // white on a SOLID fill (Primary, Danger) keeps white down to 3:1
    private const float ChipWashAlpha    = 0.22f;   // coloured chip = a 22% wash of its colour
    private const float DangerFillAlpha  = 0.85f;   // the house Danger pill: the status red at 85%
    private const float OffChipTextMix   = 0.06f;   // Off chip = a faint MenuText wash over the panel
    private const float HoverMix         = 0.10f;   // hover = 10% MenuText over the fill
    private static readonly ColorRgba SolidDarkLabel = new(0.12f, 0.12f, 0.12f);   // #1F1F1F: label when white < 3:1
    private static readonly ColorRgba White       = new(1f, 1f, 1f);
    private static readonly ColorRgba Transparent = new(0f, 0f, 0f, 0f);   // NOT null: a null FillColorFn = the default accent blob

    private IThemeColors Menu => _services.Theme.Colors;

    private ColorRgba TextColor()    => Menu.MenuText;
    private ColorRgba MutedColor()   => Menu.MenuMuted;
    private ColorRgba AccentColor()  => Menu.MenuAccent;
    private ColorRgba SuccessColor() => Menu.Success;
    private ColorRgba CautionColor() => Menu.Caution;
    private ColorRgba DangerColor()  => Menu.Danger;
    private ColorRgba WindowBg()     => Menu.MenuBackground;
    private ColorRgba PanelColor()   => Over(PanelShade, Menu.MenuBackground);
    private ColorRgba TileColor()    => Over(Menu.MenuAccent with { A = TileAccentMix },
                                             Over(Menu.MenuText with { A = TileTextMix }, PanelColor()));
    private ColorRgba NeutralFill()  => Over(Menu.MenuAccent with { A = NeutralAccentMix },
                                             Over(Menu.MenuMuted with { A = NeutralMix }, PanelColor()));
    private ColorRgba PrimaryFill()  => Menu.MenuAccent;
    private ColorRgba DangerFill()   => Over(Menu.Danger with { A = DangerFillAlpha }, WindowBg());
    private ColorRgba HoverFill(ColorRgba fill) => Over(Menu.MenuText with { A = HoverMix }, fill);
    private ColorRgba RowHoverColor(ColorRgba tile) => Over(Menu.MenuText with { A = RowHoverMix }, tile);

    // A chip wash of a semantic colour over a surface (status badges, quality step chips).
    private static ColorRgba Wash(ColorRgba color, ColorRgba surface) => Over(color with { A = ChipWashAlpha }, surface);

    // Solid-fill label rule (KB Stellar-Design-Language, fw 8df7f29): WHITE when white reaches 3:1 on the fill, otherwise
    // near-black — never a "darkened white" mid grey, which bold thin text can't carry.
    private static ColorRgba SolidLabel(ColorRgba fill)
        => ContrastRatio(1f, Luminance(fill)) >= SolidMinContrast ? White : SolidDarkLabel;

    // SolidLabel per call site, recomputed only when the fill changes (a theme switch), not on every poll.
    private sealed class SolidLabelCache
    {
        private ColorRgba _fill, _out;
        private bool _valid;

        public ColorRgba Get(ColorRgba fill)
        {
            if (_valid && fill.Equals(_fill)) return _out;
            _fill = fill; _out = SolidLabel(fill); _valid = true;
            return _out;
        }
    }

    // ── window opacity: EVERY coloured fill follows the window opacity, uniformly ──────────────────────────────────
    // The framework fades ONLY the GlassMenu frame Image (Themes → Window Opacity); every bubble fill here is an opaque
    // pre-blended colour, so each goes through Fade with the frame's factor; only text and pictures stay solid (house rule
    // 2026-11-07). Composed OPAQUE first, alpha last. A cast + property read per poll, no allocation.
    private float WindowOpacity()
    {
        float? v;
        try { v = (_services.NamedTheme as IChromeStyle)?.WindowOpacity; } catch { v = null; }
        return v is { } f ? Math.Clamp(f, 0.3f, 1f) : 1f;
    }

    private ColorRgba Fade(ColorRgba c) => c with { A = c.A * WindowOpacity() };

    // ── recessed disabled look (house rule 22) ────────────────────────────────────────────────────────────────────
    // A disabled pill / chip is RECESSED: a sunken fill darker than its surface (black @28 %), a faint label, no ring,
    // same size and radius. The fill is drawn translucent (it fades with the window by itself); the pre-blended
    // Over(shade, surface) is used ONLY for the label contrast.
    private static readonly ColorRgba DisabledShade = new(0f, 0f, 0f, 0.28f);
    // A recessed WELL (the picker's imprint list, spec ⑩): the same sunken black @28 %, drawn translucent over the window,
    // so it reads one step darker than the odds panel (black @20 %) and in the family of the dark search box.
    private static readonly ColorRgba WellShade = DisabledShade;
    private const float DisabledLabelAlpha = 0.32f;
    private const float DisabledLabelMinContrast = 2.0f;

    private sealed class DisabledLabelCache { public ColorRgba Text, Surface, Label; public bool Valid; }

    private ColorRgba DisabledLabel(ColorRgba surface, DisabledLabelCache cache)
    {
        var text = Menu.MenuText;
        if (cache.Valid && text.Equals(cache.Text) && surface.Equals(cache.Surface)) return cache.Label;
        var bg = Over(DisabledShade, surface);
        float lb = Luminance(bg), a = DisabledLabelAlpha;
        while (a < 1f && ContrastRatio(Luminance(Over(text with { A = a }, bg)), lb) < DisabledLabelMinContrast)
            a = MathF.Min(1f, a + 0.04f);
        cache.Text = text; cache.Surface = surface; cache.Label = text with { A = a }; cache.Valid = true;
        return cache.Label;
    }

    // ── contrast maths (ChatTools → MultiTool / Maestro / AutoFishing / LoadoutSwitcher Plugin.Style.cs) ──────────
    private sealed class ContrastCache
    {
        private ColorRgba _fg, _bg, _out;
        private bool _valid;

        public ColorRgba Get(ColorRgba fg, ColorRgba bg, float ratio)
        {
            if (_valid && fg.Equals(_fg) && bg.Equals(_bg)) return _out;
            _fg = fg; _bg = bg; _out = EnsureContrast(fg, bg, ratio); _valid = true;
            return _out;
        }
    }

    // Nudges fg toward white (dark bg) or black (light bg) in 15% steps until it reaches minRatio against the opaque bg.
    private static ColorRgba EnsureContrast(ColorRgba fg, ColorRgba bg, float minRatio)
    {
        float lb = Luminance(bg);
        var target = lb < 0.179f ? White : new ColorRgba(0f, 0f, 0f);
        for (int i = 0; i < 12 && ContrastRatio(Luminance(fg), lb) < minRatio; i++) fg = Mix(fg, target, 0.15f);
        return fg with { A = 1f };
    }

    private static float ContrastRatio(float la, float lb) => (MathF.Max(la, lb) + 0.05f) / (MathF.Min(la, lb) + 0.05f);
    private static float Luminance(ColorRgba c) => 0.2126f * Lin(c.R) + 0.7152f * Lin(c.G) + 0.0722f * Lin(c.B);
    private static float Lin(float v) => v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);

    private static ColorRgba Mix(ColorRgba a, ColorRgba b, float t)
        => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1f);

    // Alpha-composite fg (its A = coverage) over an opaque bg.
    private static ColorRgba Over(ColorRgba fg, ColorRgba bg) => Mix(bg, fg with { A = 1f }, fg.A);

    // ── shape ─────────────────────────────────────────────────────────────────────────────────────────────────────
    // Round (capsule / circle) radius for a bubble of side `side`: side / 2 − 1, NOT side / 2 (the 9-slice min size is
    // 2r + 2 — house rule 1).
    private static float RoundRadius(float side) => side / 2f - 1f;
    private const float TileRadius = 6f;   // panels, tiles

    private sealed class HoverFlag { public bool On; }

    // ── section panel ─────────────────────────────────────────────────────────────────────────────────────────────
    // A rounded panel (background darkened, live): bold 12 UPPERCASE caption + a right-aligned 12 px hint, then the
    // children. Directly in a Column it FILLS the width. ⚠️ ONE method with an explicit HudElement[] — deliberately NOT a
    // `params` overload pair: with two params overloads C# binds the first child as a trailing control (KB §4).
    private HudElement SectionPanel(string captionKey, Func<string> hint, HudElement[] children)
    {
        var items = new HudElement[children.Length + 1];
        items[0] = new RowElement(new HudElement[]
        {
            new TextElement(UpperLoc(captionKey), Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new CellElement(SectionHint(hint, MutedColor), Weight: 1f),
        }, Gap: 8f);
        Array.Copy(children, 0, items, 1, children.Length);
        return new BubbleElement(new ColumnElement(items, Gap: 6f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 8f,
        };
    }

    // A right-aligned 12 px line (RichEmoji = the regular TMP face, which honours FontSize) in a live colour.
    private static HudElement SectionHint(Func<string> text, Func<ColorRgba> color)
        => new TextElement(text, Color: () => color(), Align: TextAlign.Right, NoWrap: true, FontSize: 12)
            { RichEmoji = true, Ellipsize = true };

    // A 12 px regular line (RichEmoji), left aligned, ellipsized.
    private static HudElement SmallText(Func<string> text, Func<ColorRgba> color)
        => new TextElement(text, Color: () => color(), NoWrap: true, FontSize: 12) { RichEmoji = true, Ellipsize = true };

    // ONE always-present line at a fixed height: a 0-width strut pins the row so bold / empty text never changes the
    // window height; the content cell takes the rest of the width.
    private static HudElement FixedLine(float height, HudElement content) => new RowElement(new HudElement[]
    {
        new SpacerElement(0f, height),
        new CellElement(content, Weight: 1f),
    }, Gap: 0.01f);
}
