using FluentAssertions;
using Gert.Service.Storage;
using Gert.Service.Validation;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// <see cref="CountingStream"/>: byte tallying and the optional mid-stream size cap
/// (testing.md section 5 - max upload size). The cap is the streaming complement of
/// <c>DocumentUploadValidator</c>'s <c>upload.too_large</c> rule: when a host cannot
/// supply <c>SizeBytes</c> up front, this is the only brake, and it must surface the
/// exact same error code so the API's 400 is identical on both paths.
/// </summary>
public sealed class CountingStreamTests
{
    [Fact]
    public void Reading_without_a_limit_counts_every_byte()
    {
        using var counting = new CountingStream(new MemoryStream(new byte[10]));

        counting.CopyTo(Stream.Null);

        counting.BytesRead.Should().Be(10);
    }

    [Fact]
    public void Reading_exactly_the_limit_passes_and_counts()
    {
        using var counting = new CountingStream(new MemoryStream(new byte[10]), limit: 10);

        var act = () => counting.CopyTo(Stream.Null);

        act.Should().NotThrow("the cap is inclusive, matching the validator's LessThanOrEqualTo");
        counting.BytesRead.Should().Be(10);
    }

    [Fact]
    public void Reading_past_the_limit_throws_the_validators_too_large_error()
    {
        using var counting = new CountingStream(new MemoryStream(new byte[11]), limit: 10);

        var act = () => counting.CopyTo(Stream.Null);

        act.Should().Throw<ValidationException>()
            .Which.Result.Errors.Should().ContainSingle()
            .Which.Code.Should().Be("upload.too_large", "the streamed cap must map to the same branded 400 as the validator path");
    }

    [Fact]
    public async Task Async_reads_past_the_limit_throw_too()
    {
        await using var counting = new CountingStream(new MemoryStream(new byte[11]), limit: 10);

        var act = async () => await counting.CopyToAsync(Stream.Null);

        await act.Should().ThrowAsync<ValidationException>();
    }
}
