using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Plugins;
using Stellar.Abstractions.Services;

namespace Stellar.AutoManifest;

/// <summary>
/// The Add-target picker (approved ②): a title-bar-less child window that opens centred on the main window, in front.
///   ADD TARGET caption + icon-only ✕
///   category chips: Class · Secondary · Primary · Polarity · Resilience · Element (Class = current looting class only)
///   search ("Search imprints…")
///   imprint list: "Name · category" + muted full range; selected row = accent outline
///   MINIMUM VALUE (hint "every value the game can roll"): one chip per rollable step, coloured by quality
///   legend "Superior · Excellent · Epic · Immortal — picks this value or higher"
///   Primary "Add “Name ≥ v”" + Neutral Cancel
/// Widened to 520 (house rule 18) so the 11 value chips fit one line at a readable size. Rows / chips:
/// <c>Plugin.Picker.Rows.cs</c>. The filtered list is rebuilt on a category / search / class / catalogue change only.
/// </summary>
public sealed partial class Plugin
{
    private const float PickerWidth   = 520f;   // content = 496
    private const float PickerContentW = PickerWidth - 24f;
    private const float CategoryChipW = 79f;    // 6 × 79 + 5 × 4 = 494
    private const float CancelPillW   = 96f;

    private int _pickCategory;                    // index into CategoryLibIds (0 = Class)
    private string _pickSearch = string.Empty;
    private readonly List<Imprint> _pickFiltered = new();
    private Imprint? _pickSelected;
    private int _pickStep;
    private bool _pickResetScroll;
    private readonly HoverFlag _pickCloseHover = new();

    private IWindowControl RegisterPicker() => _services.Windows.Register(new WindowRegistration(
        new WindowSpec(
            Id:          "automanifest.picker",
            Title:       _loc.T(KeyPickerCaption),
            DefaultRect: new WindowRect(200f, 160f, PickerWidth, 0f),
            Category:    WindowCategory.Tools,
            Style:       WindowPanelStyle.GlassMenu)
        {
            StartVisible = false, Closable = true, Draggable = true,
            ShowTitleBar = false,   // the caption row carries the title and the ✕; the whole frame drags
            TitleProvider = () => _loc.T(KeyPickerCaption),
            Prebuild = true,        // the list reports 0 rows while hidden, so the hidden build binds nothing
            ShouldRender = () => _services.ClientState.Phase == GamePhase.World
                                 && (_services.ClientState.UiState & GameUIState.Loading) == 0,
        },
        BuildPickerRoot(),
        OnClose: ClosePicker));

