using System;
using Stellar.Abstractions.Services;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Stellar.AutoManifest;

/// <summary>
/// RUN row "Spend at most (Remnants)" as a typed number (spec ⑪): a right-aligned 96 px field, digits only, shown with
/// thousands separators, committed on Enter or focus loss (empty = 0), 0 = no limit (muted "0 = no limit" hint left of
/// the box), locked while a run is going. Same config key as the old dropdown (<c>spendLimit</c>, an int).
/// <para>Toolkit limits behind the plumbing (WindowBuilder-Patterns.md):</para>
/// <list type="bullet">
/// <item><see cref="InputElement"/> submits on Enter only — there is no blur callback and no focus readback. So while a
/// draft is open the plugin resolves the live uGUI field from the EventSystem (it is the selected object while the user
/// types) and the 10 Hz tick watches its <c>isFocused</c>: one native read per tick, no allocation, nothing at all while
/// not editing.</item>
/// <item>⚠️ The field's TYPE depends on the framework that runs, not on the SDK we build against: up to 2.33 a plain
/// InputElement is the legacy <see cref="InputField"/>, from 2.34.0 (#169, Thai text) it is ALWAYS a
/// <see cref="TMP_InputField"/>. Looking for the legacy type only found nothing on 2.34 (in-game 2026-11-22: the typed
/// limit committed on Enter only), so both are matched.</item>
/// <item><c>Get</c> must return the RAW draft while editing, never the formatted number, or the framework re-seeds
/// "1,200" mid-typing (Position 2.1.0 finding). It flips back to the formatted value on commit.</item>
/// <item>A rejected character can't be removed through <c>Get</c>: the field binding diffs <c>Get()</c> against its last
/// value, and dropping the "a" of "12a" leaves <c>Get()</c> at "12" both times, so the field would keep showing "12a".
/// The resolved field's text is written directly instead.</item>
/// </list>
/// </summary>
public sealed partial class Plugin
{
    private const float  SpendInputW         = 96f;
    private const float  SpendHintW          = 110f;
    private const int    SpendMaxDigits      = 9;    // 999,999,999 < int.MaxValue
    private const int    SpendLookupMaxTries = 10;   // ~1 s of 10 Hz ticks; still not found → warn once and stop looking
    private const string MainWindowId        = "automanifest.main";   // the main WindowSpec Id = its root's name

    private string? _spendDraft;          // digits typed since the last commit; null = not editing (the field shows SpendText)
    private InputField? _spendField;      // the live field, resolved while a draft is open: legacy (fw ≤ 2.33) …
    private TMP_InputField? _spendTmp;    // … or TMP (fw ≥ 2.34). At most one is set.
    private int _spendLookupTries;        // failed lookups for the open draft (keystrokes + ticks)
    private bool _spendFieldWarned;
    private bool _spendCleaning;          // our own write of the cleaned text echoes through OnChange
    private int _spendTextFor = -1;
    private string _spendText = string.Empty;

    private bool SpendFieldResolved => _spendField != null || _spendTmp != null;

    // "1,200", re-formatted only when the committed value changes.
    private string SpendText()
    {
        if (_spendLimit == _spendTextFor) return _spendText;
        _spendTextFor = _spendLimit;
        return _spendText = N0(_spendLimit);
    }

    private HudElement SpendControl() => new RowElement(new HudElement[]
    {
        // The hint cell is always there (a strut when the value isn't 0), so the box never moves.
        new CellElement(new ConditionalElement(() => _spendLimit == 0,
            SectionHint(() => _loc.T(KeySpendNoLimit), MutedColor),
            new SpacerElement(SpendHintW, 0f)), Width: SpendHintW),
        new CellElement(new InputElement(() => _spendDraft ?? SpendText(), OnSpendSubmit, Width: SpendInputW, OnChange: OnSpendEdit)
            {
                SingleLine = true,         // fixed 28 px box; Enter submits and ends editing without opening the game chat
                Locked = () => Running,    // no edits mid-run: read-only + dimmed (the toolkit's disabled field)
            }, Width: SpendInputW),
    }, Gap: 8f);

