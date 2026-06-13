using Gert.Service.Validation;

namespace Gert.Database.Sqlite.Tests;

/// <summary>
/// A trivial <see cref="IValidationProvider"/> that accepts everything - the ingestion
/// integration tests exercise the storage/ingestion path, not the validation gate
/// (the real validators have their own unit tests). Rejects only a null instance.
/// </summary>
internal sealed class PassThroughValidationProvider : IValidationProvider
{
    public ValidationResult Validate<T>(T instance) =>
        instance is null
            ? ValidationResult.Failure([new ValidationError { Property = string.Empty, Message = "null", Code = "null" }])
            : ValidationResult.Success;
}
