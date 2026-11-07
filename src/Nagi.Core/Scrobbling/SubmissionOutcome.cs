namespace Nagi.Core.Scrobbling;

/// <summary>
///     How a scrobbling service answered a listen.
/// </summary>
internal enum SubmissionOutcome
{
    /// <summary>Not accepted this time, such as when the service can't be reached; the listen is sent again later.</summary>
    TransientFailure = 0,
    Success,

    /// <summary>Rejected for good, such as a listen without an artist; the listen is dropped.</summary>
    PermanentFailure
}
