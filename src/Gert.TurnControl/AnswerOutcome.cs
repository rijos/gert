namespace Gert.TurnControl;

/// <summary>
/// The result of submitting an <c>ask_user</c> answer to the bus (rest-api.md section answer a
/// question), mapped by the endpoint to a status code:
/// <list type="bullet">
///   <item><see cref="Accepted"/> - delivered to the waiting turn (202);</item>
///   <item><see cref="NoSuchQuestion"/> - nothing pending, the question id is stale, or the scope
///   addresses no live turn / another tenant's turn (404, indistinguishable);</item>
///   <item><see cref="Invalid"/> - the answer count mismatches or names no offered option (400).</item>
/// </list>
/// </summary>
public enum AnswerOutcome
{
    /// <summary>Validated and delivered to the waiting turn (202).</summary>
    Accepted,

    /// <summary>No matching open question for the scope (404).</summary>
    NoSuchQuestion,

    /// <summary>The answer does not fit the asked question (400).</summary>
    Invalid,
}
