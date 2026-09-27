using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.Dialogs;

/// <summary>
///     A dialog for creating and editing smart playlists.
/// </summary>
public sealed partial class SmartPlaylistEditorDialog : ContentDialog
{
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly ILogger<SmartPlaylistEditorDialog> _logger;
    private readonly Debouncer _matchCountDebouncer = new(TimeSpan.FromMilliseconds(300));
    private readonly IDispatcherService _dispatcherService;
    private string? _selectedCoverImageUri;
    private bool _isInitialized;

    /// <summary>
    ///     Gets or sets the smart playlist being edited (null for new playlists).
    /// </summary>
    public SmartPlaylist? EditingPlaylist { get; set; }

    /// <summary>
    ///     Gets the rules for the smart playlist.
    /// </summary>
    public ObservableCollection<RuleViewModel> Rules { get; } = new();

    public SmartPlaylist? ResultPlaylist { get; private set; }

    public SmartPlaylistEditorDialog()
    {
        _smartPlaylistService = App.Services!.GetRequiredService<ISmartPlaylistService>();
        _logger = App.Services!.GetRequiredService<ILogger<SmartPlaylistEditorDialog>>();
        _dispatcherService = App.Services!.GetRequiredService<IDispatcherService>();

        InitializeComponent();

        DialogThemeHelper.ApplyThemeOverrides(this);

        Rules.CollectionChanged += OnRulesCollectionChanged;
        Closed += OnDialogClosed;

        _isInitialized = true;
        MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Calculating;
    }

    private void OnRulesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateNoRulesVisibility();

        if (e.NewItems != null)
        {
            foreach (RuleViewModel rule in e.NewItems)
            {
                rule.PropertyChanged += OnRulePropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (RuleViewModel rule in e.OldItems)
            {
                rule.PropertyChanged -= OnRulePropertyChanged;
            }
        }

        _ = UpdateMatchCountAsync();
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {

        if (e.PropertyName is nameof(RuleViewModel.Value) or
            nameof(RuleViewModel.SecondValue) or
            nameof(RuleViewModel.SelectedField) or
            nameof(RuleViewModel.SelectedOperator))
        {
            _ = UpdateMatchCountAsync();
        }
    }

    private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _isInitialized = false;
        _matchCountDebouncer.Dispose();

        Rules.CollectionChanged -= OnRulesCollectionChanged;

        foreach (var rule in Rules)
        {
            rule.PropertyChanged -= OnRulePropertyChanged;
        }
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("SmartPlaylistEditorDialog loaded. EditingPlaylist: {EditingPlaylistId}",
            EditingPlaylist?.Id.ToString() ?? "null (creating new)");

        if (EditingPlaylist != null)
        {
            Title = ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SmartPlaylist_Title_EditFormat, EditingPlaylist.Name);
            PlaylistNameTextBox.Text = EditingPlaylist.Name;
            MatchLogicComboBox.SelectedIndex = EditingPlaylist.MatchAllRules ? 0 : 1;

            SelectSortByComboBoxItem(EditingPlaylist.SortOrder);

            if (!string.IsNullOrWhiteSpace(EditingPlaylist.CoverImageUri))
            {
                _selectedCoverImageUri = EditingPlaylist.CoverImageUri;
                CoverImagePreview.Source = ImageUriHelper.SafeGetImageSource(ImageUriHelper.GetUriWithCacheBuster(EditingPlaylist.CoverImageUri));
                CoverImagePreview.Visibility = Visibility.Visible;
                CoverImagePlaceholder.Visibility = Visibility.Collapsed;
            }

            foreach (var rule in EditingPlaylist.Rules.OrderBy(r => r.Order))
                Rules.Add(new RuleViewModel(rule));
        }
        else
        {
            Title = Nagi.WinUI.Resources.Strings.SmartPlaylist_Title_New;
        }

