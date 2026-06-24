using Gert.Validation;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// A trivial <see cref="IValidationProvider"/> that accepts everything (only a null
/// instance fails): the ingestion integration tests exercise the storage/ingestion
/// path, not the validation gate, which has its own unit tests.
/// </summary>
internal sealed class PassThroughValidationProvider : IValidationProvider
{
    public ValidationResult Validate<T>(T instance) =>
        instance is null
            ? ValidationResult.Failure([new ValidationError { Property = string.Empty, Message = "null", Code = "null" }])
            : ValidationResult.Success;
}
