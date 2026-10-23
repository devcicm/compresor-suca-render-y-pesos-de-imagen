using System;
using System.Collections.Generic;

namespace SUCA_Compressor.Application.Common
{
    // Result pattern: expected outcomes travel as values, not exceptions.
    public class Result
    {
        public bool IsSuccess { get; }
        public string? Error { get; }
        public string ErrorCode { get; }

        protected Result(bool isSuccess, string? error, string errorCode)
        {
            IsSuccess = isSuccess;
            Error = error;
            ErrorCode = errorCode;
        }

        public static Result Ok() => new(true, null, ConversionErrorCodes.None);
        public static Result Fail(string error, string errorCode) => new(false, error, errorCode);
        public static Result Fail(Exception ex, string errorCode) => new(false, ex.Message, errorCode);
    }

    public sealed class Result<T> : Result
    {
        private readonly T? _value;

        private Result(T value) : base(true, null, ConversionErrorCodes.None) => _value = value;
        private Result(string error, string errorCode) : base(false, error, errorCode) => _value = default;

        public T Value => IsSuccess
            ? _value!
            : throw new InvalidOperationException($"El resultado no tiene valor: {ErrorCode} ({Error})");

        public static Result<T> Ok(T value) => new(value);
        public static new Result<T> Fail(string error, string errorCode) => new(error, errorCode);
        public static new Result<T> Fail(Exception ex, string errorCode) => new(ex.Message, errorCode);
    }

    public static class ConversionErrorCodes
    {
        public const string None = "";
        public const string InvalidFormat = "invalid-format";
        public const string EmptyFile = "empty-file";
        public const string TooLarge = "too-large";
        public const string UnsupportedImage = "unsupported-image";
        public const string IoError = "io-error";
        public const string Cancelled = "cancelled";
        public const string Internal = "internal";
        public const string InvalidCollection = "invalid-collection";
        public const string CollectionNotFound = "collection-not-found";
        public const string CollectionExists = "collection-exists";
        public const string RecordNotFound = "record-not-found";
        public const string EmptyOrder = "empty-order";
        public const string PathNotFound = "path-not-found";
        public const string InvalidParallelism = "invalid-parallelism";
        public const string OrderNotFound = "order-not-found";
        public const string InvalidStateTransition = "invalid-state-transition";
        public const string ServerRestart = "server-restart";
        public const string TooManyItems = "too-many-items";
    }
}
