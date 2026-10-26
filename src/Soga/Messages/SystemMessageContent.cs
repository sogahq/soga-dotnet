using System.Text.Json;
using Soga.Contracts;

namespace Soga.Messages;

/// <summary>
/// Builds the machine-readable body of the system messages Soga posts automatically when group membership changes.
/// </summary>
/// <remarks>
/// The body is JSON, such as <c>{"code":"participant_added","actor":"alice","target":"bob"}</c>, rather than English
/// text, so clients can render it in each user's own language.
/// </remarks>
internal static class SystemMessageContent
{
    /// <summary>
    /// Creates the system message body for a participant change.
    /// </summary>
    /// <param name="data">The participant change to describe.</param>
    /// <returns>
    /// A JSON object with a <c>code</c> of <c>participant_added</c>, <c>participant_removed</c>,
    /// <c>participant_left</c>, or <c>participant_role_changed</c>, and the <c>actor</c> and <c>target</c> user IDs.
    /// </returns>
    public static string ForParticipantChange(ParticipantChangedData data)
    {
        var code = data.Change switch
        {
            "added" => "participant_added",
            "removed" => "participant_removed",
            "left" => "participant_left",
            _ => "participant_role_changed",
        };

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["code"] = code,
            ["actor"] = data.ActorUserId,
            ["target"] = data.UserId,
        });
    }
}
