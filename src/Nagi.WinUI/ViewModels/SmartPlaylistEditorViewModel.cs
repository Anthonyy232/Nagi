using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nagi.Core.Constants;
using Nagi.Core.Helpers;
using Nagi.Core.Models;
using Nagi.Core.Services.Abstractions;
using Nagi.WinUI.Helpers;
using Nagi.WinUI.Resources;
using Nagi.WinUI.Services.Abstractions;

namespace Nagi.WinUI.ViewModels;

public enum SmartPlaylistSaveResult
{
    Saved,
    NameMissing,
    DuplicateName,
    Failed
}

public record SortOrderOption(SmartPlaylistSortOrder SortOrder, string DisplayName);

/// <summary>
///     State and behaviour of the smart playlist editor: name, cover, match logic, sort order,
///     rules, a live count of matching songs, and saving.
/// </summary>
public partial class SmartPlaylistEditorViewModel : ObservableObject, IDisposable
{
    private const int SqliteConstraintUnique = 2067;

    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly IUIService _uiService;
    private readonly IDispatcherService _dispatcherService;
    private readonly ILogger<SmartPlaylistEditorViewModel> _logger;
    private readonly Debouncer _matchCountDebouncer;
    private SmartPlaylist? _editingPlaylist;
    private bool _isInitialized;
    private bool _isDisposed;

    public SmartPlaylistEditorViewModel(
        ISmartPlaylistService smartPlaylistService,
        IUIService uiService,
        IDispatcherService dispatcherService,
        ILogger<SmartPlaylistEditorViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        _smartPlaylistService = smartPlaylistService ?? throw new ArgumentNullException(nameof(smartPlaylistService));
        _uiService = uiService ?? throw new ArgumentNullException(nameof(uiService));
        _dispatcherService = dispatcherService ?? throw new ArgumentNullException(nameof(dispatcherService));
        _logger = logger;
        _matchCountDebouncer = new Debouncer(TimeSpan.FromMilliseconds(300), timeProvider);
        SelectedSortOption = SortOptions[0];
        Rules.CollectionChanged += OnRulesCollectionChanged;
    }

    public static IReadOnlyList<SortOrderOption> SortOptions { get; } =
    [
        new(SmartPlaylistSortOrder.TitleAsc, SortOrderHelper.TitleAscLabel),
        new(SmartPlaylistSortOrder.TitleDesc, SortOrderHelper.TitleDescLabel),
        new(SmartPlaylistSortOrder.ArtistAsc, SortOrderHelper.ArtistAscLabel),
        new(SmartPlaylistSortOrder.ArtistDesc, SortOrderHelper.ArtistDescLabel),
        new(SmartPlaylistSortOrder.AlbumAsc, SortOrderHelper.AlbumAscLabel),
        new(SmartPlaylistSortOrder.AlbumDesc, SortOrderHelper.AlbumDescLabel),
        new(SmartPlaylistSortOrder.YearAsc, SortOrderHelper.YearOldestLabel),
        new(SmartPlaylistSortOrder.YearDesc, SortOrderHelper.YearNewestLabel),
        new(SmartPlaylistSortOrder.PlayCountAsc, SortOrderHelper.PlayCountAscLabel),
        new(SmartPlaylistSortOrder.PlayCountDesc, SortOrderHelper.PlayCountDescLabel),
        new(SmartPlaylistSortOrder.LastPlayedAsc, SortOrderHelper.LastPlayedAscLabel),
        new(SmartPlaylistSortOrder.LastPlayedDesc, SortOrderHelper.LastPlayedDescLabel),
        new(SmartPlaylistSortOrder.DateAddedAsc, SortOrderHelper.DateAddedAscLabel),
        new(SmartPlaylistSortOrder.DateAddedDesc, SortOrderHelper.DateAddedDescLabel),
        new(SmartPlaylistSortOrder.TrackNumberAsc, SortOrderHelper.TrackNumberAscLabel),
        new(SmartPlaylistSortOrder.TrackNumberDesc, SortOrderHelper.TrackNumberDescLabel),
        new(SmartPlaylistSortOrder.DurationAsc, SortOrderHelper.DurationAscLabel),
        new(SmartPlaylistSortOrder.DurationDesc, SortOrderHelper.DurationDescLabel),
        new(SmartPlaylistSortOrder.BpmAsc, SortOrderHelper.BpmAscLabel),
        new(SmartPlaylistSortOrder.BpmDesc, SortOrderHelper.BpmDescLabel),
        new(SmartPlaylistSortOrder.FileCreatedDateAsc, SortOrderHelper.DateCreatedOldestLabel),
        new(SmartPlaylistSortOrder.FileCreatedDateDesc, SortOrderHelper.DateCreatedNewestLabel),
        new(SmartPlaylistSortOrder.Random, SortOrderHelper.RandomLabel)
    ];

