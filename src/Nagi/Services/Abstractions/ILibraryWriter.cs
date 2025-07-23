﻿using System;
using System.Threading.Tasks;
using Nagi.Models;
using Nagi.Services.Data;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Defines the contract for writing, updating, and deleting data in the music library.
/// </summary>
public interface ILibraryWriter {
    Task<Folder?> AddFolderAsync(string path, string? name = null);
    Task<bool> RemoveFolderAsync(Guid folderId);
    Task<bool> UpdateFolderAsync(Folder folder);
    Task<Song?> AddSongAsync(Song songData);
    Task<Song?> AddSongWithDetailsAsync(Guid folderId, SongFileMetadata metadata);
    Task<bool> RemoveSongAsync(Guid songId);
    Task<bool> UpdateSongAsync(Song songToUpdate);
    Task<bool> SetSongRatingAsync(Guid songId, int? rating);
    Task<bool> SetSongLovedStatusAsync(Guid songId, bool isLoved);
    Task<bool> UpdateSongLyricsAsync(Guid songId, string? lyrics);
    Task<long?> CreateListenHistoryEntryAsync(Guid songId);
    Task<bool> MarkListenAsScrobbledAsync(long listenHistoryId);
    Task<bool> MarkListenAsEligibleForScrobblingAsync(long listenHistoryId);
    Task LogSkipAsync(Guid songId);
    Task ClearAllLibraryDataAsync();
}