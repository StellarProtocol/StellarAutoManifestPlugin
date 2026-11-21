using System;
using System.Globalization;
using Stellar.Abstractions.Domain;

namespace Stellar.AutoManifest;

// Localized text (CLAUDE.md Rule 10: every user-facing string is a key in ALL Lang/*.json). Imprint names, value texts,
// category names, material names and the looting class come from the GAME's own data (Plugin.Lua*.cs), so they follow
// the client language by themselves; the keys below are the plugin's own wording only. Plain labels are
// `() => _loc.T(key)` Funcs (T hands back the catalog's own string instance — no allocation per poll). Text COMPOSED
// from catalog strings is cached against _langGen (bumped by ILocalization.LanguageChanged) or against its inputs.
public sealed partial class Plugin
{
    private const string KeyTitle          = "am.title";
    private const string KeyHotkeyToggle   = "am.hotkey.toggle";
    private const string KeyOn             = "am.ui.on";
    private const string KeyOff            = "am.ui.off";
    private const string KeyCancel         = "am.ui.cancel";
    private const string KeyStatusLoading  = "am.status.loading";
    private const string KeyStatusNoData   = "am.status.noData";
    private const string KeyStatusReady    = "am.status.ready";
    private const string KeyStatusRunning  = "am.status.running";
    private const string KeyTargetsCaption = "am.targets.caption";
    private const string KeyTargetsHint    = "am.targets.hint";
    private const string KeyWaiting        = "am.targets.waiting";
    private const string KeyStored         = "am.targets.stored";
    private const string KeyAddTarget      = "am.targets.add";
    private const string KeyRunCaption     = "am.run.caption";
    private const string KeyRunHint        = "am.run.hint";
    private const string KeyPullSize       = "am.run.pullSize";
    private const string KeyPull1          = "am.run.pull1";
    private const string KeyPull10         = "am.run.pull10";
    private const string KeyBlessing       = "am.run.blessing";
    private const string KeySpend          = "am.run.spend";
    private const string KeyKeepFree       = "am.run.keepFree";
    private const string KeyMatCaption     = "am.materials.caption";
    private const string KeyMatHint        = "am.materials.hint";
    private const string KeyMatRemnant     = "am.materials.remnantFallback";
    private const string KeyMatPrism       = "am.materials.prismFallback";
    private const string KeyStart          = "am.start";
    private const string KeyStop           = "am.stop";
    private const string KeyLastRunNone    = "am.footer.lastRunNone";
    private const string KeyLogStarting    = "am.footer.starting";
    private const string KeyLogStopping    = "am.footer.stopping";
    private const string KeyLogPull        = "am.footer.pull";
    private const string KeyLogStored      = "am.footer.stored";
    private const string KeyBadgeDone      = "am.end.badgeDone";
    private const string KeyBadgeStopped   = "am.end.badgeStopped";
    private const string KeyBadgeError     = "am.end.badgeError";
    private const string KeyEndAllStored   = "am.end.allStored";
    private const string KeyEndPocketOne   = "am.end.pocketFullOne";
    private const string KeyEndPocketMany  = "am.end.pocketFullMany";
    private const string KeyEndSpend       = "am.end.spendLimit";
    private const string KeyEndNoRemnants  = "am.end.noRemnants";
    private const string KeyEndNoPrism     = "am.end.noPrism";
    private const string KeyEndRefused     = "am.end.refused";
    private const string KeyEndStoreFailed = "am.end.storeRefused";
    private const string KeyEndTimeout     = "am.end.timeout";
    private const string KeyEndUserStop    = "am.end.userStop";
    private const string KeyEndLeftWorld   = "am.end.leftWorld";
    private const string KeyConfirmTitle   = "am.confirm.title";
    private const string KeyConfirmBody    = "am.confirm.body";
    private const string KeyConfirmBless   = "am.confirm.bodyBlessing";
    private const string KeyPickerCaption  = "am.picker.caption";
    private const string KeyPickerSearch   = "am.picker.search";
    private const string KeyPickerMin      = "am.picker.min";
    private const string KeyPickerMinHint  = "am.picker.minHint";
    private const string KeyPickerLegend   = "am.picker.legendTail";
    private const string KeyPickerAdd      = "am.picker.add";
    private const string KeyPickerAddNone  = "am.picker.addNone";
    private const string KeyPickerEmpty    = "am.picker.empty";
    private const string KeyPickerLoading  = "am.picker.loading";
    // Reachability (spec ⑥, Plugin.Reach.cs)
    private const string KeyNeedsLv          = "am.targets.needsLv";
    private const string KeyLocked           = "am.targets.locked";
    private const string KeyReachNone        = "am.reach.noneNow";
    private const string KeyReachNoneBless   = "am.reach.noneNowBlessing";
    private const string KeyReachConfirmOne  = "am.reach.confirmOne";
    private const string KeyReachConfirmMany = "am.reach.confirmMany";
    private const string KeyReachItemLv      = "am.reach.itemLv";
    private const string KeyReachItemLocked  = "am.reach.itemLocked";
    private const string KeyLogLevelUp       = "am.footer.levelUp";
    private const string KeyLogLevelUpReach  = "am.footer.levelUpReach";
    private const string KeyEndNeedOne       = "am.end.needOne";
    private const string KeyEndNeedMany      = "am.end.needMany";
    private const string KeyEndLockedOne     = "am.end.lockedOne";
    private const string KeyEndLockedMany    = "am.end.lockedMany";
    private const string KeyOddsOn           = "am.picker.oddsOn";
    private const string KeyOddsOff          = "am.picker.oddsOff";
    private const string KeyHintBoth         = "am.picker.hintBoth";
    private const string KeyHintBless        = "am.picker.hintBlessing";
    private const string KeyHintNormal       = "am.picker.hintNoBlessing";
    private const string KeyHintNowBless     = "am.picker.hintNowBlessing";
    private const string KeyHintLocked       = "am.picker.hintLocked";
    // Keep better rolls (spec ⑦)
    private const string KeyKeepBetter       = "am.run.keepBetter";
    private const string KeyKeepBetterHint   = "am.run.keepBetterHint";
    private const string KeyLogBetter        = "am.footer.better";
    private const string KeyLogSkipped       = "am.footer.betterSkipped";
    private const string KeyConfirmBetter    = "am.confirm.keepBetter";
    private const string KeyEndBetterOne     = "am.end.betterOne";
    private const string KeyEndBetterMany    = "am.end.betterMany";
    // Pocket window + discard (spec ⑨, Plugin.Pocket*.cs)
    private const string KeyClose              = "am.ui.close";
    private const string KeyPocketPill         = "am.targets.pocket";
    private const string KeyPocketCaption      = "am.pocket.caption";
    private const string KeyPocketPanel        = "am.pocket.panel";
    private const string KeyPocketHint         = "am.pocket.hint";
    private const string KeyPocketHintRunning  = "am.pocket.hintRunning";
    private const string KeyPocketFree         = "am.pocket.freeSlot";
    private const string KeyPocketTarget       = "am.pocket.target";
    private const string KeyPocketUnlockOne    = "am.pocket.unlockOne";
    private const string KeyPocketUnlockMany   = "am.pocket.unlockMany";
    private const string KeyDiscardTitle       = "am.pocket.discardTitle";
    private const string KeyDiscardBody        = "am.pocket.discardBody";
    private const string KeyDiscardWaiting     = "am.pocket.discardWaiting";
    private const string KeyDiscardNext        = "am.pocket.discardNext";
    private const string KeyDiscard            = "am.pocket.discard";

