using System;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The picker's reachability parts (approved spec ⑥, option B), fed by the cached pass in <c>Plugin.Reach.cs</c>:
/// <list type="bullet">
/// <item>MINIMUM VALUE chips whose quality can't roll now draw FADED text on their normal fill — still pickable, never
/// recessed (recessed = disabled, house rule 22).</item>
/// <item>A selected value that can't roll now swaps the legend line for an amber hint: "Epic can't roll at Lv 2. From
/// Lv 4 with Blessing (about 92 more Blessing pulls), or Lv 10 without."</item>
/// <item>The odds panel "QUALITY CHANCE PER ROW · Lv 2 · Blessing on": the current row's four quality weights as %.</item>
/// </list>
/// Every text is composed only when the reach result, the EXP (pull estimate) or the language moves.
/// </summary>
public sealed partial class Plugin
{
    private const float FadedChipAlpha = 0.38f;   // a chip whose quality can't roll now: same fill, faded value
    private const float PickHintLineH  = 30f;     // two 12 px lines — the hint wraps; the legend sits in the same slot
    private const float OddsLineH      = 16f;

    private bool StepFaded(int k) => StepAt(k) is { } s && !RollNow(s.Quality);

    private ColorRgba FadeIfUnreachable(int k, ColorRgba ink) => StepFaded(k) ? ink with { A = FadedChipAlpha } : ink;

    // ── hint line (or the legend) ─────────────────────────────────────────────────────────────────────────────────
    private readonly ContrastCache _pickHintInk = new();

    private bool PickHintShown() => _levels != null && _st.Valid && StepAt(_pickStep) is { } s && !RollNow(s.Quality);

    private HudElement BuildPickHintOrLegend() => FixedLine(PickHintLineH, new ConditionalElement(PickHintShown,
        // Wraps (no NoWrap / Width): the full sentence is ~100 characters, two lines at 12 px in the 556 px picker.
        new TextElement(PickHintText, Color: () => _pickHintInk.Get(CautionColor(), WindowBg(), PillMinContrast), FontSize: 12)
            { RichEmoji = true },
        BuildLegend()));

    private (int q, int ver, int exp, int gen) _pickHintKey = (-1, -1, -1, -1);
    private string _pickHintText = string.Empty;

    private string PickHintText()
    {
        if (_levels == null || StepAt(_pickStep) is not { } step) return string.Empty;
        var key = (step.Quality, _reachVer, _st.Exp, _langGen);
        if (key == _pickHintKey) return _pickHintText;
        _pickHintKey = key;
        return _pickHintText = ComposePickHint(step.Quality);
    }

    // Two paths, each "the first level ≥ now where some quality ≥ q has weight": with Blessing (PrimeWeight — only when
    // the game lets Blessing be used now) and without (NormalWeight). A path through a locked level is dropped.
    private string ComposePickHint(int q)
    {
        var t = _levels!;
        int lv = _st.Level;
        var qn = _loc.T(QualityKeys[Math.Clamp(q - 2, 0, QualityKeys.Length - 1)]);
        int lp = _st.BlessAllowed ? t.FirstLevel(lv, q, true) : -1;
        if (lp > 0 && !PathOpen(lv, lp)) lp = -1;
        int ln = t.FirstLevel(lv, q, false);
        if (ln > 0 && !PathOpen(lv, ln)) ln = -1;

        if (!ReachBless && lp == lv) return _loc.TFormat(KeyHintNowBless, qn, lv);   // Blessing off, Prime has it now
        if (lp > lv)
        {
            long exp = t.ExpTo(lv, _st.Exp, lp);
            long pulls = exp > 0 ? (exp + t.ExpPerBlessPull - 1) / t.ExpPerBlessPull : 0;
            return ln > 0 ? _loc.TFormat(KeyHintBoth, qn, lv, lp, N0(pulls), ln) : _loc.TFormat(KeyHintBless, qn, lv, lp, N0(pulls));
        }
        return ln > 0 ? _loc.TFormat(KeyHintNormal, qn, lv, ln) : _loc.TFormat(KeyHintLocked, qn, lv);
    }

    // ── odds panel ────────────────────────────────────────────────────────────────────────────────────────────────
    // Panel 6 + 16 + 4 + 16 + 6 = 48 px; the list gave up two rows (56 px) to make room (Plugin.Picker.Rows.cs).
    private HudElement BuildOddsPanel()
    {
        var cells = new HudElement[4];
        for (int i = 0; i < cells.Length; i++)
        {
            int k = i;
            cells[i] = new CellElement(new TextElement(() => OddsCell(k), Color: () => OddsColor(k), NoWrap: true, FontSize: 12)
                { Bold = true, Ellipsize = true }, Weight: 1f);
        }
        return new BubbleElement(new ColumnElement(new HudElement[]
        {
            FixedLine(OddsLineH, new TextElement(OddsTitle, Color: () => TextColor(), NoWrap: true, FontSize: 12)
                { Bold = true, Ellipsize = true }),
            FixedLine(OddsLineH, new RowElement(cells, Gap: 6f)),
        }, Gap: 4f))
        {
            FillColorFn = () => Fade(PanelColor()), CornerRadius = TileRadius, PaddingX = 10f, PaddingY = 6f,
        };
    }

    // Weight per mille of quality index k (0..3 = Superior..Immortal) at the current level; -1 = not known yet.
    private int OddsWeight(int k) => _st.Valid && _levels?.Row(_st.Level) is { } row ? row.Weights(ReachBless)[k] : -1;

    private ColorRgba OddsColor(int k) => OddsWeight(k) > 0 ? QualityColors[k] : MutedColor();

    // "QUALITY CHANCE PER ROW · LV 2 · BLESSING ON" (captions are upper case, house style).
    private int _oddsTitleVer = -1, _oddsTitleGen = -1;
    private string _oddsTitle = string.Empty;

    private string OddsTitle()
    {
        if (_oddsTitleVer == _reachVer && _oddsTitleGen == _langGen) return _oddsTitle;
        _oddsTitleVer = _reachVer; _oddsTitleGen = _langGen;
        var lv = _st.Valid ? _st.Level.ToString(CultureInfo.InvariantCulture) : "—";
        return _oddsTitle = _loc.TFormat(ReachBless ? KeyOddsOn : KeyOddsOff, lv).ToUpperInvariant();
    }

    // "Epic 2.5%": the game's own maths — weight × 100 / the row's total (entry_popup_view.lua:238-251).
    private readonly int[] _oddsCellVer = { -1, -1, -1, -1 };
    private readonly int[] _oddsCellGen = { -1, -1, -1, -1 };
    private readonly string[] _oddsCell = { string.Empty, string.Empty, string.Empty, string.Empty };

    private string OddsCell(int k)
    {
        if (_oddsCellVer[k] == _reachVer && _oddsCellGen[k] == _langGen) return _oddsCell[k];
        _oddsCellVer[k] = _reachVer; _oddsCellGen[k] = _langGen;
        var name = _loc.T(QualityKeys[k]);
        int w = OddsWeight(k);
        if (w < 0) return _oddsCell[k] = name + " —";
        var weights = _levels!.Row(_st.Level)!.Weights(ReachBless);
        int total = 0;
        foreach (var x in weights) total += x;
        double pct = total > 0 ? w * 100.0 / total : 0;
        return _oddsCell[k] = name + " " + pct.ToString("0.##", CultureInfo.InvariantCulture) + "%";
    }
}
