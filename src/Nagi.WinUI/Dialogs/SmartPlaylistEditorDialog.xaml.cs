using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage.Pickers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nagi.Core.Constants;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using WinRT.Interop;

namespace Nagi.WinUI.Dialogs;

/// <summary>
///     A dialog for creating and editing smart playlists.
/// </summary>
public sealed partial class SmartPlaylistEditorDialog : ContentDialog
{
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly ILogger<SmartPlaylistEditorDialog> _logger;
    private CancellationTokenSource? _matchCountCts;
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

    /// <summary>
    ///     Gets or sets the resulting smart playlist after save.
    /// </summary>
    public SmartPlaylist? ResultPlaylist { get; private set; }

    public SmartPlaylistEditorDialog()
    {
        _smartPlaylistService = App.Services!.GetRequiredService<ISmartPlaylistService>();
        _logger = App.Services!.GetRequiredService<ILogger<SmartPlaylistEditorDialog>>();

        InitializeComponent();
        
        // Apply app theme overrides for TextBox styling inside ContentDialog
        DialogThemeHelper.ApplyThemeOverrides(this);
        
        Rules.CollectionChanged += OnRulesCollectionChanged;
        Unloaded += OnDialogUnloaded;
        
        _isInitialized = true;
        MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Calculating;
    }

    private void OnRulesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateNoRulesVisibility();
        
        // Subscribe to PropertyChanged for new rules
        if (e.NewItems != null)
        {
            foreach (RuleViewModel rule in e.NewItems)
            {
                rule.PropertyChanged += OnRulePropertyChanged;
            }
        }
        
        // Unsubscribe from removed rules
        if (e.OldItems != null)
        {
            foreach (RuleViewModel rule in e.OldItems)
            {
                rule.PropertyChanged -= OnRulePropertyChanged;
            }
        }

