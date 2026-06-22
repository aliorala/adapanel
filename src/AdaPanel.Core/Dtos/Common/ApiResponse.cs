namespace AdaPanel.Core.Dtos.Common;

public sealed class ApiResponse<T>
{
    public bool Success { get; init; }

    public T? Data { get; init; }

    public string Message { get; init; } = string.Empty;

    public int Code { get; init; }

    public string Details { get; init; } = string.Empty;

    public static ApiResponse<T> Ok(T data, string message = "", int code = 0)
    {
        return new ApiResponse<T>
        {
            Success = true,
            Data = data,
            Message = message,
            Code = code
        };
    }

    public static ApiResponse<T> Fail(string message, int code = 1, T? data = default, string details = "")
    {
        return new ApiResponse<T>
        {
            Success = false,
            Data = data,
            Message = message,
            Code = code,
            Details = details
        };
    }
}
