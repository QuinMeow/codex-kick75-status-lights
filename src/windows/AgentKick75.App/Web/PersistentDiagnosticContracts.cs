// SPDX-License-Identifier: MIT
using System.Text.Json;
using AgentKick75.App.Diagnostics;

namespace AgentKick75.App.Web;

/// <summary>
/// Page-safe projection of one persisted diagnostic entry. This contract omits
/// SessionHash and contains no free-text fields, file metadata, or device identity.
/// </summary>
public sealed record PersistentDiagnosticEntryDto(
    DateTimeOffset Timestamp,
    string EventType,
    string? VisualState,
    long? LatencyMilliseconds,
    string? TransportFailure,
    string? Code);

internal static class PersistentDiagnosticPrivacy
{
    private const long MaximumLatencyMilliseconds = 24 * 60 * 60 * 1000;

    public static bool TryProject(
        SanitizedDiagnosticEntry entry,
        out PersistentDiagnosticEntryDto? projected)
    {
        ArgumentNullException.ThrowIfNull(entry);
        projected = null;

        if (!Enum.IsDefined(entry.EventType) ||
            (entry.VisualState is not null && !Enum.IsDefined(entry.VisualState.Value)) ||
            (entry.TransportFailure is not null && !Enum.IsDefined(entry.TransportFailure.Value)) ||
            (entry.Code is not null && !Enum.IsDefined(entry.Code.Value)) ||
            entry.LatencyMilliseconds is < 0 or > MaximumLatencyMilliseconds)
        {
            return false;
        }

        projected = new PersistentDiagnosticEntryDto(
            entry.TimestampUtc.ToUniversalTime(),
            ToJsonToken(entry.EventType),
            entry.VisualState is null ? null : ToJsonToken(entry.VisualState.Value),
            entry.LatencyMilliseconds,
            entry.TransportFailure is null ? null : ToJsonToken(entry.TransportFailure.Value),
            entry.Code is null ? null : ToJsonToken(entry.Code.Value));
        return true;
    }

    private static string ToJsonToken<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        return JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    }
}
