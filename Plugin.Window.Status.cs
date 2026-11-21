using Stellar.Abstractions.Domain;

namespace Stellar.AutoManifest;

/// <summary>
/// The window's live text: the header status line, the footer log line while running, and the end line (⑤) with its
/// Done / Stopped / Error badge. Each is composed only when one of its inputs moves (or the language switches) and the
/// polled Func hands back the cached string (Plugin-Performance-Rules §2). The footer stores WHAT happened (event + ids
/// + counts), not the finished text, so a language switch re-words a line that is still on screen.
/// </summary>
public sealed partial class Plugin
{
    // ── header status ─────────────────────────────────────────────────────────────────────────────────────────────
    // Idle: "Ready · Matrix Lv.2 · Pocket 1/5 · looting class: Twin Striker"; running: "Running · pull 23 · 552 / 1,200
    // Remnants". The cache key is every input that can change the text.
    private (bool ready, bool valid, bool running, int a, int b, int c, long d, string prof, int gen) _statusKey;
    private string _statusText = string.Empty;

    private string StatusText()
    {
        var key = Running
            ? (true, true, true, _runPulls, 0, 0, _runSpent, string.Empty, _langGen)
            : (_catalogReady, _st.Valid, false, _st.Level, _st.Stored, _st.StoreLimit, (long)SpendLimit, _profName, _langGen);
        if (key == _statusKey && _statusText.Length > 0) return _statusText;
        _statusKey = key;
        _statusText = Running ? _loc.TFormat(KeyStatusRunning, _runPulls, N0(_runSpent), N0(SpendLimit))
                    : !_catalogReady ? _loc.T(KeyStatusLoading)
                    : !_st.Valid ? _loc.T(KeyStatusNoData)
                    : _loc.TFormat(KeyStatusReady, _st.Level, _st.Stored, _st.StoreLimit, _profName.Length > 0 ? _profName : "—");
        return _statusText;
    }

    // ── footer log line (running) / last run (idle, before any run) ───────────────────────────────────────────────
    private enum FooterEvent : byte { None, Starting, Stopping, Pull, Stored }

    private FooterEvent _fbEvent;
    private int _fbPull, _fbEntry, _fbStored, _fbLimit, _fbVer, _fbBuiltVer = -1, _fbBuiltGen = -1;
    private string _fbText = string.Empty;

    private void SetFooter(FooterEvent ev, int entryId = 0)
    {
        // "Stopping…" stays until the run ends; a later pull/store line must not hide that the stop was heard.
        if (_stopRequested && ev != FooterEvent.Stopping) ev = FooterEvent.Stopping;
        _fbEvent = ev; _fbPull = _runPulls; _fbEntry = entryId; _fbStored = _st.Stored; _fbLimit = _st.StoreLimit;
        _fbVer++;
        MarkMain();
    }

    private string FooterText()
    {
        if (!Running) return _loc.T(KeyLastRunNone);
        if (_fbVer == _fbBuiltVer && _langGen == _fbBuiltGen) return _fbText;
        _fbBuiltVer = _fbVer; _fbBuiltGen = _langGen;
        return _fbText = _fbEvent switch
        {
            FooterEvent.Stopping => _loc.T(KeyLogStopping),
            FooterEvent.Pull     => _loc.TFormat(KeyLogPull, _fbPull, _fbStored, _fbLimit),
            FooterEvent.Stored   => _loc.TFormat(KeyLogStored, _fbPull, EntryName(_fbEntry), ValueOf(_fbEntry), _fbStored, _fbLimit),
            _                    => _loc.T(KeyLogStarting),
        };
    }

    private string EntryName(int entryId)
        => _catalog != null && _catalog.ByEntry.TryGetValue(entryId, out var hit) ? hit.Imp.Name : "?";

    // ── end line (⑤) ──────────────────────────────────────────────────────────────────────────────────────────────
    private LocComposed? _endComposedCache;
    private LocComposed _endComposed => _endComposedCache ??= new LocComposed(ComposeEndLine);

    private string EndLineText() => _endComposed.Get(_langGen);

    private string ComposeEndLine() => _endReason switch
    {
        EndReason.AllStored    => _loc.TFormat(KeyEndAllStored, KnownTargetCount(), _endPulls, N0(_endSpent)),
        EndReason.PocketFull   => _endWaiting == 1
                                    ? _loc.TFormat(KeyEndPocketOne, _endStored, _endLimit)
                                    : _loc.TFormat(KeyEndPocketMany, _endStored, _endLimit, _endWaiting),
        EndReason.SpendLimit   => _loc.TFormat(KeyEndSpend, N0(SpendLimit)),
        EndReason.NoRemnants   => _loc.T(KeyEndNoRemnants),
        EndReason.NoPrism      => _loc.TFormat(KeyEndNoPrism, PrismName()),
        EndReason.Refused      => _loc.T(KeyEndRefused),
        EndReason.StoreRefused => _loc.T(KeyEndStoreFailed),
        EndReason.Timeout      => _loc.T(KeyEndTimeout),
        EndReason.UserStop     => _loc.TFormat(KeyEndUserStop, _endPulls, N0(_endSpent)),
        EndReason.LeftWorld    => _loc.T(KeyEndLeftWorld),
        _                      => string.Empty,
    };

    private enum EndKind : byte { Done, Stopped, Error }

    private EndKind CurrentEndKind() => _endReason switch
    {
        EndReason.AllStored => EndKind.Done,
        EndReason.Refused or EndReason.StoreRefused or EndReason.Timeout => EndKind.Error,
        _ => EndKind.Stopped,
    };

    private string EndBadgeText() => _loc.T(CurrentEndKind() switch
    {
        EndKind.Done  => KeyBadgeDone,
        EndKind.Error => KeyBadgeError,
        _             => KeyBadgeStopped,
    });

    // Done green, Stopped amber, Error red — the theme's status colours.
    private ColorRgba EndBadgeColor() => CurrentEndKind() switch
    {
        EndKind.Done  => SuccessColor(),
        EndKind.Error => DangerColor(),
        _             => CautionColor(),
    };
}
