namespace NatsSyncLab.SharedKernel.Infrastructure.ApiResponse;

public sealed record CustomApiResponse<T>(bool Success, string? Message, T? Data)
{
    public static CustomApiResponse<T> Ok(T data, string? message = null) =>
        new(true, message, data);

    public static CustomApiResponse<T> Fail(string message) =>
        new(false, message, default);
}