        // Addition or removal of rules affects the match count
        _ = UpdateMatchCountAsync();
    }

    private void OnRulePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Update match count when any rule property changes
        // Debouncing is already handled in UpdateMatchCountAsync (300ms delay)
        if (e.PropertyName is nameof(RuleViewModel.Value) or 
            nameof(RuleViewModel.SecondValue) or
            nameof(RuleViewModel.SelectedField) or 
            nameof(RuleViewModel.SelectedOperator))
        {
            _ = UpdateMatchCountAsync();
        }
    }

    private void OnDialogUnloaded(object sender, RoutedEventArgs e)
    {
        _matchCountCts?.Cancel();
        _matchCountCts?.Dispose();
        _matchCountCts = null;
        
        // Unsubscribe from collection changed event
        Rules.CollectionChanged -= OnRulesCollectionChanged;
        
        // Unsubscribe from all rule property changes
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
            Title = string.Format(Nagi.WinUI.Resources.Strings.SmartPlaylist_Title_EditFormat, EditingPlaylist.Name);
            PlaylistNameTextBox.Text = EditingPlaylist.Name;
            MatchLogicComboBox.SelectedIndex = EditingPlaylist.MatchAllRules ? 0 : 1;

            SelectSortByComboBoxItem(EditingPlaylist.SortOrder);

            // Load existing cover image
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
        var tagToFind = sortOrder.ToString();
        for (var i = 0; i < SortByComboBox.Items.Count; i++)
        {
            if (SortByComboBox.Items[i] is ComboBoxItem item && item.Tag?.ToString() == tagToFind)
            {
                SortByComboBox.SelectedIndex = i;
                return;
            }
        }
    }


    private void OnPlaylistNameChanged(object sender, TextChangedEventArgs e)
    {
        // Name change affects whether we can calculate match count
        _ = UpdateMatchCountAsync();
    }

    private void OnComboBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Match logic or sort order change affects the query/count
        _ = UpdateMatchCountAsync();
    }

    private async void PickCoverImage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _logger.LogDebug("Opening file picker for cover image.");
            var picker = new FileOpenPicker();
            var hwnd = WindowNative.GetWindowHandle(App.RootWindow);
            InitializeWithWindow.Initialize(picker, hwnd);
            foreach (var ext in FileExtensions.ImageFileExtensions)
                picker.FileTypeFilter.Add(ext);

            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _logger.LogDebug("User picked image file: {FilePath}", file.Path);
                _selectedCoverImageUri = file.Path;
                CoverImagePreview.Source = ImageUriHelper.SafeGetImageSource(ImageUriHelper.GetUriWithCacheBuster(file.Path));
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
        // UpdateMatchCountAsync is triggered by OnRulesCollectionChanged -> OnRulePropertyChanged
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleViewModel rule })
        {
            _logger.LogDebug("Removing rule");
            Rules.Remove(rule);
            // UpdateMatchCountAsync is triggered by OnRulesCollectionChanged
        }
    }


    private void UpdateNoRulesVisibility()
    {
        NoRulesPanel.Visibility = Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task UpdateMatchCountAsync()
    {
        // Guard against early execution before UI is fully initialized.
        // XAML event handlers (e.g., OnComboBoxSelectionChanged) may fire during
        // InitializeComponent() before services are available.
        if (!_isInitialized)
        {
            return;
        }

        // Dispose the old CTS to prevent resource leaks
        _matchCountCts?.Cancel();
        _matchCountCts?.Dispose();
        _matchCountCts = new CancellationTokenSource();
        var token = _matchCountCts.Token;

        try
        {
            await Task.Delay(300, token); // Debounce
            if (token.IsCancellationRequested) return;

            // Build a temporary smart playlist to get the count
            var tempPlaylist = BuildSmartPlaylistFromUI();
            if (tempPlaylist == null)
            {
                MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_EnterName;
                return;
            }

            // Use the temp playlist for both new and existing playlists - it represents the current UI state
            var count = await Task.Run(() => _smartPlaylistService.GetMatchingSongCountAsync(tempPlaylist), token);

            if (token.IsCancellationRequested) return;

            MatchCountText.Text = count >= 0 
                ? string.Format(Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_MatchCountFormat, count) 
                : Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_EnterNameToSeeSongs;
        }
        catch (TaskCanceledException)
        {
            // Ignore - new calculation started
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating match count");
            MatchCountText.Text = Nagi.WinUI.Resources.Strings.SmartPlaylist_Status_Error;
        }
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
            DateCreated = EditingPlaylist?.DateCreated ?? DateTime.UtcNow,
            DateModified = DateTime.UtcNow
        };


        // Add and index rules in a single pass
        var orderIndex = 0;
        foreach (var ruleVm in Rules)
        {
            var rule = ruleVm.ToSmartPlaylistRule();
            if (rule != null)
            {
                rule.SmartPlaylistId = playlist.Id;
                rule.Order = orderIndex++;
                playlist.Rules.Add(rule);
            }
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
        
        try
        {
            var name = PlaylistNameTextBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                args.Cancel = true;
                PlaylistNameTextBox.Focus(FocusState.Programmatic);
                // Visual feedback - highlight the textbox
                PlaylistNameTextBox.PlaceholderText = Nagi.WinUI.Resources.Strings.SmartPlaylist_Error_NameEmptyFeedback;
                return;
            }

            var isEditing = EditingPlaylist != null;
            
            // Build rules list once for both paths
            var newRules = Rules
                .Select(ruleVm => ruleVm.ToSmartPlaylistRule())
                .OfType<SmartPlaylistRule>()
                .ToList();

            if (isEditing)
            {
                // Update existing playlist
                EditingPlaylist!.Name = name;
                EditingPlaylist.MatchAllRules = MatchLogicComboBox.SelectedIndex == 0;
                EditingPlaylist.SortOrder = GetSelectedSortOrder();

                await _smartPlaylistService.UpdateSmartPlaylistAsync(EditingPlaylist);
                
                // Update cover image separately if needed to handle cache properly
                if (!string.Equals(_selectedCoverImageUri, EditingPlaylist.CoverImageUri, StringComparison.Ordinal))
                {
                    await _smartPlaylistService.UpdateSmartPlaylistCoverAsync(EditingPlaylist.Id, _selectedCoverImageUri);
                }

                // Replace all rules in a single transaction
                await _smartPlaylistService.ReplaceAllRulesAsync(EditingPlaylist.Id, newRules);

                // Refresh the playlist from the DB to get the updated rules and properties
                ResultPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(EditingPlaylist.Id);
                _logger.LogInformation("Updated smart playlist {PlaylistId}", EditingPlaylist.Id);
            }
            else
            {
                // Create new playlist
                var newPlaylist = await _smartPlaylistService.CreateSmartPlaylistAsync(name, null, _selectedCoverImageUri);
                if (newPlaylist == null)
                {
                    args.Cancel = true;
                    PlaylistNameTextBox.Focus(FocusState.Programmatic);
                    PlaylistNameTextBox.PlaceholderText = Nagi.WinUI.Resources.Strings.SmartPlaylist_Error_DuplicateName;
                    PlaylistNameTextBox.Text = string.Empty;
                    return;
                }

                // Set configuration
                await _smartPlaylistService.SetMatchAllRulesAsync(newPlaylist.Id, MatchLogicComboBox.SelectedIndex == 0);
                await _smartPlaylistService.SetSortOrderAsync(newPlaylist.Id, GetSelectedSortOrder());

                // Add all rules in a single transaction
                await _smartPlaylistService.ReplaceAllRulesAsync(newPlaylist.Id, newRules);

                ResultPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(newPlaylist.Id);
                _logger.LogInformation("Created smart playlist {PlaylistId}", newPlaylist.Id);
            }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx) when (dbEx.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            _logger.LogWarning(dbEx, "Duplicate smart playlist name");
            args.Cancel = true;
            PlaylistNameTextBox.Focus(FocusState.Programmatic);
            PlaylistNameTextBox.PlaceholderText = Nagi.WinUI.Resources.Strings.SmartPlaylist_Error_DuplicateName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving smart playlist");
            args.Cancel = true;
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
public class RuleViewModel : INotifyPropertyChanged
{
    // Static cached lists to avoid repeated allocations
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
        new(SmartPlaylistField.Composer, Nagi.WinUI.Resources.Strings.Label_Composer)
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
        new(SmartPlaylistOperator.LessThanOrEqual, Nagi.WinUI.Resources.Strings.Operator_LessThanOrEqual)
    };

    private static readonly IReadOnlyList<OperatorOption> DateOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsInTheLast, Nagi.WinUI.Resources.Strings.Operator_IsInTheLast),
        new(SmartPlaylistOperator.IsNotInTheLast, Nagi.WinUI.Resources.Strings.Operator_IsNotInTheLast)
    };

    private static readonly IReadOnlyList<OperatorOption> BooleanOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.IsTrue, Nagi.WinUI.Resources.Strings.Operator_IsTrue),
        new(SmartPlaylistOperator.IsFalse, Nagi.WinUI.Resources.Strings.Operator_IsFalse)
    };

    private static readonly IReadOnlyList<OperatorOption> FallbackOperators = new List<OperatorOption>
    {
        new(SmartPlaylistOperator.Contains, Nagi.WinUI.Resources.Strings.Operator_Contains),
        new(SmartPlaylistOperator.Is, Nagi.WinUI.Resources.Strings.Operator_Is)
    };

    // Cached PropertyChangedEventArgs to avoid repeated allocations
    private static readonly PropertyChangedEventArgs SelectedFieldChangedArgs = new(nameof(SelectedField));
    private static readonly PropertyChangedEventArgs SelectedOperatorChangedArgs = new(nameof(SelectedOperator));
    private static readonly PropertyChangedEventArgs ValueChangedArgs = new(nameof(Value));
    private static readonly PropertyChangedEventArgs SecondValueChangedArgs = new(nameof(SecondValue));
    private static readonly PropertyChangedEventArgs AvailableOperatorsChangedArgs = new(nameof(AvailableOperators));

    // Instance properties for x:Bind access to static cached lists
    public IReadOnlyList<FieldOption> AvailableFieldsList => AvailableFields;
    public IReadOnlyList<OperatorOption> TextOperatorsList => TextOperators;
    public IReadOnlyList<OperatorOption> NumericOperatorsList => NumericOperators;
    public IReadOnlyList<OperatorOption> DateOperatorsList => DateOperators;
    public IReadOnlyList<OperatorOption> BooleanOperatorsList => BooleanOperators;

    private FieldOption _selectedField;
    private OperatorOption _selectedOperator;
    private string _value = string.Empty;
    private string _secondValue = string.Empty;

    public RuleViewModel()
    {
        _selectedField = AvailableFields[0];
        UpdateOperatorsForField();
        _selectedOperator = AvailableOperators.Count > 0 ? AvailableOperators[0] : FallbackOperators[0];
    }

    public RuleViewModel(SmartPlaylistRule rule)
    {
        _selectedField = AvailableFields.FirstOrDefault(f => f.Field == rule.Field) ?? AvailableFields[0];
        UpdateOperatorsForField();
        _selectedOperator = AvailableOperators.FirstOrDefault(o => o.Operator == rule.Operator) ?? AvailableOperators[0];
        _value = rule.Value ?? string.Empty;
        _secondValue = rule.SecondValue ?? string.Empty;
    }

    public IReadOnlyList<OperatorOption> AvailableOperators { get; private set; } = FallbackOperators;

    public FieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            if (_selectedField != value)
            {
                _selectedField = value;
                PropertyChanged?.Invoke(this, SelectedFieldChangedArgs);
                UpdateOperatorsForField();
            }
        }
    }

    public OperatorOption SelectedOperator
    {
        get => _selectedOperator;
        set
        {
            if (_selectedOperator != value)
            {
                _selectedOperator = value;
                PropertyChanged?.Invoke(this, SelectedOperatorChangedArgs);
            }
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value != value)
            {
                _value = value;
                PropertyChanged?.Invoke(this, ValueChangedArgs);
            }
        }
    }

    public string SecondValue
    {
        get => _secondValue;
        set
        {
            if (_secondValue != value)
            {
                _secondValue = value;
                PropertyChanged?.Invoke(this, SecondValueChangedArgs);
            }
        }
    }

    private void UpdateOperatorsForField()
    {
        var fieldType = GetFieldType(_selectedField.Field);
        AvailableOperators = fieldType switch
        {
            FieldType.Text => TextOperators,
            FieldType.Numeric => NumericOperators,
            FieldType.Date => DateOperators,
            FieldType.Boolean => BooleanOperators,
            _ => FallbackOperators
        };

        // Notify that operators list changed first
        PropertyChanged?.Invoke(this, AvailableOperatorsChangedArgs);

        // Always select first operator when switching fields to prevent empty state
        // Use property setter to ensure proper change notification
        if (AvailableOperators.Count > 0)
        {
            // Try to keep the same operator type if it exists in the new list
            var matchingOperator = AvailableOperators.FirstOrDefault(o => o.Operator == _selectedOperator?.Operator);
            SelectedOperator = matchingOperator ?? AvailableOperators[0];
        }
    }

    private static FieldType GetFieldType(SmartPlaylistField field) => field switch
    {
        SmartPlaylistField.Title or SmartPlaylistField.Artist or SmartPlaylistField.Album or
        SmartPlaylistField.Genre or SmartPlaylistField.Comment or SmartPlaylistField.Composer or
        SmartPlaylistField.Grouping => FieldType.Text,
        
        SmartPlaylistField.Year or SmartPlaylistField.PlayCount or SmartPlaylistField.SkipCount or
        SmartPlaylistField.Rating or SmartPlaylistField.Duration or SmartPlaylistField.Bpm or
        SmartPlaylistField.TrackNumber or SmartPlaylistField.DiscNumber or SmartPlaylistField.Bitrate or
        SmartPlaylistField.SampleRate => FieldType.Numeric,
        
        SmartPlaylistField.DateAdded or SmartPlaylistField.LastPlayed or
        SmartPlaylistField.FileCreatedDate or SmartPlaylistField.FileModifiedDate => FieldType.Date,
        
        SmartPlaylistField.IsLoved or SmartPlaylistField.HasLyrics => FieldType.Boolean,
        
        _ => FieldType.Text
    };

    public SmartPlaylistRule? ToSmartPlaylistRule()
    {
        // Defensive check: if no operator is selected (shouldn't happen), skip this rule
        if (_selectedOperator == null) return null;

        return new SmartPlaylistRule
        {
            Field = _selectedField.Field,
            Operator = _selectedOperator.Operator,
            Value = _value,
            SecondValue = string.IsNullOrWhiteSpace(_secondValue) ? null : _secondValue
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;



    private enum FieldType { Text, Numeric, Date, Boolean }
}

/// <summary>
///     Represents a field option for smart playlist rules.
/// </summary>
public record FieldOption(SmartPlaylistField Field, string DisplayName);

/// <summary>
///     Represents an operator option for smart playlist rules.
/// </summary>
public record OperatorOption(SmartPlaylistOperator Operator, string DisplayName);
