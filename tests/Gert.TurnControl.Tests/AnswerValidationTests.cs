using FluentAssertions;
using Gert.Model.Events;
using Gert.TurnControl;
using Xunit;

namespace Gert.TurnControl.Tests;

/// <summary>
/// The single-sourced closed-question fit rule (chat-and-tools.md section ask the user): one answer
/// per asked question, a closed question answered with an offered option, free text otherwise.
/// </summary>
public sealed class AnswerValidationTests
{
    private static AskedQuestion Closed(params string[] options) =>
        new("Which color?", null, options, AllowFreeText: false);

    private static AskedQuestion FreeText() =>
        new("Anything else?", null, [], AllowFreeText: true);

    [Fact]
    public void A_closed_question_accepts_an_offered_option()
    {
        AnswerValidation.Fits([Closed("red", "blue")], ["blue"]).Should().BeTrue();
    }

    [Fact]
    public void A_closed_question_rejects_an_off_menu_answer()
    {
        AnswerValidation.Fits([Closed("red", "blue")], ["green"]).Should().BeFalse();
    }

    [Fact]
    public void A_free_text_question_accepts_any_value()
    {
        AnswerValidation.Fits([FreeText()], ["whatever the user typed"]).Should().BeTrue();
    }

    [Fact]
    public void A_count_mismatch_never_fits()
    {
        AnswerValidation.Fits([Closed("red", "blue"), FreeText()], ["blue"]).Should().BeFalse();
        AnswerValidation.Fits([FreeText()], ["a", "b"]).Should().BeFalse();
    }

    [Fact]
    public void Each_answer_is_matched_to_its_own_question_in_order()
    {
        var questions = new[] { Closed("red", "blue"), FreeText(), Closed("yes", "no") };

        AnswerValidation.Fits(questions, ["blue", "note", "yes"]).Should().BeTrue();

        // The third answer is off-menu for its closed question, even though the first two fit.
        AnswerValidation.Fits(questions, ["blue", "note", "maybe"]).Should().BeFalse();
    }
}