        UpdateNoRulesVisibility();
        MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Calculating;
        _ = UpdateMatchCountAsync();
    }

    private void SelectSortByComboBoxItem(SmartPlaylistSortOrder sortOrder)
    {
        SortByComboBox.SelectedItem = SortByComboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == sortOrder.ToString());
    }

    private void OnPlaylistNameChanged(object sender, TextChangedEventArgs e)
    {

        _ = UpdateMatchCountAsync();
    }

    private void OnComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {

        _ = UpdateMatchCountAsync();
    }

    private async void PickCoverImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("Opening file picker for cover image.");
            var file = await App.Services!.GetRequiredService<IUIService>()
                .PickSingleFileAsync(FileExtensions.ImageFileExtensions);
            if (file != null)
            {
                _logger.LogDebug("User picked image file: {FilePath}", file);
                _selectedCoverImageUri = file;
                CoverImagePreview.Source = ImageUriHelper.SafeGetImageSource(ImageUriHelper.GetUriWithCacheBuster(file));
                CoverImagePreview.Visibility = Visibility.Visible;
                CoverImagePlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error picking cover image");
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Adding new rule");
        Rules.Add(new RuleViewModel());

    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleViewModel rule })
        {
            _logger.LogDebug("Removing rule");
            Rules.Remove(rule);

        }
    }

    private void UpdateNoRulesVisibility()
    {
        NoRulesPanel.Visibility = Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Task UpdateMatchCountAsync()
    {
        // XAML selection events can fire during InitializeComponent.
        if (!_isInitialized) return Task.CompletedTask;

        return _matchCountDebouncer.RunAsync(async token =>
        {
            try
            {
                var playlist = await _dispatcherService.EnqueueAsync(() =>
                    token.IsCancellationRequested ? null : BuildSmartPlaylistFromUI());
                token.ThrowIfCancellationRequested();
                // SQLite executes count queries synchronously, so keep them off the UI thread.
                int? count = playlist == null ? null
                    : await Task.Run(() => _smartPlaylistService.GetMatchingSongCountAsync(playlist, token: token), token);

                _dispatcherService.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    MatchCountText.Text = count.HasValue
                        ? ResourceFormatter.Format(Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_MatchCountFormat, count.Value)
                        : Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_EnterName;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error calculating match count");
                _dispatcherService.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested)
                        MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Error;
                });
            }
        });
    }

    private SmartPlaylist? BuildSmartPlaylistFromUI()
    {
        var name = PlaylistNameTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var playlist = new SmartPlaylist
        {
            Id = EditingPlaylist?.Id ?? Guid.NewGuid(),
            Name = name,
            MatchAllRules = MatchLogicComboBox.SelectedIndex == 0,
            SortOrder = GetSelectedSortOrder(),
            CoverImageUri = _selectedCoverImageUri,
            Description = EditingPlaylist?.Description,
            DateCreated = EditingPlaylist?.DateCreated ?? DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };

        var orderIndex = 0;
        foreach (var ruleVm in Rules)
        {
            var rule = ruleVm.ToSmartPlaylistRule();
            rule.SmartPlaylistId = playlist.Id;
            rule.Order = orderIndex++;
            playlist.Rules.Add(rule);
        }

        return playlist;
    }

    private SmartPlaylistSortOrder GetSelectedSortOrder()
    {
        if (SortByComboBox.SelectedItem is ComboBoxItem selected &&
            Enum.TryParse<SmartPlaylistSortOrder>(selected.Tag?.ToString(), out var sortOrder))
        {
            return sortOrder;
        }
        return SmartPlaylistSortOrder.TitleAsc;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        _matchCountDebouncer.Cancel();

        try
        {
            var playlist = BuildSmartPlaylistFromUI();
            if (playlist is null)
            {
                args.Cancel = true;
                PlaylistNameTextBox.Focus(FocusState.Programmatic);
                PlaylistNameTextBox.PlaceholderText = Nagi.WinUI.Resources.Strings.SmartPlaylist_Error_NameEmptyFeedback;
                return;
            }

            ResultPlaylist = EditingPlaylist is null
                ? await _smartPlaylistService.CreateSmartPlaylistAsync(playlist)
                : await _smartPlaylistService.UpdateSmartPlaylistAsync(playlist);
            if (ResultPlaylist is null)
            {
                args.Cancel = true;
                MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Error;
            }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx) when (dbEx.InnerException is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            _logger.LogWarning(dbEx, "Duplicate smart playlist name");
            args.Cancel = true;
            PlaylistNameTextBox.Focus(FocusState.Programmatic);
            MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Error_DuplicateName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving smart playlist");
            args.Cancel = true;
            MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Error;
        }
        finally
        {
            deferral.Complete();
        }
    }
}

