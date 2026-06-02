using System.Collections.Generic;
using FluentValidation;
using FluentValidation.TestHelper;
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
