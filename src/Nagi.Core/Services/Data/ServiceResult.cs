namespace Nagi.Core.Services.Data;

public enum ServiceResultStatus
{
    Success,

    /// <summary>
    ///     The operation completed successfully, but no data was found for the query.
    /// </summary>
    SuccessNotFound,

    /// <summary>
    ///     The operation failed due to a temporary issue (e.g., network error) and can be retried.
    /// </summary>
    TemporaryError
}

public class ServiceResult<T> where T : class
{
    private ServiceResult(T? data, ServiceResultStatus status)
    {
        Data = data;
        Status = status;
    }

    public T? Data { get; }

    public ServiceResultStatus Status { get; }

    /// <summary>
    ///     Gets a value indicating whether the service call was conclusive (i.e., not a temporary error).
    ///     A conclusive result means the operation either succeeded or confirmed no data exists.
    /// </summary>
    public bool IsConclusive => Status is not ServiceResultStatus.TemporaryError;

    public static ServiceResult<T> FromSuccess(T data) => new(data, ServiceResultStatus.Success);

    public static ServiceResult<T> FromSuccessNotFound() => new(null, ServiceResultStatus.SuccessNotFound);

    public static ServiceResult<T> FromSuccessOrNotFound(T? data) => data is null ? FromSuccessNotFound() : FromSuccess(data);

    public static ServiceResult<T> FromTemporaryError() => new(null, ServiceResultStatus.TemporaryError);
}
