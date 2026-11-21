using System;
using System.Diagnostics;
using System.Globalization;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.AutoManifest;

/// <summary>
/// C# side of the Lua bridge (<c>Plugin.Lua.Script.cs</c>): define the script once, call its functions with one-line
/// chunks, read the parked results back. Main thread only, and only while in a stable world (never at the title screen:
/// a mod must not be the FIRST caller of a game VM / DataMgr — Lua-Injection-from-CSharp.md "Z.DataMgr.Get caches
/// BEFORE Init()"). Also owns the sliced catalogue load (Plugin-Performance-Rules §1: long table walks are sliced).
/// </summary>
public sealed partial class Plugin
{
    // Constant chunks (no per-call string building). rawget: never trips the strict-global guard on a lost table.
    private const string CallState   = "pcall(function() rawget(_G,'__stam').state() end)";
    private const string CallResults = "pcall(function() rawget(_G,'__stam').results() end)";
    private const string CallRows    = "pcall(function() rawset(_G,'__stam_rows',nil) rawget(_G,'__stam').rows() end)";
    private const string CallMeta    = "pcall(function() rawset(_G,'__stam_meta',nil) rawget(_G,'__stam').meta() end)";

    private bool InWorld()
    {
        var cs = _services.ClientState;
        return cs.Phase == GamePhase.World && cs.IsWorldActive && (cs.UiState & GameUIState.Loading) == 0;
    }

    // Defines the script when it is missing or older (a Lua reset drops it; a plugin update bumps the version).
    private bool EnsureLua()
    {
        var lua = _services.Lua;
        if (lua is null || !lua.Ready || !InWorld()) return false;
        if (lua.TryReadGlobalNumber("__stam_v", out var v) && (int)v == LuaScriptVersion) return true;
        lua.DoString(LuaScript);
        if (lua.TryReadGlobalNumber("__stam_v", out v) && (int)v == LuaScriptVersion) return true;
        if (!_luaDefineWarned) { _luaDefineWarned = true; _services.Log.Warning("[AutoManifest] Lua script did not define (Lua error?)"); }
        return false;
    }

    private bool _luaDefineWarned;

    private ReforgeState ReadState()
    {
        if (!EnsureLua()) return new ReforgeState();
        _services.Lua.DoString(CallState);
        return ReforgeState.Parse(_services.Lua.ReadGlobalString("__stam_s"), _st);
    }

    // The current season's level table (weights, EXP per level). Static game data: read once per season (Plugin.Reach.cs).
    private const string CallLevels = "pcall(function() rawset(_G,'__stam_lv',nil) rawget(_G,'__stam').levels() end)";

    private MatrixLevels? ReadLevels()
    {
        if (!EnsureLua()) return null;
        _services.Lua.DoString(CallLevels);
        var t = MatrixLevels.Parse(_services.Lua.ReadGlobalString("__stam_lv"));
        _services.Lua.DoString("rawset(_G,'__stam_lv',nil)");
        return t;
    }

    private ReforgeResults ReadResults()
    {
        if (!EnsureLua()) return new ReforgeResults();
        _services.Lua.DoString(CallResults);
        return ReforgeResults.Parse(_services.Lua.ReadGlobalString("__stam_r"));
    }

    // Starts the async roll / store in a game coroutine; the outcome lands in __stam_ret (TryTakeRet).
    private bool StartRoll(bool bless, int times)
    {
        if (!EnsureLua()) return false;
        _services.Lua.DoString("pcall(function() rawset(_G,'__stam_ret',nil) rawget(_G,'__stam').roll("
                               + (bless ? "true" : "false") + "," + times.ToString(CultureInfo.InvariantCulture) + ") end)");
        return true;
    }

    private bool StartStore(int pull0, int row0)
    {
        if (!EnsureLua()) return false;
        _services.Lua.DoString("pcall(function() rawset(_G,'__stam_ret',nil) rawget(_G,'__stam').store("
                               + pull0.ToString(CultureInfo.InvariantCulture) + "," + row0.ToString(CultureInfo.InvariantCulture) + ") end)");
        return true;
    }

    // Pocket discard (Plugin.Pocket.cs). Shares __stam_ret with roll / store, so it is only ever started with no run going
    // (and a run can't start while it is in flight — CanStart).
    private bool StartDiscard(int uuid)
    {
        if (!EnsureLua()) return false;
        _services.Lua.DoString("pcall(function() rawset(_G,'__stam_ret',nil) rawget(_G,'__stam').discard("
                               + uuid.ToString(CultureInfo.InvariantCulture) + ") end)");
        return true;
    }

