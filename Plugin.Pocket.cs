using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The pocket window (approved ⑨): a title-bar-less child window opened by the main window's "Pocket N/M" pill, centred on
/// the main window and in front (<see cref="WindowCenterer.OpenCentred"/> — the CLAUDE.md Rule 5 exception for re-opening
/// an already-visible child). Top → bottom:
///   POCKET · N/M caption + icon-only ✕
///   STORED IMPRINTS panel, hint "discarding frees the slot" ("locked while running" during a run): one 26 px tile per
///   pocket entry, best first, then a faint "free slot" tile per free unlocked slot; 6 rows visible, then it scrolls
///   footer: muted "25 more slots can be unlocked in the game's own pocket screen." (hidden at 30) + Neutral Close
/// A row's ✕ (red hover) asks a modal Danger confirm (<see cref="ConfirmHostElement"/>, ALWAYS asked) and then discards
/// through the game's own pocket-screen call (Lua <c>M.discard</c>). The stored marks follow by themselves: the next
/// state read no longer holds the uuid, so <see cref="ValidateMarks"/> drops that copy and the target row falls back to
/// its next copy or to waiting. This window never discards on its own (only a capped "Keep better rolls" run does,
/// spec ⑫ — Plugin.Run.Replace.cs; that discard refreshes these rows like any pocket change) and never unlocks slots. Rows:
/// <c>Plugin.Pocket.Rows.cs</c>.
/// </summary>
public sealed partial class Plugin
{
    private const float PocketWidth  = 512f;   // same as the main window; content = 488
    private const float PocketCloseW = 96f;
    private const float DiscardTimeoutS = 15f;

    private readonly ConfirmController _pocketConfirm = new();
    private readonly HoverFlag _pocketCloseHover = new();
    private bool _pocketOpen;          // opened by the player (IsShown lags a tick on the first mount)
    private bool _pocketResetScroll;
    private bool _discardBusy;         // a discard request is in flight (its ret lands in __stam_ret)
    private float _discardAt;
    private int _discardUuid, _discardEntry;

