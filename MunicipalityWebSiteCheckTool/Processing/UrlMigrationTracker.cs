using MunicipalityWebSiteCheckTool.Domain;

namespace MunicipalityWebSiteCheckTool.Processing;

public static class UrlMigrationTracker
{
    public static UrlMigrationState Update(UrlMigrationState? previousState, UrlMigrationHint? hint)
    {
        var current = previousState ?? new UrlMigrationState();
        if (hint is null)
        {
            return current with
            {
                CurrentFingerprint = null,
                ConsecutiveCount = 0,
                LastReason = null,
                LastCandidateUrl = null,
                LastConfidence = null
            };
        }

        var nextCount = string.Equals(current.CurrentFingerprint, hint.Fingerprint, StringComparison.OrdinalIgnoreCase)
            ? current.ConsecutiveCount + 1
            : 1;

        return current with
        {
            CurrentFingerprint = hint.Fingerprint,
            ConsecutiveCount = nextCount,
            LastReason = hint.Reason,
            LastCandidateUrl = hint.CandidateUrl,
            LastConfidence = hint.Confidence
        };
    }

    public static bool ShouldNotify(UrlMigrationState? state, UrlMigrationHint? hint)
    {
        if (state is null || hint is null)
        {
            return false;
        }

        var threshold = hint.Confidence switch
        {
            UrlMigrationConfidences.High => 1,
            UrlMigrationConfidences.Medium => 2,
            _ => 3
        };

        return state.ConsecutiveCount >= threshold &&
               !string.Equals(state.LastNotifiedFingerprint, hint.Fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public static UrlMigrationState MarkNotified(UrlMigrationState state, UrlMigrationHint hint, DateTimeOffset notifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(hint);

        return state with
        {
            LastNotifiedFingerprint = hint.Fingerprint,
            LastNotifiedUtc = notifiedUtc
        };
    }
}