    private bool TryTakeRet(out int ret)
    {
        ret = 0;
        if (_services.Lua is not { Ready: true } lua || !lua.TryReadGlobalNumber("__stam_ret", out var d)) return false;
        ret = (int)d;
        return true;
    }

    private string LuaError() => _services.Lua?.ReadGlobalString("__stam_err") ?? string.Empty;

    // ── looting class name (cached per profession id) ─────────────────────────────────────────────────────────────
    private int _profId = -1;
    private string _profName = string.Empty;

    private void RefreshProfessionName(int id)
    {
        if (id == _profId || !EnsureLua()) return;
        _services.Lua.DoString("pcall(function() rawset(_G,'__stam_prof',nil) rawget(_G,'__stam').prof("
                               + id.ToString(CultureInfo.InvariantCulture) + ") end)");
        _profName = ImprintCatalog.StripTags(_services.Lua.ReadGlobalString("__stam_prof") ?? string.Empty);
        _profId = id;
    }

    // ── catalogue load (sliced) ───────────────────────────────────────────────────────────────────────────────────
    // Step 1 (one tick): the compact row dump of SeasonTalentFactorAttrLibTable (2332 rows, no Lang calls) + category /
    // material names. Then the localized names and value texts, NamesSlice groups per tick (each group = 11 game
    // formatter calls). Published as ready only when complete. Started on the first window open, never per frame.
    private const int NamesSlice = 16;

    private ImprintCatalog? _catalog;
    private bool _catalogReady;
    private int _namesNext;
    private string[] _gameCategoryNames = Array.Empty<string>();
    private string _remnantName = string.Empty, _prismName = string.Empty;

    private void CatalogLoadTick()
    {
        if (_catalogReady || !EnsureLua()) return;
        long t0 = StellarDiagnostics.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        var lua = _services.Lua;
        if (_catalog == null)
        {
            lua.DoString(CallRows);
            var rows = lua.ReadGlobalString("__stam_rows");
            if (string.IsNullOrEmpty(rows)) return;   // tables not ready yet — retry next tick
            _catalog = ImprintCatalog.ParseRows(rows);
            lua.DoString("rawset(_G,'__stam_rows',nil)");   // free the dump on the Lua side

            lua.DoString(CallMeta);
            var meta = (lua.ReadGlobalString("__stam_meta") ?? string.Empty).Split('\x1F');
            _gameCategoryNames = new string[CategoryLibIds.Length];
            for (int i = 0; i < _gameCategoryNames.Length; i++)
                _gameCategoryNames[i] = i < meta.Length ? ImprintCatalog.StripTags(meta[i]).Trim() : string.Empty;
            if (meta.Length > CategoryLibIds.Length)     _remnantName = ImprintCatalog.StripTags(meta[CategoryLibIds.Length]).Trim();
            if (meta.Length > CategoryLibIds.Length + 1) _prismName   = ImprintCatalog.StripTags(meta[CategoryLibIds.Length + 1]).Trim();
            _namesNext = 0;
            LogCatalogTiming("rows", t0);
            return;
        }

        int to = Math.Min(_namesNext + NamesSlice, _catalog.All.Count);
        lua.DoString("pcall(function() rawset(_G,'__stam_names',nil) rawget(_G,'__stam').names('"
                     + _catalog.NamesSpec(_namesNext, to) + "') end)");
        _catalog.ApplyNames(lua.ReadGlobalString("__stam_names") ?? string.Empty);
        _namesNext = to;
        LogCatalogTiming("names", t0);
        if (_namesNext < _catalog.All.Count) return;

        _catalogReady = true;
        RebindTargets();
        RefilterPicker();
        _services.Log.Info($"[AutoManifest] catalogue ready: {_catalog.All.Count} imprints, {_catalog.ByEntry.Count} values; "
                           + $"materials '{_remnantName}' / '{_prismName}'; sample: {CatalogSample()}");
        _mainWindow?.MarkDirty();
        _pickerWindow?.MarkDirty();
    }

    // A few names + ranges in the log, so the in-game check can see what the game's formatters returned.
    private string CatalogSample()
    {
        if (_catalog == null || _catalog.All.Count == 0) return "-";
        var a = _catalog.All;
        int[] picks = { 0, a.Count / 4, a.Count / 2, a.Count - 1 };
        var sb = new System.Text.StringBuilder();
        foreach (var i in picks)
            sb.Append('[').Append(a[i].Group).Append(' ').Append(a[i].Name).Append(' ').Append(a[i].RangeText).Append("] ");
        return sb.ToString();
    }

    private void LogCatalogTiming(string step, long t0)
    {
        if (t0 == 0) return;
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        _services.Log.Info($"[AutoManifest] catalogue {step} slice {ms:0.0} ms");
    }
}
