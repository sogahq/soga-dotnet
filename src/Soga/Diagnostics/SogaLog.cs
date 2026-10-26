using Microsoft.Extensions.Logging;

namespace Soga.Diagnostics;

internal static partial class SogaLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled Soga endpoint failure.")]
    public static partial void EndpointFailure(ILogger logger, Exception exception);
}
