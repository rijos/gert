using System.Collections.Generic;
using FluentValidation;
using FluentValidation.TestHelper;
using Gert.Model.Chat;
using Gert.Model.Dtos;
using Gert.Service.Validation;
using Xunit;

namespace Gert.Service.Tests.Validation;

/// <summary>
/// Positive / negative / boundary tests for <see cref="SendMessageRequest"/>
/// validation (the chat hot path). Resolves the <b>production</b> validator from
/// the real DI registration.
/// </summary>
public sealed class SendMessageRequestValidatorTests
{
    private readonly IValidator<SendMessageRequest> _validator;

    public SendMessageRequestValidatorTests()
    {
        var sp = ValidationTestHost.Build("rag", "search");
        _validator = sp.Validator<SendMessageRequest>();
    }

    [Fact]
    public void Valid_message_passes()
    {
        var result = _validator.TestValidate(new SendMessageRequest { Content = "Hello there." });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Empty_or_whitespace_content_fails(string content)
    {
        var result = _validator.TestValidate(new SendMessageRequest { Content = content });
        result.ShouldHaveValidationErrorFor(r => r.Content);
    }

    [Fact]
    public void Content_at_the_limit_passes_and_one_over_fails()
    {
        var atLimit = new string('a', ValidationRules.LongTextMax);
        _validator.TestValidate(new SendMessageRequest { Content = atLimit })
            .ShouldNotHaveValidationErrorFor(r => r.Content);

        var over = new string('a', ValidationRules.LongTextMax + 1);
        _validator.TestValidate(new SendMessageRequest { Content = over })
            .ShouldHaveValidationErrorFor(r => r.Content);
    }

    [Fact]
    public void Control_and_bidi_chars_fail()
    {
        _validator.TestValidate(new SendMessageRequest { Content = "bad" + (char)0x0000 })
            .ShouldHaveValidationErrorFor(r => r.Content);
        _validator.TestValidate(new SendMessageRequest { Content = "bad" + (char)0x202E })
            .ShouldHaveValidationErrorFor(r => r.Content);
    }

    [Fact]
    public void Unknown_model_id_charset_fails_but_safe_token_passes()
    {
        _validator.TestValidate(new SendMessageRequest { Content = "hi", ModelId = "qwen2.5:7b" })
            .ShouldNotHaveValidationErrorFor(r => r.ModelId);
        _validator.TestValidate(new SendMessageRequest { Content = "hi", ModelId = "bad model id" })
            .ShouldHaveValidationErrorFor(r => r.ModelId);
    }

    private static MessageAttachment Png(string data = "aGVsbG8=") =>
        new() { MimeType = "image/png", Data = data };

    [Fact]
    public void Image_only_message_with_empty_content_passes()
    {
        var result = _validator.TestValidate(
            new SendMessageRequest { Content = string.Empty, Attachments = [Png()] });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Empty_content_without_attachments_still_fails()
    {
        _validator.TestValidate(new SendMessageRequest { Content = string.Empty, Attachments = [] })
            .ShouldHaveValidationErrorFor(r => r.Content);
    }

    [Fact]
    public void Null_content_with_attachments_reports_missing_text_not_too_long()
    {
        // The DTO contract is non-null, but a client can still send
        // `"content": null` on the wire - the validator must report it
        // accurately, not as a bogus length failure.
        var result = _validator.TestValidate(
            new SendMessageRequest { Content = null!, Attachments = [Png()] });

        result.ShouldHaveValidationErrorFor(r => r.Content).WithErrorCode("text.missing");
        Assert.DoesNotContain(result.Errors, e => e.ErrorCode == "text.too_long");
    }

    [Fact]
    public void Content_with_attachments_keeps_the_character_and_length_bar()
    {
        _validator.TestValidate(new SendMessageRequest
        {
            Content = "bad" + (char)0x202E,
            Attachments = [Png()],
        }).ShouldHaveValidationErrorFor(r => r.Content);

        _validator.TestValidate(new SendMessageRequest
        {
            Content = new string('a', ValidationRules.LongTextMax + 1),
            Attachments = [Png()],
        }).ShouldHaveValidationErrorFor(r => r.Content);
    }

    [Fact]
    public void Disallowed_attachment_mime_fails_and_allowed_passes()
    {
        var result = _validator.TestValidate(new SendMessageRequest
        {
            Content = "look",
            Attachments = [new MessageAttachment { MimeType = "image/svg+xml", Data = "aGVsbG8=" }],
        });
        Assert.False(result.IsValid);

        _validator.TestValidate(new SendMessageRequest
        {
            Content = "look",
            Attachments = [new MessageAttachment { MimeType = "image/webp", Data = "aGVsbG8=" }],
        }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Malformed_base64_attachment_fails()
    {
        var result = _validator.TestValidate(new SendMessageRequest
        {
            Content = "look",
            Attachments = [Png("not base64!!")],
        });
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Too_many_attachments_fail()
    {
        var attachments = new List<MessageAttachment>();
        for (var i = 0; i <= ValidationRules.AttachmentMaxCount; i++)
        {
            attachments.Add(Png());
        }

        _validator.TestValidate(new SendMessageRequest { Content = "x", Attachments = attachments })
            .ShouldHaveValidationErrorFor(r => r.Attachments);
    }

    [Fact]
    public void Unknown_tool_id_fails_and_known_tool_passes()
    {
        var known = new ToolToggles(new Dictionary<string, bool> { ["rag"] = true });
        _validator.TestValidate(new SendMessageRequest { Content = "hi", Tools = known })
            .ShouldNotHaveAnyValidationErrors();

        var unknown = new ToolToggles(new Dictionary<string, bool> { ["nope"] = true });
        var result = _validator.TestValidate(new SendMessageRequest { Content = "hi", Tools = unknown });
        Assert.False(result.IsValid);
    }
}
