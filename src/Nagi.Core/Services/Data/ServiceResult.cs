namespace Nagi.Core.Services.Data;

public enum ServiceResultStatus
{
    Success,

    /// <summary>The service answered, and has no data for the query.</summary>
    SuccessNotFound,

    /// <summary>The service couldn't answer, for example because of a network error, so the query can be retried.</summary>
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

    /// <summary>The service answered, so missing data is final rather than worth retrying.</summary>
    public bool IsConclusive => Status is not ServiceResultStatus.TemporaryError;

    public static ServiceResult<T> FromSuccess(T data) => new(data, ServiceResultStatus.Success);

    public static ServiceResult<T> FromSuccessNotFound() => new(null, ServiceResultStatus.SuccessNotFound);

    public static ServiceResult<T> FromSuccessOrNotFound(T? data) => data is null ? FromSuccessNotFound() : FromSuccess(data);

    public static ServiceResult<T> FromTemporaryError() => new(null, ServiceResultStatus.TemporaryError);

    /// <summary>Converts the data, keeping a missing answer missing and a failure retryable.</summary>
    public ServiceResult<TOut> Map<TOut>(Func<T, TOut?> map) where TOut : class =>
        Data is not null ? ServiceResult<TOut>.FromSuccessOrNotFound(map(Data))
        : IsConclusive ? ServiceResult<TOut>.FromSuccessNotFound()
        : ServiceResult<TOut>.FromTemporaryError();
}
