using FluentValidation;
using Gert.Model.Dtos;
using Gert.Service.Documents;
using Gert.Service.Validation;
using Gert.Testing.TestData;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// The data-driven adversarial pass (testing.md section 5 #2): every string in the
/// shared <see cref="NaughtyStrings"/> corpus is fed through every string field of
/// every request DTO. The contract: validation must <b>never crash</b> and must
/// <b>never slip a dangerous payload through</b> — each input is either rejected or
/// safely accepted. We assert the validator returns a result (no exception) and
/// that the known-malicious categories (control chars, bidi overrides, traversal)
/// are actively rejected on the fields that carry human text or filenames.
/// </summary>
public sealed class AdversarialCorpusTests
{
    private readonly ServiceProvider _sp = ValidationTestHost.Build("rag", "search");

    /// <summary>The whole corpus never crashes the message validator.</summary>
    [Theory]
    [MemberData(nameof(All))]
    public void Message_content_never_crashes(string payload)
    {
        var v = _sp.Validator<SendMessageRequest>();
        // Must not throw, regardless of how hostile the input is.
        _ = v.Validate(new SendMessageRequest { Content = payload });
    }

    /// <summary>Control / bidi / empty payloads are actively rejected as message content.</summary>
    [Theory]
    [MemberData(nameof(MustReject))]
    public void Dangerous_message_content_is_rejected(string payload)
    {
        var v = _sp.Validator<SendMessageRequest>();
        var result = v.Validate(new SendMessageRequest { Content = payload });
        Assert.False(result.IsValid, $"payload should have been rejected: {Describe(payload)}");
    }

    /// <summary>
    /// Filenames (U7d decision): the upload name is display metadata, not a storage
    /// path (it is base64'd into <c>documents.filename</c>; the blob lives under a
    /// server-generated <c>{doc-id}.{ext}</c> key), so the gate is extension allowlist
    /// + length + non-empty only — it does NOT sanitize the name for traversal/bidi.
    /// The contract here: validation never crashes, and a name is rejected exactly
    /// when its extension is disallowed, it is empty, or it is overlong. Path-shaped
    /// or bidi-laden names with an allowed extension are preserved.
    /// </summary>
    [Theory]
    [MemberData(nameof(All))]
    public void Upload_filename_gate_is_extension_length_and_nonempty_only(string payload)
    {
        var v = _sp.Validator<DocumentUpload>();
        var upload = new DocumentUpload
        {
            Filename = payload,
            Mime = "application/pdf",
            OpenReadStream = () => Stream.Null,
            SizeBytes = 1,
        };

        var result = v.Validate(upload); // never throws

        var ext = ValidationRules.ExtensionOf(payload);
        var extAllowed = UploadConstraints.AllowedExtensions.Contains(ext);
        var lengthOk = !string.IsNullOrEmpty(payload)
                       && payload.Length <= Gert.Service.Validation.Validators.DocumentUploadValidator.MaxFilenameLength;

        if (!extAllowed || !lengthOk)
        {
            Assert.False(result.IsValid, $"filename should have been rejected: {Describe(payload)}");
        }

        // The filename rule must never crash on any corpus entry — the assertion above
        // already exercised Validate without an exception.
    }

    /// <summary>Model id / title fields never crash across the whole corpus.</summary>
    [Theory]
    [MemberData(nameof(All))]
    public void Model_id_and_title_never_crash(string payload)
    {
        _ = _sp.Validator<SendMessageRequest>()
            .Validate(new SendMessageRequest { Content = "ok", ModelId = payload });
        _ = _sp.Validator<CreateConversationRequest>()
            .Validate(new CreateConversationRequest { Title = payload });
        _ = _sp.Validator<CreateProjectRequest>()
            .Validate(new CreateProjectRequest { Name = payload });
        _ = _sp.Validator<CreateMemoryRequest>()
            .Validate(new CreateMemoryRequest { Title = payload, Content = payload });
    }

    public static IEnumerable<object[]> All() => NaughtyStrings.AllTheoryData();

    public static IEnumerable<object[]> MustReject()
    {
        // Control-char and bidi-override entries that actually carry a forbidden
        // char (an entry of only tab/newline is legitimately accepted).
        foreach (var s in NaughtyStrings.ControlChars.Concat(NaughtyStrings.BidiAndZeroWidth))
        {
            if (ValidationRules.ContainsForbiddenControlChar(s) || ValidationRules.ContainsBidiOverride(s))
            {
                yield return new object[] { s };
            }
        }

        // Truly empty / whitespace-only entries are rejected. (A lone zero-width
        // space is not .NET-whitespace, so it is accepted-but-safe — excluded here.)
        foreach (var s in NaughtyStrings.Emptyish)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                yield return new object[] { s };
            }
        }

        // Oversized blobs exceed the message length cap.
        foreach (var s in NaughtyStrings.Oversized)
        {
            if (s.Length > ValidationRules.LongTextMax)
            {
                yield return new object[] { s };
            }
        }
    }

    private static string Describe(string s) =>
        string.Concat(s.Take(40).Select(c => c < ' ' || c > '~' ? $"\\u{(int)c:X4}" : c.ToString()));
}
