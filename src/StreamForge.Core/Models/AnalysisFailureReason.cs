namespace StreamForge.Core.Models;

public enum AnalysisFailureReason
{
    None,
    NoSupportedMedia,
    AuthenticationRequired,
    BotChallenge,
    AuthorizationExpired,
    DrmProtected,
    UnsupportedProtocol,
    Timeout
}
