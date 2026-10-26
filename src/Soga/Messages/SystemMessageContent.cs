using System.Text.Json;
using Soga.Contracts;

namespace Soga.Messages;

/// <summary>
/// Builds the machine-readable body of automatic system messages: <c>{"code":"participant_added","actor":"a","target":"b"}</c>.
/// Clients render it in the user's language. It is JSON, not English, so the server never localizes.
/// </summary>
internal static class SystemMessageContent
{
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
