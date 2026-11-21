using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The Auto Manifestation window, house style (approved prototype 2026-11-21, <c>automanifest-APPROVED.md</c> ①/④).
/// Top → bottom:
///   ToolHeader [40 px accent tile | bold title / ONE live status line / thin spent-vs-limit bar while running]
///   TARGETS    hint "stops when all are stored" · 5 reserved rows, scrolling past 5 · "+ Add target"
///   RUN        hint "each pull = N Remnants at Lv.L" · pull size chips · Blessing On/Off · spend limit · keep free
///   MATERIALS  hint "live" · Remnant / Dawn Prism counts
///   footer     Start (Primary, 120) or Stop (Danger, 120) + one line: last run / run log / end line with badge
/// Everything below the header sits in a <see cref="ConfirmHostElement"/>, so the Start confirm (③) locks it modally.
/// Every line is pinned (struts, fixed cells), so the window keeps ONE height in every state. The Funcs only read
/// cached fields; game reads happen on the 10 Hz tick (Plugin.cs).
/// </summary>
public sealed partial class Plugin
{
    private const float WindowWidth = 452f;   // approved ~452; content = 452 − 2 × 12 GlassMenu padding = 428
    private const float StatusLineH = 16f;
    private const float ProgressH   = 4f;
    private const float SettingRowH = 28f;
    private const float MaterialRowH = 22f;
    private const float StartPillW  = 120f;   // Start and Stop share it (approved)
    private const float RunDropdownW = 110f;
    private const float PullChipW   = 76f;
    private const float EndBadgeW   = 76f;