    public ObservableCollection<RuleViewModel> Rules { get; } = new();

    public bool HasNoRules => Rules.Count == 0;

    [ObservableProperty] public partial string Title { get; set; } = Strings.SmartPlaylist_Title_New;

    [ObservableProperty] public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchLogicIndex))]
    public partial bool MatchAllRules { get; set; } = true;

    /// <summary>
    ///     The match logic as the editor's "All"/"Any" picker index.
    /// </summary>
    public int MatchLogicIndex
    {
        get => MatchAllRules ? 0 : 1;
        set => MatchAllRules = value != 1;
    }

    [ObservableProperty] public partial SortOrderOption SelectedSortOption { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCoverImage))]
    public partial string? CoverImageUri { get; set; }

    public bool HasCoverImage => !string.IsNullOrWhiteSpace(CoverImageUri);

    [ObservableProperty] public partial string MatchCountText { get; set; } = Strings.SmartPlaylist_Status_Calculating;

    /// <summary>
    ///     The created or updated playlist after a successful <see cref="SaveAsync" />.
    /// </summary>
    public SmartPlaylist? ResultPlaylist { get; private set; }

    /// <summary>
    ///     Loads the editor for a new playlist (<paramref name="editing" /> is null) or an existing one,
    ///     then starts counting matches.
    /// </summary>
    public void Initialize(SmartPlaylist? editing)
    {
        _editingPlaylist = editing;
        if (editing is not null)
        {
            Title = ResourceFormatter.Format(Strings.SmartPlaylist_Title_EditFormat, editing.Name);
            Name = editing.Name;
            MatchAllRules = editing.MatchAllRules;
            SelectedSortOption = SortOptions.FirstOrDefault(o => o.SortOrder == editing.SortOrder) ?? SortOptions[0];
            CoverImageUri = string.IsNullOrWhiteSpace(editing.CoverImageUri) ? null : editing.CoverImageUri;
            foreach (var rule in editing.Rules.OrderBy(r => r.Order))
                Rules.Add(new RuleViewModel(rule));
        }
        else
        {
            Title = Strings.SmartPlaylist_Title_New;
        }

        _isInitialized = true;
        MatchCountText = Strings.SmartPlaylist_Status_Calculating;
        _ = UpdateMatchCountAsync();
    }

    partial void OnNameChanged(string value) => _ = UpdateMatchCountAsync();

    partial void OnMatchAllRulesChanged(bool value) => _ = UpdateMatchCountAsync();

    partial void OnSelectedSortOptionChanged(SortOrderOption value) => _ = UpdateMatchCountAsync();

    [RelayCommand]
    private void AddRule() => Rules.Add(new RuleViewModel());

    [RelayCommand]
    private void RemoveRule(RuleViewModel? rule)
    {
        if (rule is not null) Rules.Remove(rule);
    }

    [RelayCommand]
    private async Task PickCoverImageAsync()
    {
        try
        {
            var file = await _uiService.PickSingleFileAsync(FileExtensions.ImageFileExtensions);
            if (file is not null) CoverImageUri = file;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error picking cover image");
        }
    }

    /// <summary>
    ///     Creates or updates the playlist. On anything other than <see cref="SmartPlaylistSaveResult.Saved" />
    ///     the editor should stay open; <see cref="MatchCountText" /> explains save failures.
    /// </summary>
    public async Task<SmartPlaylistSaveResult> SaveAsync()
    {
        _matchCountDebouncer.Cancel();

        var playlist = BuildPlaylist();
        if (playlist is null) return SmartPlaylistSaveResult.NameMissing;

        try
        {
            ResultPlaylist = _editingPlaylist is null
                ? await _smartPlaylistService.CreateSmartPlaylistAsync(playlist)
                : await _smartPlaylistService.UpdateSmartPlaylistAsync(playlist);
            if (ResultPlaylist is not null) return SmartPlaylistSaveResult.Saved;

            MatchCountText = Strings.SmartPlaylist_Status_Error;
            return SmartPlaylistSaveResult.Failed;
        }
        catch (DbUpdateException dbEx) when (dbEx.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteConstraintUnique })
        {
            _logger.LogWarning(dbEx, "Duplicate smart playlist name");
            MatchCountText = Strings.SmartPlaylist_Error_DuplicateName;
            return SmartPlaylistSaveResult.DuplicateName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving smart playlist");
            MatchCountText = Strings.SmartPlaylist_Status_Error;
            return SmartPlaylistSaveResult.Failed;
        }
    }

    /// <summary>
    ///     The playlist described by the editor, or null while it has no name.
    /// </summary>
    public SmartPlaylist? BuildPlaylist()
    {
        var name = Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) return null;

        var playlist = new SmartPlaylist
        {
            Id = _editingPlaylist?.Id ?? Guid.NewGuid(),
            Name = name,
            MatchAllRules = MatchAllRules,
            SortOrder = SelectedSortOption.SortOrder,
            CoverImageUri = CoverImageUri,
            Description = _editingPlaylist?.Description,
            DateCreated = _editingPlaylist?.DateCreated ?? DateTime.UtcNow,
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

    internal Task UpdateMatchCountAsync()
    {
        if (!_isInitialized || _isDisposed) return Task.CompletedTask;

        return _matchCountDebouncer.RunAsync(async token =>
        {
            try
            {
                var playlist = await _dispatcherService.EnqueueAsync(() =>
                    token.IsCancellationRequested ? null : BuildPlaylist());
                token.ThrowIfCancellationRequested();
                // SQLite executes count queries synchronously, so keep them off the UI thread.
                int? count = playlist == null ? null
                    : await Task.Run(() => _smartPlaylistService.GetMatchingSongCountAsync(playlist, token: token), token);

                _dispatcherService.TryEnqueue(() =>
                {
                    if (token.IsCancellationRequested) return;
                    MatchCountText = count.HasValue
                        ? ResourceFormatter.Format(Strings.SmartPlaylist_Status_MatchCountFormat, count.Value)
                        : Strings.SmartPlaylist_Status_EnterName;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error calculating match count");
                _dispatcherService.TryEnqueue(() =>
                {
                    if (!token.IsCancellationRequested) MatchCountText = Strings.SmartPlaylist_Status_Error;
                });
            }
        });
    }

    private void OnRulesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNoRules));

        if (e.NewItems != null)
            foreach (RuleViewModel rule in e.NewItems)
                rule.PropertyChanged += OnRulePropertyChanged;

        if (e.OldItems != null)
            foreach (RuleViewModel rule in e.OldItems)
                rule.PropertyChanged -= OnRulePropertyChanged;

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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _matchCountDebouncer.Dispose();
        Rules.CollectionChanged -= OnRulesCollectionChanged;
        foreach (var rule in Rules) rule.PropertyChanged -= OnRulePropertyChanged;
    }
}
