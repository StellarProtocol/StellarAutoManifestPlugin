using System;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// Auto Manifest — automates the game's Matrix Manifestation (Season Talent Reforge): rolls until the imprints the
/// player picked are stored in the pocket. Scaffold only; the window and the run loop land in later commits.
/// </summary>
public sealed partial class Plugin : IStellarPlugin
{
    public string Name => "AutoManifest";

    private readonly IPluginServices _services;
    private readonly ILocalization _loc;

    public Plugin(IPluginServices services)
    {
        _services = services;
        _loc = services.Localization;
        _services.Log.Info("[AutoManifest] plugin constructed");
    }

    public void Dispose() { }
}
