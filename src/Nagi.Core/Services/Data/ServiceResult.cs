namespace Nagi.Core.Services.Data;

/// <summary>
///     Defines the possible outcomes of a service operation.
/// </summary>
public enum ServiceResultStatus
{
    /// <summary>
    ///     The operation completed successfully and returned data.
    /// </summary>
    Success,

    /// <summary>
    ///     The operation completed successfully, but no data was found for the query.
    /// </summary>
    SuccessNotFound,

    /// <summary>
    ///     The operation failed due to a temporary issue (e.g., network error) and can be retried.
    /// </summary>
    TemporaryError,

    /// <summary>
    ///     The operation failed due to a permanent issue (e.g., invalid configuration, bad request).
    /// </summary>
    PermanentError
}

/// <summary>
///     A generic wrapper for service method return values, encapsulating the result status,
///     and data.
/// </summary>
/// <typeparam name="T">The type of the data returned by the service.</typeparam>
public class ServiceResult<T> where T : class
{
    private ServiceResult(T? data, ServiceResultStatus status)
    {
        Data = data;
        Status = status;
    }

    /// <summary>
    ///     The data payload of a successful operation. Null otherwise.
    /// </summary>
    public T? Data { get; }

    public ServiceResultStatus Status { get; }

    /// <summary>
    ///     Gets a value indicating whether the service call was conclusive (i.e., not a temporary error).
    ///     A conclusive result means the operation either succeeded, confirmed no data exists, or failed permanently.
    /// </summary>
    public bool IsConclusive => Status is not ServiceResultStatus.TemporaryError;

    public static ServiceResult<T> FromSuccess(T data) => new(data, ServiceResultStatus.Success);

    public static ServiceResult<T> FromSuccessNotFound() => new(null, ServiceResultStatus.SuccessNotFound);

    public static ServiceResult<T> FromTemporaryError() => new(null, ServiceResultStatus.TemporaryError);

    public static ServiceResult<T> FromPermanentError() => new(null, ServiceResultStatus.PermanentError);
}
