namespace PdfStampNgrokDesktop.Core;

public class Result
{
    public static Result Ok(string message = "") => new(true, ErrorCode.None, message);

    public static Result Fail(ErrorCode code, string message) => new(false, code, message);

    protected Result(bool isSuccess, ErrorCode code, string message)
    {
        IsSuccess = isSuccess;
        Code = code;
        Message = message;
    }

    public bool IsSuccess { get; }

    public ErrorCode Code { get; }

    public string Message { get; }
}

public sealed class Result<T> : Result
{
    private Result(bool isSuccess, ErrorCode code, string message, T? value)
        : base(isSuccess, code, message)
    {
        Value = value;
    }

    public T? Value { get; }

    public static Result<T> Ok(T value, string message = "") => new(true, ErrorCode.None, message, value);

    public static new Result<T> Fail(ErrorCode code, string message) => new(false, code, message, default);
}
