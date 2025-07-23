using System.Threading.Tasks;

namespace Nagi.Services.Abstractions;

/// <summary>
/// Manages and coordinates all available IPresenceService implementations,
/// acting as a bridge between the core playback service and external integrations.
/// </summary>
public interface IPresenceManager {
    /// <summary>
    /// Initializes the manager and all enabled presence services based on user settings.
    /// Subscribes to playback events.
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Shuts down all active presence services gracefully and unsubscribes from events.
    /// </summary>
    Task ShutdownAsync();
}