    private void OnSpendEdit(string s)
    {
        if (_spendCleaning || Running) return;
        s ??= string.Empty;
        if (_spendDraft == null && s == SpendText()) return;   // the re-seed echo after a commit, not an edit
        if (_spendDraft == null) _spendLookupTries = 0;
        // Every keystroke retries until found (a keystroke is the moment the field is certainly selected); the tick
        // retries too, for a draft that began on an echo rather than a keystroke.
        if (!SpendFieldResolved) TryResolveSpendField();
        var clean = SpendDigits(s);
        _spendDraft = clean;
        if (clean.Length == s.Length || !SpendFieldResolved) return;
        _spendCleaning = true;
        try
        {
            if (_spendTmp != null) { _spendTmp.text = clean; _spendTmp.caretPosition = clean.Length; }
            else if (_spendField != null) { _spendField.text = clean; _spendField.caretPosition = clean.Length; }
        }
        catch (Exception ex) { _services.Log.Warning($"[AutoManifest] spend field clean failed: {ex.Message}"); }
        finally { _spendCleaning = false; }
    }

    private void OnSpendSubmit(string s)
    {
        if (Running) return;
        _spendDraft = SpendDigits(s ?? string.Empty);
        CommitSpendDraft();
    }

    // Empty = 0. The draft is digits only and at most 9 long, so the parse can't fail or overflow.
    private void CommitSpendDraft()
    {
        if (_spendDraft == null) return;
        var d = _spendDraft;
        _spendDraft = null; _spendField = null; _spendTmp = null; _spendLookupTries = 0;
        SetSpendLimit(d.Length == 0 ? 0 : int.Parse(d, System.Globalization.CultureInfo.InvariantCulture));
        MarkMain();   // Get() flips back to the formatted value → the field re-seeds "1,200" even when the value didn't move
    }

    // 10 Hz, only while a draft is open: focus gone (click-away, Esc, window closed) = commit. A click elsewhere
    // deselects the field and uGUI's OnDeselect deactivates it (both field types), so isFocused alone covers "the
    // selection moved" without reading currentSelectedGameObject (a fresh interop wrapper) on every tick.
    private void SpendDraftTick()
    {
        if (!SpendFieldResolved)
        {
            if (_spendLookupTries < SpendLookupMaxTries) { TryResolveSpendField(); return; }   // found → poll from the next tick
            if (_spendFieldWarned) return;
            _spendFieldWarned = true;   // once a session; Enter / Start / closing the window still commit
            _services.Log.Warning("[AutoManifest] spend field not found: the typed limit commits on Enter only");
            return;
        }
        bool focused;
        try { focused = _spendTmp != null ? _spendTmp.isFocused : _spendField != null && _spendField.isFocused; }
        catch { focused = false; }
        if (!focused) CommitSpendDraft();
    }

    // The field the user is typing in is the EventSystem's selected object (UGuiTextInput selects it on focus). Match
    // either field type, focused, under the main window's root (the framework names it after the WindowSpec Id, so the
    // picker's search box can never be taken for it). Runs only while a draft is open and unresolved.
    private bool TryResolveSpendField()
    {
        try
        {
            var es = EventSystem.current;
            var go = es != null ? es.currentSelectedGameObject : null;
            if (go != null && IsUnderMainWindow(go.transform))
            {
                var tmp = go.GetComponent<TMP_InputField>();
                if (tmp != null && tmp.isFocused) { _spendTmp = tmp; return true; }
                var f = go.GetComponent<InputField>();
                if (f != null && f.isFocused) { _spendField = f; return true; }
            }
        }
        catch (Exception ex)
        {
            if (!_spendFieldWarned) _services.Log.Warning($"[AutoManifest] spend field lookup failed: {ex.Message}");
            _spendFieldWarned = true;
        }
        _spendLookupTries++;
        return false;
    }

    private static bool IsUnderMainWindow(Transform? t)
    {
        for (; t != null; t = t.parent)
            if (t.name == MainWindowId) return true;
        return false;
    }

    private static string SpendDigits(string s)
    {
        int n = 0;
        foreach (var ch in s) if (ch >= '0' && ch <= '9') n++;
        if (n == s.Length && n <= SpendMaxDigits) return s;
        var buf = new char[Math.Min(n, SpendMaxDigits)];
        int k = 0;
        foreach (var ch in s)
            if (ch >= '0' && ch <= '9' && k < buf.Length) buf[k++] = ch;
        return new string(buf);
    }
}