    private IWindowControl RegisterMain() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "automanifest.main",
            Title:       _loc.T(KeyTitle),
            DefaultRect: new WindowRect(20f, 120f, WindowWidth, 0f),   // 0 height = content-sized (content never changes height)
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            StartVisible = false, Closable = true, Draggable = true,
            Anchor = WindowAnchor.TopRight,
            TitleProvider = () => _loc.T(KeyTitle),
            // Built while hidden so the first open doesn't pay for the row pool (Plugin-Performance-Rules §5). Every Func
            // is safe without game state (cached fields only) and the target list reports 0 rows while hidden.
            Prebuild = true,
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildMainRoot(),
        OnClose: CloseMain));

    private HudElement BuildMainRoot() => new ColumnElement(new HudElement[]
    {
        BuildHeader(),
        new ConfirmHostElement(new ColumnElement(new HudElement[]
        {
            BuildTargetsPanel(),
            BuildRunPanel(),
            BuildMaterialsPanel(),
            BuildFooter(),
        }, Gap: 8f), _confirm) { ConfirmLabelWidth = 96f },
    }, Gap: 8f);

    // ── header ────────────────────────────────────────────────────────────────────────────────────────────────────
    private readonly ContrastCache _statusInk = new();

    private HudElement BuildHeader() => new RowElement(new HudElement[]
    {
        IconBox(() => HeaderIconPng(ToolGlyphSize), ToolIconSize, ToolGlyphSize),
        new CellElement(new ColumnElement(new HudElement[]
        {
            new TextElement(() => _loc.T(KeyTitle), Color: () => TextColor(), Emphasis: true, NoWrap: true) { Ellipsize = true },
            FixedLine(StatusLineH, SmallText(StatusText, StatusColor)),
            // The bar's slot is always there (a strut when idle), so starting a run never moves anything.
            FixedLine(ProgressH, new ConditionalElement(() => Running, ProgressBar(), new SpacerElement(0f, ProgressH))),
        }, Gap: 2f), Weight: 1f),
    }, Gap: 10f);

    // Running = accent (readable on the window background); idle = muted.
    private ColorRgba StatusColor() => Running ? _statusInk.Get(AccentColor(), WindowBg(), PillMinContrast) : MutedColor();

    private HudElement ProgressBar() => new BarElement(Progress, new ColorRgba(1f, 1f, 1f))
    {
        Style = BarStyle.Modern, Height = ProgressH, FillWidth = true,
        CornerRadius = 1f,   // ≤ (4 − 2) / 2: a larger radius would grow the 4 px bar (sliced min size 2r + 2)
        FillColorFn  = () => Fade(AccentColor()),
        TrackColorFn = () => Fade(Over(TextColor() with { A = 0.10f }, WindowBg())),
    };

    // Spent / limit, quantized to ~1 px of a 300 px bar so it re-meshes only when it visibly moves (§5).
    private float Progress() => SpendLimit <= 0 ? 0f : MathF.Round(Math.Clamp(_runSpent / (float)SpendLimit, 0f, 1f) * 300f) / 300f;

    // ── RUN ───────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildRunPanel() => SectionPanel(KeyRunCaption, RunHint, new[]
    {
        SettingRow(() => _loc.T(KeyPullSize), new RowElement(new HudElement[]
        {
            ChoiceChip(() => _loc.T(KeyPull1), () => !_pullTen, () => SetPullTen(false), PullChipW, PanelColor, () => Running),
            ChoiceChip(() => _loc.T(KeyPull10), () => _pullTen, () => SetPullTen(true), PullChipW, PanelColor, () => Running),
        }, Gap: 4f)),
        SettingRow(BlessingLabel, OnOffChip(() => _useBlessing, SetUseBlessing, () => !_st.BlessAllowed)),
        SettingRow(() => _loc.T(KeySpend), RunDropdown(() => _spendIndex, SpendOptions, SetSpendIndex)),
        SettingRow(() => _loc.T(KeyKeepFree), RunDropdown(() => _keepFreeIndex, KeepFreeOptions, SetKeepFreeIndex)),
    });

    private HudElement SettingRow(Func<string> label, HudElement control) => FixedLine(SettingRowH, new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(label, Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
        control,
    }, Gap: 8f));

    private static HudElement RunDropdown(Func<int> selected, string[] options, Action<int> onSelect)
        => new CellElement(new DropdownElement(selected, () => options, onSelect, Width: RunDropdownW)
            { Style = DropdownStyle.Pill, CornerRadius = 13f, MaxVisibleRows = 8 }, Width: RunDropdownW);

    // "each pull = 12 Remnants at Lv.2" — rebuilt only when the cost, the level or the language moves.
    private int _runHintCost = -1, _runHintLevel = -1, _runHintGen = -1;
    private string _runHint = string.Empty;

    private string RunHint()
    {
        if (!_st.Valid) return string.Empty;
        if (_st.CostPerPull == _runHintCost && _st.Level == _runHintLevel && _langGen == _runHintGen) return _runHint;
        _runHintCost = _st.CostPerPull; _runHintLevel = _st.Level; _runHintGen = _langGen;
        return _runHint = _loc.TFormat(KeyRunHint, _st.CostPerPull, _st.Level);
    }

    // "Use Blessing (Dawn Prism · 64 owned)" — the material name from the game, rebuilt when the count moves.
    private long _blessLabelCount = -1;
    private int _blessLabelGen = -1;
    private string _blessLabelName = string.Empty, _blessLabel = string.Empty;

    private string BlessingLabel()
    {
        var name = PrismName();
        if (_st.Prisms == _blessLabelCount && _langGen == _blessLabelGen && ReferenceEquals(name, _blessLabelName)) return _blessLabel;
        _blessLabelCount = _st.Prisms; _blessLabelGen = _langGen; _blessLabelName = name;
        return _blessLabel = _loc.TFormat(KeyBlessing, name, N0(_st.Prisms));
    }

    // ── MATERIALS ─────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildMaterialsPanel() => SectionPanel(KeyMatCaption, () => _loc.T(KeyMatHint), new[]
    {
        MaterialRow(RemnantName, () => _matRemnantText.Get(_st.Valid ? _st.Remnants : -1)),
        MaterialRow(PrismName, () => _matPrismText.Get(_st.Valid ? _st.Prisms : -1)),
    });

    private readonly CountText _matRemnantText = new(), _matPrismText = new();

    // A number shown as "1,234", re-formatted only when it changes ("—" while unknown).
    private sealed class CountText
    {
        private long _v = long.MinValue;
        private string _s = "—";

        public string Get(long v)
        {
            if (v == _v) return _s;
            _v = v;
            return _s = v < 0 ? "—" : N0(v);
        }
    }

    private HudElement MaterialRow(Func<string> name, Func<string> count) => FixedLine(MaterialRowH, new RowElement(new HudElement[]
    {
        new CellElement(new TextElement(name, Color: () => TextColor(), NoWrap: true) { Ellipsize = true }, Weight: 1f),
        new TextElement(count, Color: () => TextColor(), Width: 96f, Align: TextAlign.Right, NoWrap: true, FontSize: 14) { Bold = true },
    }, Gap: 8f));

    // ── footer ────────────────────────────────────────────────────────────────────────────────────────────────────
    private HudElement BuildFooter() => FixedLine(PillH, new RowElement(new HudElement[]
    {
        new CellElement(new ConditionalElement(() => Running,
            PillButton(() => _loc.T(KeyStop), PillKind.Danger, OnStopClicked, StartPillW, WindowBg, () => !_stopRequested),
            PillButton(() => _loc.T(KeyStart), PillKind.Primary, OnStartClicked, StartPillW, WindowBg, CanStart)),
            Width: StartPillW),
        new CellElement(new ConditionalElement(() => !Running && _endReason != EndReason.None,
            new RowElement(new HudElement[]
            {
                Badge(EndBadgeText, EndBadgeColor, WindowBg, EndBadgeW),
                new CellElement(SmallText(EndLineText, MutedColor), Weight: 1f),
            }, Gap: 6f),
            SmallText(FooterText, MutedColor)), Weight: 1f),
    }, Gap: 10f));
}