    private HudElement BuildPickerRoot() => new ColumnElement(new HudElement[]
    {
        new RowElement(new HudElement[]
        {
            new TextElement(UpperLoc(KeyPickerCaption), Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new SpacerElement(0f),
            CrossButton(() => _pickCloseHover.On, over => _pickCloseHover.On = over, ClosePicker, WindowBg, danger: false),
        }, Gap: 8f),
        BuildCategoryChips(),
        new RowElement(new HudElement[]
        {
            new CellElement(new InputElement(Get: () => _pickSearch, Submit: SetPickSearch, Width: PickerContentW, OnChange: SetPickSearch)
                { SingleLine = true, Placeholder = () => _loc.T(KeyPickerSearch) }, Weight: 1f),
        }, Gap: 0.01f),
        BuildPickerList(),
        new RowElement(new HudElement[]
        {
            new TextElement(UpperLoc(KeyPickerMin), Color: () => TextColor(), NoWrap: true, FontSize: 12) { Bold = true },
            new CellElement(SectionHint(() => _loc.T(KeyPickerMinHint), MutedColor), Weight: 1f),
        }, Gap: 8f),
        BuildStepChips(),
        BuildLegend(),
        new RowElement(new HudElement[]
        {
            PillButton(AddLabel, PillKind.Primary, AddFromPicker, PickerContentW - CancelPillW - 8f, WindowBg,
                () => _pickSelected != null && !Running),
            PillButton(() => _loc.T(KeyCancel), PillKind.Neutral, ClosePicker, CancelPillW, WindowBg),
        }, Gap: 8f),
    }, Gap: 8f);

    private HudElement BuildCategoryChips()
    {
        var chips = new HudElement[CategoryLibIds.Length];
        for (int i = 0; i < chips.Length; i++)
        {
            int c = i;
            chips[i] = ChoiceChip(() => CategoryName(c), () => _pickCategory == c, () => SetPickCategory(c), CategoryChipW, WindowBg);
        }
        return new RowElement(chips, Gap: 4f);
    }

    // "Superior · Excellent · Epic · Immortal — picks this value or higher": each quality in its own colour.
    private HudElement BuildLegend()
    {
        var parts = new List<HudElement>();
        for (int q = 0; q < QualityKeys.Length; q++)
        {
            int k = q;
            if (q > 0) parts.Add(new TextElement(() => "·", Color: () => MutedColor(), NoWrap: true, FontSize: 12) { RichEmoji = true });
            parts.Add(new TextElement(() => _loc.T(QualityKeys[k]), Color: () => QualityColors[k], NoWrap: true, FontSize: 12) { Bold = true });
        }
        parts.Add(new CellElement(SmallText(() => _loc.T(KeyPickerLegend), MutedColor), Weight: 1f));
        return new RowElement(parts, Gap: 4f);
    }

    // ── open / close / add ────────────────────────────────────────────────────────────────────────────────────────
    private void OpenPicker()
    {
        if (Running || !_catalogReady) return;
        _pickSelected = null;
        _pickStep = 0;
        RefilterPicker();
        _pickerCenterer.OpenCentred();
        _pickerWindow.MarkDirty();
    }

    private void ClosePicker()
    {
        _pickerCenterer?.Stop();
        if (_pickerWindow is { IsShown: true }) _pickerWindow.SetVisible(false);
    }

    private void AddFromPicker()
    {
        if (_pickSelected is not { } imp || Running) return;
        AddTarget(imp.Group, Math.Clamp(_pickStep, 0, Math.Max(0, imp.Steps.Length - 1)));
        ClosePicker();
    }

    private void SelectImprint(Imprint imp)
    {
        if (!ReferenceEquals(_pickSelected, imp)) _pickStep = 0;
        _pickSelected = imp;
        _pickerWindow.MarkDirty();
    }

    private void SetPickCategory(int c)
    {
        if (_pickCategory == c) return;
        _pickCategory = c;
        RefilterPicker();
    }

    private void SetPickSearch(string s)
    {
        s ??= string.Empty;
        if (s == _pickSearch) return;
        _pickSearch = s;
        RefilterPicker();
    }

    // Class = imprints of the current looting class only; the other chips = the attribute library of that category.
    private void RefilterPicker()
    {
        _pickFiltered.Clear();
        if (_catalog != null)
        {
            int lib = CategoryLibIds[Math.Clamp(_pickCategory, 0, CategoryLibIds.Length - 1)];
            var q = _pickSearch.Trim().ToLowerInvariant();
            foreach (var imp in _catalog.All)
            {
                if (imp.AttrLibId != lib) continue;
                if (lib == 1001 ? imp.Profession != _st.Profession : imp.Profession != 0) continue;
                if (q.Length > 0 && imp.NameLower.IndexOf(q, StringComparison.Ordinal) < 0) continue;
                _pickFiltered.Add(imp);
            }
        }
        _pickResetScroll = true;
        _pickerWindow?.MarkDirty();
    }

    // Add “Great Crimson Lotus ≥ 2.24%” — rebuilt when the selection, the step or the language moves.
    private Imprint? _addLabelImp;
    private int _addLabelStep = -1, _addLabelGen = -1;
    private string _addLabel = string.Empty;

    private string AddLabel()
    {
        if (_pickSelected is not { } imp) return _loc.T(KeyPickerAddNone);
        if (ReferenceEquals(imp, _addLabelImp) && _pickStep == _addLabelStep && _langGen == _addLabelGen) return _addLabel;
        _addLabelImp = imp; _addLabelStep = _pickStep; _addLabelGen = _langGen;
        var opt = _pickStep >= 0 && _pickStep < imp.MinOptions.Length ? imp.MinOptions[_pickStep] : string.Empty;
        return _addLabel = _loc.TFormat(KeyPickerAdd, imp.Name, opt);
    }
}