/// <summary>
///     ViewModel for a single smart playlist rule in the editor.
/// </summary>
public class RuleViewModel : ObservableObject
{
    public static IReadOnlyList<FieldOption> AvailableFields { get; } = new List<FieldOption>
    {
        new(SmartPlaylistField.Title, Nagi.Core.Resources.Strings.Label_Title),
        new(SmartPlaylistField.Artist, Nagi.Core.Resources.Strings.Label_Artist),
        new(SmartPlaylistField.Album, Nagi.Core.Resources.Strings.Label_Album),
        new(SmartPlaylistField.Genre, Nagi.Core.Resources.Strings.Label_Genre),
        new(SmartPlaylistField.Year, Nagi.Core.Resources.Strings.Label_Year),
        new(SmartPlaylistField.Rating, Nagi.WinUI.Resources.Strings.Label_Rating),
        new(SmartPlaylistField.Duration, Nagi.Core.Resources.Strings.Label_Duration),
        new(SmartPlaylistField.Bpm, Nagi.Core.Resources.Strings.Label_Bpm),
        new(SmartPlaylistField.Comment, Nagi.WinUI.Resources.Strings.Label_Comment),
        new(SmartPlaylistField.Composer, Nagi.WinUI.Resources.Strings.Label_Composer),
        new(SmartPlaylistField.Grouping, Nagi.WinUI.Resources.Strings.Label_Grouping),
        new(SmartPlaylistField.PlayCount, Nagi.Core.Resources.Strings.Label_PlayCount),
        new(SmartPlaylistField.SkipCount, Nagi.WinUI.Resources.Strings.Label_SkipCount),
        new(SmartPlaylistField.TrackNumber, Nagi.Core.Resources.Strings.Label_TrackNumber),
        new(SmartPlaylistField.DiscNumber, Nagi.WinUI.Resources.Strings.Label_DiscNumber),
        new(SmartPlaylistField.Bitrate, Nagi.WinUI.Resources.Strings.Label_Bitrate),
        new(SmartPlaylistField.SampleRate, Nagi.WinUI.Resources.Strings.Label_SampleRate),
        new(SmartPlaylistField.IsLoved, Nagi.WinUI.Resources.Strings.Label_IsLoved),
        new(SmartPlaylistField.HasLyrics, Nagi.WinUI.Resources.Strings.Label_HasLyrics),
        new(SmartPlaylistField.DateAdded, Nagi.Core.Resources.Strings.Label_DateAdded),
        new(SmartPlaylistField.LastPlayed, Nagi.Core.Resources.Strings.Label_LastPlayed),
        new(SmartPlaylistField.FileCreatedDate, Nagi.Core.Resources.Strings.Label_DateCreated),
        new(SmartPlaylistField.FileModifiedDate, Nagi.Core.Resources.Strings.Label_DateModified)
    };

