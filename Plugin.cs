using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// Auto Manifest — automates the game's Matrix Manifestation (Season Talent Reforge; KB Matrix-Manifestation.md): the
/// player picks target imprints ("imprint X, value v or higher" = "v+") and the plugin rolls, stores every match into the pocket, and
/// stops when all targets are stored or a limit is hit. Approved UI: <c>_scratch\prototypes\automanifest-APPROVED.md</c>.
/// <para>Layout: <c>Plugin.Window*.cs</c> (main window), <c>Plugin.Picker*.cs</c> (Add-target picker),
/// <c>Plugin.Run.cs</c> (the run state machine), <c>Plugin.Lua*.cs</c> (the game bridge + catalogue load),
/// <c>Catalog.cs</c> / <c>Matcher.cs</c> (pure data + matching), <c>Plugin.Style*.cs</c> (house kit).</para>
/// <para>Ticking: one <see cref="IFramework.Update"/> handler throttled to 10 Hz. Idle with both windows closed it is a
/// float compare + two bools; the game is read (Lua) only while a window is open (1 Hz state refresh) or a run is going
/// (Plugin-Performance-Rules §1 / §7).</para>
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    private const float TickS        = 0.1f;   // 10 Hz — every game read / run step happens on this beat
    private const float IdleRefreshS = 1f;     // window open, no run: live counts once per second

    public string Name => "AutoManifest";

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;
    private readonly ConfirmController _confirm = new();
    private readonly IWindowControl _mainWindow;
    private readonly IWindowControl _pickerWindow;
    private readonly WindowCenterer _pickerCenterer;
    private readonly IHotkeyAction _toggle;
    private readonly IDisposable _launcherEntry;

    private float _nextTickAt, _stateReadAt = float.NegativeInfinity;

    private float FwNow => _services.Framework.TimeNow;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        LoadConfig();

        _pickerWindow = RegisterPicker();
        _mainWindow = RegisterMain();
        _pickerCenterer = new WindowCenterer(_services.Framework, _pickerWindow, WindowCenterer.CentreOf(_mainWindow));

        // Same discoverability as Loadout Switcher: a toggle hotkey WITHOUT a default binding + a launcher tile.
        _toggle = _services.Hotkeys.DeclareAction(
            new HotkeyAction(Id: "automanifest.window.toggle", Description: _loc.T(KeyHotkeyToggle), SuggestedDefault: null),
            callback: ToggleMain);
        // Title stays the fixed literal (the launcher's pin-identity key); TitleProvider re-localizes the tile live.
        _launcherEntry = _services.Launcher.Register(new LauncherEntry("Auto Manifestation", IconPng: LauncherIconPng(),
            IconKey: null, OnOpen: ToggleMain)
        {
            ShouldShow = () => _services.ClientState.Phase == Stellar.Abstractions.Domain.GamePhase.World,
            TitleProvider = () => _loc.T(KeyTitle),
        });

        _services.Framework.Update += OnUpdate;
        _services.ClientState.Logout += OnLogout;
        _loc.LanguageChanged += OnLanguageChanged;
        _services.Log.Info("[AutoManifest] plugin constructed");
    }

    public void Dispose()
    {
        _services.Framework.Update -= OnUpdate;
        _services.ClientState.Logout -= OnLogout;
        _loc.LanguageChanged -= OnLanguageChanged;
        _pickerCenterer.Stop();
        try { _launcherEntry.Dispose(); } catch { /* disposal must not throw */ }
        try { _toggle.Dispose(); } catch { /* disposal must not throw */ }
        try { _pickerWindow.Remove(); } catch { /* disposal must not throw */ }
        try { _mainWindow.Remove(); } catch { /* disposal must not throw */ }
    }

    private void OnUpdate(float dt)
    {
        float now = FwNow;
        if (now < _nextTickAt) return;
        _nextTickAt = now + TickS;

        if (Running) { RunTick(now); return; }
        if (!_mainWindow.IsShown && !_pickerWindow.IsShown) return;   // nothing to show → no game reads
        if (!InWorld()) return;
        if (!_catalogReady) CatalogLoadTick();
        if (now - _stateReadAt >= IdleRefreshS) RefreshIdleState(now);
    }

    private void RefreshIdleState(float now)
    {
        _stateReadAt = now;
        _st = ReadState();
        RefreshMaterialsFromState();
    }

    // Live state side effects: the looting class name (only when the class changes) and the picker's Class list.
    private void RefreshMaterialsFromState()
    {
        if (!_st.Valid || _st.Profession == _profId) return;
        RefreshProfessionName(_st.Profession);
        RefilterPicker();
    }

    private void MarkMain() => _mainWindow?.MarkDirty();

    // Launcher tile + hotkey. Opening reads the state at once (next tick), so the header is right on the first frame.
    private void ToggleMain()
    {
        if (_mainWindow.IsShown) { CloseMain(); return; }
        _stateReadAt = float.NegativeInfinity;
        _nextTickAt = 0f;
        _mainWindow.SetVisiblePersist(true);
    }

    // Closing the window stops the run (after the request in flight) and drops the picker + an open confirm.
    private void CloseMain()
    {
        if (Running) OnStopClicked();
        _confirm.Cancel();
        ClosePicker();
        if (_mainWindow.IsShown) _mainWindow.SetVisiblePersist(false);
    }

    // Logout: synchronously (Update is world-gated, so no Post) end the run and close every window, so nothing stays
    // up through the loading screen (house rule, MultiTool 3554a1e). SetVisible, not SetVisiblePersist: logging out is
    // not the player closing the window.
    private void OnLogout()
    {
        End(EndReason.LeftWorld);
        _confirm.Cancel();
        ClosePicker();
        if (_mainWindow.IsShown) _mainWindow.SetVisible(false);
        _profId = -1;
    }
}