    private IWindowControl RegisterPocket() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "automanifest.pocket",
            Title:       _loc.T(KeyPocketPanel),
            DefaultRect: new WindowRect(220f, 180f, PocketWidth, 0f),   // content-sized; the list height is fixed
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            StartVisible = false, Closable = true, Draggable = true,
            ShowTitleBar = false,   // the caption row carries the title and the ✕; the whole frame drags
            TitleProvider = () => _loc.T(KeyPocketPanel),
            Prebuild = true,        // the list reports 0 rows while hidden, so the hidden build binds nothing
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildPocketRoot(),
        OnClose: ClosePocket));

    // Everything sits in the confirm host: the scrim covers the whole body anyway (title-bar-less window), and the card
    // gets the full window height to centre in.
    private HudElement BuildPocketRoot() => new ConfirmHostElement(new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new TextElement(PocketCaption, Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new SpacerElement(0f),
            CrossButton(() => _pocketCloseHover.On, over => _pocketCloseHover.On = over, ClosePocket, WindowBg, danger: false),
        }, Gap: 8f),
        SectionPanel(KeyPocketPanel, () => _loc.T(Running ? KeyPocketHintRunning : KeyPocketHint), new[] { BuildPocketList() }),
        FixedLine(PillH, new RowElement(new HudElement[]
        {
            // 11 px and WRAPPING (like the main footer's gate line): two lines fit the 28 px footer in every language.
            new CellElement(new TextElement(PocketUnlockText, Color: () => MutedColor(), FontSize: 11) { RichEmoji = true }, Weight: 1f),
            PillButton(() => _loc.T(KeyClose), PillKind.Neutral, ClosePocket, PocketCloseW, WindowBg),
        }, Gap: 10f)),
    }, Gap: 8f), _pocketConfirm) { ConfirmLabelWidth = 96f };

    // "POCKET · 3/5" — rebuilt only when the count, the limit or the language moves.
    private int _pkCapN = -1, _pkCapM = -1, _pkCapGen = -1;
    private string _pkCap = string.Empty;

    private string PocketCaption()
    {
        int n = _st.Valid ? _st.Stored : 0, m = _st.Valid ? _st.StoreLimit : 0;
        if (n == _pkCapN && m == _pkCapM && _langGen == _pkCapGen && _pkCap.Length > 0) return _pkCap;
        _pkCapN = n; _pkCapM = m; _pkCapGen = _langGen;
        return _pkCap = _loc.TFormat(KeyPocketCaption, n, m).ToUpperInvariant();
    }

    // "25 more slots can be unlocked in the game's own pocket screen." — 30 (SeasonTalentReforgeStorageTable) minus the
    // unlocked count; empty at 30 (or before the first state read).
    private int _pkUnlockLimit = -1, _pkUnlockGen = -1;
    private string _pkUnlock = string.Empty;

    private string PocketUnlockText()
    {
        int limit = _st.Valid ? _st.StoreLimit : -1;
        if (limit == _pkUnlockLimit && _langGen == _pkUnlockGen) return _pkUnlock;
        _pkUnlockLimit = limit; _pkUnlockGen = _langGen;
        int more = limit <= 0 ? 0 : MaxPocket - limit;
        return _pkUnlock = more <= 0 ? string.Empty
                         : more == 1 ? _loc.T(KeyPocketUnlockOne) : _loc.TFormat(KeyPocketUnlockMany, more);
    }

    // ── open / close ──────────────────────────────────────────────────────────────────────────────────────────────
    private bool CanOpenPocket() => _catalogReady && _st.Valid && _st.HasPocket;

    private void OpenPocket()
    {
        if (!CanOpenPocket()) return;
        _pocketOpen = true;
        _pkSrc = null;                 // force the row rebuild from the state we have now …
        RefreshPocketView();
        _stateReadAt = float.NegativeInfinity;   // … and a fresh state read on the next tick
        _nextTickAt = 0f;
        _pocketResetScroll = true;
        _pocketCenterer.OpenCentred();
        _pocketWindow.MarkDirty();
    }

    private void ClosePocket()
    {
        _pocketOpen = false;
        _pocketConfirm.Cancel();
        _pocketCenterer?.Stop();
        if (_pocketWindow is { IsShown: true }) _pocketWindow.SetVisible(false);
    }

    // ── discard ───────────────────────────────────────────────────────────────────────────────────────────────────
    // ✕ is inert during a run (no discard racing a store) and while another discard is in flight.
    private bool CanDiscard() => !Running && !_discardBusy;

    private void OnDiscardClicked(int index)
    {
        if (!CanDiscard() || index < 0 || index >= _pkCount) return;
        int uuid = _pkUuid[index], entry = _pkEntry[index], target = _pkTarget[index];
        var title = _loc.TFormat(KeyDiscardTitle, _pkImp[index]?.Name ?? "?", ValueOf(entry));
        var body = _loc.T(KeyDiscardBody);
        // A copy Auto Manifest stored for a row: say what the row does next — its next copy, or back to waiting.
        if (target >= 0 && target < _targets.Count)
            body += " " + _loc.TFormat(OtherActiveCopy(_targets[target], uuid) ? KeyDiscardNext : KeyDiscardWaiting, TargetRowLabel(target));
        _pocketConfirm.Show(new ConfirmRequest(title, body, () => ConfirmDiscard(uuid, entry))
        {
            ConfirmLabel = _loc.T(KeyDiscard), CancelLabel = _loc.T(KeyCancel), Severity = ConfirmSeverity.Danger,
        });
    }

    private static bool OtherActiveCopy(Target t, int uuid)
    {
        foreach (var c in t.Copies) if (c.Active && c.Uuid != uuid) return true;
        return false;
    }

    // "Great Crimson Lotus 2.24%+" — the row as the main window shows it (name + its minimum option).
    private string TargetRowLabel(int t)
    {
        var tg = _targets[t];
        if (_catalog == null || !_catalog.ByGroup.TryGetValue(tg.Group, out var imp)) return "#" + tg.Group;
        return imp.MinOptions.Length == 0 ? imp.Name : imp.Name + " " + imp.MinOptions[Math.Clamp(tg.MinStep, 0, imp.MinOptions.Length - 1)];
    }

    // Confirmed. Re-checked against the LIVE state: a run may have started, or the pocket moved, while the card was open.
    private void ConfirmDiscard(int uuid, int entry)
    {
        if (!CanDiscard() || !InWorld() || !_st.Valid || !_st.PocketHolds(uuid, entry))
        {
            _services.Log.Info($"[AutoManifest] discard skipped: uuid={uuid} entry={entry} running={Running} busy={_discardBusy} "
                               + $"inPocket={_st.Valid && _st.PocketHolds(uuid, entry)}");
            return;
        }
        if (!StartDiscard(uuid)) { _services.Log.Warning("[AutoManifest] discard: Lua not ready"); return; }
        _discardBusy = true;
        _discardAt = FwNow;
        _discardUuid = uuid; _discardEntry = entry;
        _services.Log.Info($"[AutoManifest] discard → uuid={uuid} entry={entry} {EntryText(entry)} char={_st.CharId} "
                           + $"season={_st.SeasonId} pocket={_st.Stored}/{_st.StoreLimit}");
        _pocketWindow.MarkDirty();
        MarkMain();
    }

    // From OnUpdate (10 Hz) while a discard is in flight. The reply is ret only; the pocket itself changes through the
    // container sync, which the next state read (forced here) picks up — ValidateMarks then drops the copy.
    private void DiscardTick(float now)
    {
        if (TryTakeRet(out var ret))
        {
            _discardBusy = false;
            // errorId < 1000 = the game treats it as a disconnect.
            _services.Log.Info($"[AutoManifest] discard ← ret={ret}{ErrSuffix(ret)}{(ret > 0 && ret < 1000 ? " (disconnect code)" : "")} "
                               + $"uuid={_discardUuid} entry={_discardEntry} after {(now - _discardAt) * 1000f:0} ms");
        }
        else if (now - _discardAt > DiscardTimeoutS)
        {
            _discardBusy = false;
            _services.Log.Warning($"[AutoManifest] discard: no reply within {DiscardTimeoutS:0} s (uuid={_discardUuid})");
        }
        else return;
        _stateReadAt = float.NegativeInfinity;
        _pocketWindow.MarkDirty();
        MarkMain();
    }
}