    private static readonly IReadOnlyList<OperatorOption> TextOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Contains, Nagi.WinUI.Resources.Strings.Operator_Contains),
        new(SmartPlaylistOperator.DoesNotContain, Nagi.WinUI.Resources.Strings.Operator_DoesNotContain),
        new(SmartPlaylistOperator.Is, Nagi.WinUI.Resources.Strings.Operator_Is),
        new(SmartPlaylistOperator.IsNot, Nagi.WinUI.Resources.Strings.Operator_IsNot),
        new(SmartPlaylistOperator.StartsWith, Nagi.WinUI.Resources.Strings.Operator_StartsWith),
        new(SmartPlaylistOperator.EndsWith, Nagi.WinUI.Resources.Strings.Operator_EndsWith)
    };

    private static readonly IReadOnlyList<OperatorOption> NumericOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Equals, Nagi.WinUI.Resources.Strings.Operator_Equals),
        new(SmartPlaylistOperator.NotEquals, Nagi.WinUI.Resources.Strings.Operator_NotEquals),
        new(SmartPlaylistOperator.GreaterThan, Nagi.WinUI.Resources.Strings.Operator_GreaterThan),
        new(SmartPlaylistOperator.LessThan, Nagi.WinUI.Resources.Strings.Operator_LessThan),
        new(SmartPlaylistOperator.GreaterThanOrEqual, Nagi.WinUI.Resources.Strings.Operator_GreaterThanOrEqual),
        new(SmartPlaylistOperator.LessThanOrEqual, Nagi.WinUI.Resources.Strings.Operator_LessThanOrEqual),
        new(SmartPlaylistOperator.IsInRange, Nagi.WinUI.Resources.Strings.Operator_IsInRange)
    };

    private static readonly IReadOnlyList<OperatorOption> DateOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsInTheLast, Nagi.WinUI.Resources.Strings.Operator_IsInTheLast),
        new(SmartPlaylistOperator.IsNotInTheLast, Nagi.WinUI.Resources.Strings.Operator_IsNotInTheLast),
        new(SmartPlaylistOperator.GreaterThan, Nagi.WinUI.Resources.Strings.Operator_IsAfter),
        new(SmartPlaylistOperator.LessThan, Nagi.WinUI.Resources.Strings.Operator_IsBefore),
        new(SmartPlaylistOperator.IsInRange, Nagi.WinUI.Resources.Strings.Operator_IsInRange)
    };

    private static readonly IReadOnlyList<OperatorOption> BooleanOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsTrue, Nagi.WinUI.Resources.Strings.Operator_IsTrue),
        new(SmartPlaylistOperator.IsFalse, Nagi.WinUI.Resources.Strings.Operator_IsFalse)
    };

    public IReadOnlyList<FieldOption> AvailableFieldsList => AvailableFields;

    private FieldOption _selectedField = AvailableFields[0];
    private OperatorOption _selectedOperator = TextOperators[0];
    private string _value = string.Empty;
    private string _secondValue = string.Empty;

    public RuleViewModel()
    {
    }

    public RuleViewModel(SmartPlaylistRule rule)
    {
        _selectedField = AvailableFields.First(f => f.Field == rule.Field);
        UpdateOperatorsForField();
        _selectedOperator = AvailableOperators.First(o => o.Operator == rule.Operator);
        _value = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.Value);
        _secondValue = SmartPlaylistRuleValue.ToDisplayValue(rule.Field, rule.SecondValue);
    }

    public IReadOnlyList<OperatorOption> AvailableOperators { get; private set; } = TextOperators;

    public FieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            // ComboBox can clear selection while its items are refreshed.
            if (value != null && SetProperty(ref _selectedField, value))
                UpdateOperatorsForField();
        }
    }

    public OperatorOption SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (value != null && SetProperty(ref _selectedOperator, value))
            {
                OnPropertyChanged(nameof(IsRange));
                OnPropertyChanged(nameof(RequiresValue));
            }
        }
    }

    public bool IsRange => SelectedOperator.Operator == SmartPlaylistOperator.IsInRange;
    public bool RequiresValue => SelectedOperator.Operator is not (SmartPlaylistOperator.IsTrue or SmartPlaylistOperator.IsFalse);

    public string Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }

    public string SecondValue
    {
        get => _secondValue;
        set => SetProperty(ref _secondValue, value);
    }

    private void UpdateOperatorsForField()
    {
        AvailableOperators = _selectedField.Field switch
        {
            SmartPlaylistField.Year or SmartPlaylistField.PlayCount or SmartPlaylistField.SkipCount or
            SmartPlaylistField.Rating or SmartPlaylistField.Duration or SmartPlaylistField.Bpm or
            SmartPlaylistField.TrackNumber or SmartPlaylistField.DiscNumber or SmartPlaylistField.Bitrate or
            SmartPlaylistField.SampleRate => NumericOperators,

            SmartPlaylistField.DateAdded or SmartPlaylistField.LastPlayed or
            SmartPlaylistField.FileCreatedDate or SmartPlaylistField.FileModifiedDate => DateOperators,

            SmartPlaylistField.IsLoved or SmartPlaylistField.HasLyrics => BooleanOperators,

            _ => TextOperators
        };

        OnPropertyChanged(nameof(AvailableOperators));
        SelectedOperator = AvailableOperators.FirstOrDefault(o => o.Operator == _selectedOperator.Operator)
                           ?? AvailableOperators[0];
    }

    public SmartPlaylistRule ToSmartPlaylistRule()
    {
        return new SmartPlaylistRule
        {
            Field = _selectedField.Field,
            Operator = _selectedOperator.Operator,
            Value = SmartPlaylistRuleValue.ToStoredValue(_selectedField.Field, _selectedOperator.Operator, _value),
            SecondValue = string.IsNullOrWhiteSpace(_secondValue) ? null
                : SmartPlaylistRuleValue.ToStoredValue(_selectedField.Field, _selectedOperator.Operator, _secondValue)
        };
    }

}

public record FieldOption(SmartPlaylistField Field, string DisplayName);

public record OperatorOption(SmartPlaylistOperator Operator, string DisplayName);