    // Category chips, in picker order; the game's own Lang('SeasonTalentAttrLibName_<id>') wins when it exists.
    private static readonly int[] CategoryLibIds = { 1001, 101, 102, 103, 104, 105 };
    private static readonly string[] CategoryKeys =
        { "am.cat.class", "am.cat.secondary", "am.cat.primary", "am.cat.polarity", "am.cat.resilience", "am.cat.element" };

    // Quality 2..5 (AffixQuality): fixed semantic colours from the approved prototype + their names.
    private static readonly ColorRgba[] QualityColors =
    {
        Hex(0x8f, 0xb4, 0xff),   // 2 Superior
        Hex(0xab, 0x85, 0xf6),   // 3 Excellent
        Hex(0xee, 0xd3, 0x35),   // 4 Epic
        Hex(0xef, 0x8e, 0x60),   // 5 Immortal
    };
    private static readonly string[] QualityKeys = { "am.q.superior", "am.q.excellent", "am.q.epic", "am.q.immortal" };

    private static ColorRgba Hex(int r, int g, int b) => new(r / 255f, g / 255f, b / 255f);

    private ColorRgba QualityColor(int quality) => QualityColors[Math.Clamp(quality - 2, 0, QualityColors.Length - 1)];

    private int _langGen;   // bumped on every live language switch

    private void OnLanguageChanged()
    {
        _langGen++;
        _mainWindow?.MarkDirty();
        _pickerWindow?.MarkDirty();
        _pocketWindow?.MarkDirty();
    }

    // A string composed from catalog text, rebuilt only when _langGen moves (or Invalidate is called).
    private sealed class LocComposed
    {
        private readonly Func<string> _build;
        private int _gen = -1;
        private string _s = string.Empty;

        public LocComposed(Func<string> build) => _build = build;

        public string Get(int gen)
        {
            if (gen != _gen) { _gen = gen; _s = _build(); }
            return _s;
        }

        public void Invalidate() => _gen = -1;
    }

    // Section captions are drawn UPPERCASE (house style); upper-case the catalog text once per language.
    private Func<string> UpperLoc(string key)
    {
        var cache = new LocComposed(() => _loc.T(key).ToUpperInvariant());
        return () => cache.Get(_langGen);
    }

    // "1,200" — group separators like the approved prototype.
    private static string N0(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

    // Category name: the game's own label when it has one, else the plugin's catalog.
    private string CategoryName(int index)
        => index >= 0 && index < _gameCategoryNames.Length && _gameCategoryNames[index].Length > 0
            ? _gameCategoryNames[index] : _loc.T(CategoryKeys[index]);

    private string CategoryNameForLib(int libId)
    {
        int i = Array.IndexOf(CategoryLibIds, libId);
        return i >= 0 ? CategoryName(i) : string.Empty;
    }

    // Material names from the game's ItemTable (client language), with a catalog fallback until they are read.
    private string RemnantName() => _remnantName.Length > 0 ? _remnantName : _loc.T(KeyMatRemnant);
    private string PrismName()   => _prismName.Length > 0 ? _prismName : _loc.T(KeyMatPrism);
}
