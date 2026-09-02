namespace  SafePathBD.Web.Common;

/// <summary>
/// Envelope used by the JSON endpoints, matching docs/API_GUIDELINES.md.
/// </summary>
public sealed record ApiResult<T>(bool Success, string? Message, T? Data);

public static class  ApiResult
{
    public static ApiResult<T> Ok<T>(T data, string? message = null) => new(true, message, data);

    public static ApiResult<object?>  Fail(string message) => new(false, message, null);
}
