namespace Nagi.Core.Services.Data;

/// <summary>
/// Defines the possible outcomes of a service operation.
/// </summary>
public enum ServiceResultStatus {
    /// <summary>
    /// The operation completed successfully and returned data.
    /// </summary>
    Success,
    /// <summary>
    /// The operation completed successfully, but no data was found for the query.
    /// </summary>
    SuccessNotFound,
    /// <summary>
    /// The operation failed due to a temporary issue (e.g., network error) and can be retried.
    /// </summary>
    TemporaryError,
    /// <summary>
    /// The operation failed due to a permanent issue (e.g., invalid configuration, bad request).
    /// </summary>
    PermanentError
}

/// <summary>
/// A generic wrapper for service method return values, encapsulating the result status,
/// data, and any potential error messages.
/// </summary>
/// <typeparam name="T">The type of the data returned by the service.</typeparam>
public class ServiceResult<T> where T : class {
    /// <summary>
    /// The data payload of a successful operation. Null otherwise.
    /// </summary>
    public T? Data { get; }

    /// <summary>
    /// The status of the operation.
    /// </summary>
    public ServiceResultStatus Status { get; }

    /// <summary>
    /// A descriptive message for failed operations. Null otherwise.
    /// </summary>
    public string? ErrorMessage { get; }

    /// <summary>
    /// Gets a value indicating whether the service call was conclusive (i.e., not a temporary error).
    /// A conclusive result means the operation either succeeded, confirmed no data exists, or failed permanently.
    /// </summary>
    public bool IsConclusive => Status is not ServiceResultStatus.TemporaryError;

    private ServiceResult(T? data, ServiceResultStatus status, string? errorMessage = null) {
        Data = data;
        Status = status;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a new success result with a data payload.
    /// </summary>
    public static ServiceResult<T> FromSuccess(T data) =>
        new(data, ServiceResultStatus.Success);

    /// <summary>
    /// Creates a new result indicating the operation was successful but no data was found.
    /// </summary>
    public static ServiceResult<T> FromSuccessNotFound() =>
        new(null, ServiceResultStatus.SuccessNotFound);

    /// <summary>
    /// Creates a new result indicating a temporary error that may be resolved by retrying.
    /// </summary>
    public static ServiceResult<T> FromTemporaryError(string message) =>
        new(null, ServiceResultStatus.TemporaryError, message);

    /// <summary>
    /// Creates a new result indicating a permanent error that should not be retried.
    /// </summary>
    public static ServiceResult<T> FromPermanentError(string message) =>
        new(null, ServiceResultStatus.PermanentError, message);
}