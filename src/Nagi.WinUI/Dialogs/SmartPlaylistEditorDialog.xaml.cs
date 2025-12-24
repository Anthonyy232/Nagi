using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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
        InitializeComponent();
        _smartPlaylistService = App.Services!.GetRequiredService<ISmartPlaylistService>();
        _logger = App.Services!.GetRequiredService<ILogger<SmartPlaylistEditorDialog>>();
        
        Rules.CollectionChanged += (s, e) =>
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
        };
        Unloaded += OnDialogUnloaded;
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
            UpdateMatchCountAsync();
        }
    }

    private void OnDialogUnloaded(object sender, RoutedEventArgs e)
    {
        _matchCountCts?.Cancel();
        _matchCountCts?.Dispose();
        _matchCountCts = null;
    }

    private void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("SmartPlaylistEditorDialog loaded. EditingPlaylist: {EditingPlaylistId}",
            EditingPlaylist?.Id.ToString() ?? "null (creating new)");

        if (EditingPlaylist != null)
        {
            Title = $"Edit Smart Playlist - {EditingPlaylist.Name}";
            PlaylistNameTextBox.Text = EditingPlaylist.Name;
            MatchLogicComboBox.SelectedIndex = EditingPlaylist.MatchAllRules ? 0 : 1;

            SelectSortByComboBoxItem(EditingPlaylist.SortOrder);

            // Load existing cover image
            if (!string.IsNullOrWhiteSpace(EditingPlaylist.CoverImageUri))
            {
                _selectedCoverImageUri = EditingPlaylist.CoverImageUri;
                CoverImagePreview.Source = EditingPlaylist.CoverImageUri;
                CoverImagePreview.Visibility = Visibility.Visible;
                CoverImagePlaceholder.Visibility = Visibility.Collapsed;
            }

            foreach (var rule in EditingPlaylist.Rules.OrderBy(r => r.Order))
                Rules.Add(new RuleViewModel(rule));
        }
        else
        {
            Title = "New Smart Playlist";
        }

        UpdateNoRulesVisibility();
        UpdateMatchCountAsync();
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
        UpdateMatchCountAsync();
    }

    private async void PickCoverImage_Click(object sender, RoutedEventArgs e)
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
            CoverImagePreview.Source = file.Path;
            CoverImagePreview.Visibility = Visibility.Visible;
            CoverImagePlaceholder.Visibility = Visibility.Collapsed;
        }
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        _logger.LogDebug("Adding new rule");
        Rules.Add(new RuleViewModel());
        UpdateMatchCountAsync();
    }

    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: RuleViewModel rule })
        {
            _logger.LogDebug("Removing rule");
            Rules.Remove(rule);
            UpdateMatchCountAsync();
        }
    }


    private void UpdateNoRulesVisibility()
    {
        NoRulesPanel.Visibility = Rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void UpdateMatchCountAsync()
    {
        // Dispose the old CTS to prevent resource leaks
        _matchCountCts?.Cancel();
        _matchCountCts?.Dispose();
        _matchCountCts = new CancellationTokenSource();
        var token = _matchCountCts.Token;

        MatchCountText.Text = "Calculating...";

        try
        {
            await Task.Delay(300, token); // Debounce
            if (token.IsCancellationRequested) return;

            // Build a temporary smart playlist to get the count
            var tempPlaylist = BuildSmartPlaylistFromUI();
            if (tempPlaylist == null)
            {
                MatchCountText.Text = "Enter a playlist name";
                return;
            }

            var count = await Task.Run(async () =>
            {
                // Create a temporary smart playlist with the current rules to get count
                // This is a simplified approach - ideally we'd have a preview method
                if (EditingPlaylist != null)
                {
                    // For existing playlists, strictly speaking we might want to check the DB
                    // BUT if we modified rules in UI, we want to count against those *unsaved* rules.
                    // safely use the tempPlaylist which represents the current UI state.
                    return await _smartPlaylistService.GetMatchingSongCountAsync(tempPlaylist);
                }
                
                // For new playlists, use the temp object directly
                return await _smartPlaylistService.GetMatchingSongCountAsync(tempPlaylist);
            }, token);

            if (token.IsCancellationRequested) return;

            MatchCountText.Text = count >= 0 
                ? $"{count:N0} songs match current rules" 
                : "Save to see matching songs";
        }
        catch (TaskCanceledException)
        {
            // Ignore - new calculation started
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating match count");
            MatchCountText.Text = "Unable to calculate";
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


        foreach (var ruleVm in Rules)
        {
            var rule = ruleVm.ToSmartPlaylistRule();
            if (rule != null)
            {
                rule.SmartPlaylistId = playlist.Id;
                playlist.Rules.Add(rule);
            }
        }

        // Reindex rules
        var rulesList = playlist.Rules.ToList();
        for (var i = 0; i < rulesList.Count; i++)
            rulesList[i].Order = i;

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
                PlaylistNameTextBox.PlaceholderText = "Please enter a name!";
                return;
            }

            var isEditing = EditingPlaylist != null;

            if (isEditing)
            {
                // Update existing playlist
                EditingPlaylist!.Name = name;
                EditingPlaylist.MatchAllRules = MatchLogicComboBox.SelectedIndex == 0;
                EditingPlaylist.SortOrder = GetSelectedSortOrder();
                EditingPlaylist.CoverImageUri = _selectedCoverImageUri;

                await _smartPlaylistService.UpdateSmartPlaylistAsync(EditingPlaylist);

                // Remove and re-add rules
                var existingRules = EditingPlaylist.Rules.ToList();
                foreach (var rule in existingRules)
                    await _smartPlaylistService.RemoveRuleAsync(rule.Id);

                foreach (var ruleVm in Rules)
                {
                    var rule = ruleVm.ToSmartPlaylistRule();
                    if (rule != null)
                    {
                        await _smartPlaylistService.AddRuleAsync(
                            EditingPlaylist.Id,
                            rule.Field,
                            rule.Operator,
                            rule.Value,
                            rule.SecondValue);
                    }
                }

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
                    PlaylistNameTextBox.PlaceholderText = "A playlist with this name already exists!";
                    PlaylistNameTextBox.Text = string.Empty;
                    return;
                }

                // Set configuration
                await _smartPlaylistService.SetMatchAllRulesAsync(newPlaylist.Id, MatchLogicComboBox.SelectedIndex == 0);
                await _smartPlaylistService.SetSortOrderAsync(newPlaylist.Id, GetSelectedSortOrder());


                // Add rules
                foreach (var ruleVm in Rules)
                {
                    var rule = ruleVm.ToSmartPlaylistRule();
                    if (rule != null)
                    {
                        await _smartPlaylistService.AddRuleAsync(
                            newPlaylist.Id,
                            rule.Field,
                            rule.Operator,
                            rule.Value,
                            rule.SecondValue);
                    }
                }

                ResultPlaylist = await _smartPlaylistService.GetSmartPlaylistByIdAsync(newPlaylist.Id);
                _logger.LogInformation("Created smart playlist {PlaylistId}", newPlaylist.Id);
            }
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx) when (dbEx.InnerException?.Message.Contains("UNIQUE constraint failed") == true)
        {
            _logger.LogWarning(dbEx, "Duplicate smart playlist name");
            args.Cancel = true;
            PlaylistNameTextBox.Focus(FocusState.Programmatic);
            PlaylistNameTextBox.PlaceholderText = "A playlist with this name already exists!";
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
    private FieldOption _selectedField;
    private OperatorOption _selectedOperator;
    private string _value = string.Empty;
    private string _secondValue = string.Empty;

    public RuleViewModel()
    {
        _selectedField = AvailableFields[0];
        UpdateOperatorsForField();
        _selectedOperator = AvailableOperators.Count > 0 ? AvailableOperators[0] : new OperatorOption(SmartPlaylistOperator.Contains, "contains");
    }

    public RuleViewModel(SmartPlaylistRule rule)
    {
        _selectedField = AvailableFields.FirstOrDefault(f => f.Field == rule.Field) ?? AvailableFields[0];
        UpdateOperatorsForField();
        _selectedOperator = AvailableOperators.FirstOrDefault(o => o.Operator == rule.Operator) ?? AvailableOperators[0];
        _value = rule.Value ?? string.Empty;
        _secondValue = rule.SecondValue ?? string.Empty;
    }

    public List<FieldOption> AvailableFields { get; } = new()
    {
        new FieldOption(SmartPlaylistField.Title, "Title"),
        new FieldOption(SmartPlaylistField.Artist, "Artist"),
        new FieldOption(SmartPlaylistField.Album, "Album"),
        new FieldOption(SmartPlaylistField.Genre, "Genre"),
        new FieldOption(SmartPlaylistField.Year, "Year"),
        new FieldOption(SmartPlaylistField.Rating, "Rating"),
        new FieldOption(SmartPlaylistField.Duration, "Duration"),
        new FieldOption(SmartPlaylistField.Bpm, "BPM"),
        new FieldOption(SmartPlaylistField.Comment, "Comment"),
        new FieldOption(SmartPlaylistField.Composer, "Composer")
    };

    public List<OperatorOption> AvailableOperators { get; private set; } = new();

    public FieldOption SelectedField
    {
        get => _selectedField;
        set
        {
            if (_selectedField != value)
            {
                _selectedField = value;
                OnPropertyChanged();
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
                OnPropertyChanged();
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
                OnPropertyChanged();
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
                OnPropertyChanged();
            }
        }
    }

    private void UpdateOperatorsForField()
    {
        var fieldType = GetFieldType(_selectedField.Field);
        AvailableOperators = fieldType switch
        {
            FieldType.Text => new List<OperatorOption>
            {
                new(SmartPlaylistOperator.Contains, "Contains"),
                new(SmartPlaylistOperator.DoesNotContain, "Does not contain"),
                new(SmartPlaylistOperator.Is, "Is exactly"),
                new(SmartPlaylistOperator.IsNot, "Is not"),
                new(SmartPlaylistOperator.StartsWith, "Starts with"),
                new(SmartPlaylistOperator.EndsWith, "Ends with")
            },
            FieldType.Numeric => new List<OperatorOption>
            {
                new(SmartPlaylistOperator.Equals, "Is equal to"),
                new(SmartPlaylistOperator.NotEquals, "Is not equal to"),
                new(SmartPlaylistOperator.GreaterThan, "Is greater than"),
                new(SmartPlaylistOperator.LessThan, "Is less than"),
                new(SmartPlaylistOperator.GreaterThanOrEqual, "Is at least"),
                new(SmartPlaylistOperator.LessThanOrEqual, "Is at most")
            },
            FieldType.Date => new List<OperatorOption>
            {
                new(SmartPlaylistOperator.IsInTheLast, "Is in the last (days)"),
                new(SmartPlaylistOperator.IsNotInTheLast, "Is not in the last (days)")
            },
            FieldType.Boolean => new List<OperatorOption>
            {
                new(SmartPlaylistOperator.IsTrue, "Is true"),
                new(SmartPlaylistOperator.IsFalse, "Is false")
            },
        // Fallback to text operators
            _ => new List<OperatorOption>
            {
                new(SmartPlaylistOperator.Contains, "Contains"),
                new(SmartPlaylistOperator.Is, "Is exactly")
            }
        };

        // Notify that operators list changed first
        OnPropertyChanged(nameof(AvailableOperators));

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private enum FieldType { Text, Numeric, Date, Boolean }
}

public class FieldOption
{
    public FieldOption(SmartPlaylistField field, string displayName)
    {
        Field = field;
        DisplayName = displayName;
    }
    
    public SmartPlaylistField Field { get; set; }
    public string DisplayName { get; set; }
}

public class OperatorOption
{
    public OperatorOption(SmartPlaylistOperator op, string displayName)
    {
        Operator = op;
        DisplayName = displayName;
    }
    
    public SmartPlaylistOperator Operator { get; set; }
    public string DisplayName { get; set; }
}
