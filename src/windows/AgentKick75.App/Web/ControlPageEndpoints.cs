// SPDX-License-Identifier: MIT
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentKick75.App.Diagnostics;
using AgentKick75.App.Ipc;
using AgentKick75.Core.Lighting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;

namespace AgentKick75.App.Web;

internal static class ControlPageEndpoints
{
    private const int MaxConcurrentEventStreams = 4;
    private const int DefaultDiagnosticLimit = 50;
    private const int MaximumDiagnosticLimit = 100;
    private static readonly TimeSpan EventHeartbeatInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EventWriteDeadline = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions EventJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static IEndpointRouteBuilder MapAgentKick75ControlPage(
        this IEndpointRouteBuilder endpoints,
        IControlPlane controlPlane,
        ControlPageOptions options,
        ISanitizedDiagnosticLog? diagnosticLog = null,
        Func<PipeEnvelope, CancellationToken, ValueTask<PipeEnvelope?>>? hookHandler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(options);

        var eventStreamSlots = new SemaphoreSlim(
            MaxConcurrentEventStreams,
            MaxConcurrentEventStreams);

        endpoints.MapGet("/", context => WriteAssetAsync(
            context,
            ControlPageAssets.IndexHtml.Replace(
                ControlPageAssets.TokenPlaceholder,
                options.WriteToken,
                StringComparison.Ordinal),
            "text/html; charset=utf-8"));
        endpoints.MapGet("/index.html", context => WriteAssetAsync(
            context,
            ControlPageAssets.IndexHtml.Replace(
                ControlPageAssets.TokenPlaceholder,
                options.WriteToken,
                StringComparison.Ordinal),
            "text/html; charset=utf-8"));
        endpoints.MapGet("/styles.css", context => WriteAssetAsync(
            context,
            ControlPageAssets.StylesCss,
            "text/css; charset=utf-8"));
        endpoints.MapGet("/app.js", context => WriteAssetAsync(
            context,
            ControlPageAssets.AppJavaScript,
            "text/javascript; charset=utf-8"));

        RouteGroupBuilder api = endpoints.MapGroup("/api/v1");

        api.MapGet("/status", async (CancellationToken cancellationToken) =>
            Results.Ok(ControlPlanePrivacy.SanitizeStatus(
                await controlPlane.GetStatusAsync(cancellationToken))));

        api.MapGet("/settings", async (CancellationToken cancellationToken) =>
            Results.Ok(await controlPlane.GetSettingsAsync(cancellationToken)));

        api.MapGet("/diagnostics", (
            HttpContext context,
            CancellationToken cancellationToken) =>
            ReadDiagnosticsAsync(context, diagnosticLog, cancellationToken));

        if (hookHandler is not null)
        {
            api.MapPost("/hooks/codex", async (
                PipeEnvelope envelope,
                CancellationToken cancellationToken) =>
            {
                if (envelope.Version != PipeEnvelope.CurrentVersion ||
                    envelope.Kind != PipeMessageKinds.HookEvent)
                {
                    return Results.BadRequest();
                }

                PipeEnvelope? response = await hookHandler(envelope, cancellationToken);
                return response?.Kind == PipeMessageKinds.Rejected
                    ? Results.BadRequest()
                    : Results.Accepted();
            });
        }

        api.MapPut("/settings", async (
            ControlSettingsDto settings,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeSettings(settings, out ControlSettingsDto? normalized, out var errors))
            {
                return Results.ValidationProblem(errors);
            }

            ControlSettingsDto applied = await controlPlane.ApplySettingsAsync(
                normalized!,
                cancellationToken);
            return Results.Ok(applied);
        });

        api.MapPost("/preview/{state}", async (
            string state,
            CancellationToken cancellationToken) =>
        {
            if (!TryParsePreviewState(state, out ControlPreviewState parsedState))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["state"] = ["State must be thinking, requires-input, complete, or interrupted."],
                });
            }

            await controlPlane.PreviewAsync(parsedState, options.PreviewDuration, cancellationToken);
            return Results.Ok(new { durationSeconds = 3 });
        });

        api.MapPost("/pause", async (
            PauseRequestDto request,
            CancellationToken cancellationToken) =>
            Results.Ok(ControlPlanePrivacy.SanitizeStatus(
                await controlPlane.SetPausedAsync(request.Paused, cancellationToken))));

        api.MapPost("/hooks/install", async (CancellationToken cancellationToken) =>
            Results.Ok(await controlPlane.InstallCodexHooksAsync(cancellationToken)));

        api.MapGet("/events", context => StreamEventsAsync(
            context,
            controlPlane,
            eventStreamSlots));

        return endpoints;
    }

    private static async Task<IResult> ReadDiagnosticsAsync(
        HttpContext context,
        ISanitizedDiagnosticLog? diagnosticLog,
        CancellationToken cancellationToken)
    {
        if (!TryReadDiagnosticLimit(context.Request, out int limit))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["limit"] = ["Limit must be one integer from 1 through 100."],
            });
        }

        if (diagnosticLog is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        IReadOnlyList<SanitizedDiagnosticEntry> entries;
        try
        {
            entries = await diagnosticLog.ReadRecentAsync(limit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Diagnostics are optional. Never reflect filesystem paths or reader
            // exception text through the local HTTP surface.
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var projected = new List<PersistentDiagnosticEntryDto>(Math.Min(limit, entries.Count));
        foreach (SanitizedDiagnosticEntry entry in entries.Take(limit))
        {
            if (PersistentDiagnosticPrivacy.TryProject(entry, out PersistentDiagnosticEntryDto? safe))
            {
                projected.Add(safe!);
            }
        }

        return Results.Ok(projected);
    }

    private static bool TryReadDiagnosticLimit(HttpRequest request, out int limit)
    {
        limit = DefaultDiagnosticLimit;
        if (request.Query.Any(item => !string.Equals(
                item.Key,
                "limit",
                StringComparison.Ordinal)) ||
            request.Query["limit"].Count > 1)
        {
            return false;
        }

        string? rawLimit = request.Query["limit"].SingleOrDefault();
        if (rawLimit is null)
        {
            return true;
        }

        return int.TryParse(
                rawLimit,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out limit) &&
            limit is >= 1 and <= MaximumDiagnosticLimit;
    }

    private static bool TryNormalizeSettings(
        ControlSettingsDto? settings,
        out ControlSettingsDto? normalized,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        normalized = null;

        if (settings is null)
        {
            errors["settings"] = ["A settings object is required."];
            return false;
        }

        ControlLightStyleDto? thinking = NormalizeStyle(settings.Thinking, "thinking", errors);
        ControlLightStyleDto? requiresInput = NormalizeStyle(
            settings.RequiresInput,
            "requiresInput",
            errors);
        ControlLightStyleDto? complete = NormalizeStyle(settings.Complete, "complete", errors);
        ControlLightStyleDto? interrupted = NormalizeStyle(
            settings.Interrupted ?? new ControlLightStyleDto("#FF3B30", 100),
            "interrupted",
            errors);

        if (settings.CompleteHoldSeconds is < 1 or > 3600)
        {
            errors["completeHoldSeconds"] = ["Complete hold duration must be between 1 and 3600 seconds."];
        }

        string keepAwakePolicy = settings.KeepAwakePolicy?.Trim() ?? string.Empty;
        if (keepAwakePolicy is not ("disabled" or "codexActive" or "hostRunning"))
        {
            errors["keepAwakePolicy"] = ["Keep-awake policy must be disabled, codexActive, or hostRunning."];
        }

        string keepAwakeRegion = settings.KeepAwakeRegion?.Trim() ?? string.Empty;
        if (keepAwakeRegion != "sideLights")
        {
            errors["keepAwakeRegion"] = ["Only side-light keep-awake is supported."];
        }

        if (settings.KeepAwakeRefreshSeconds is < 10 or > 300)
        {
            errors["keepAwakeRefreshSeconds"] = ["Keep-awake refresh must be between 10 and 300 seconds."];
        }

        if (errors.Count != 0)
        {
            return false;
        }

        normalized = new ControlSettingsDto(
            thinking!,
            requiresInput!,
            complete!,
            settings.CompleteHoldSeconds,
            settings.LaunchAtSignIn,
            interrupted!,
            keepAwakePolicy,
            settings.KeepAwakeRefreshSeconds,
            keepAwakeRegion);
        return true;
    }

    private static ControlLightStyleDto? NormalizeStyle(
        ControlLightStyleDto? style,
        string fieldName,
        Dictionary<string, string[]> errors)
    {
        if (style is null)
        {
            errors[fieldName] = ["A lighting style is required."];
            return null;
        }

        string color = style.Color?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!IsHexColor(color))
        {
            errors[$"{fieldName}.color"] = ["Color must use #RRGGBB format."];
        }

        if (style.Brightness is < 0 or > 100)
        {
            errors[$"{fieldName}.brightness"] = ["Brightness must be between 0 and 100."];
        }

        string effect = style.Effect?.Trim().ToLowerInvariant() ?? string.Empty;
        if (effect is not ("flowing" or "static" or "breathing"))
        {
            errors[$"{fieldName}.effect"] = ["Effect must be flowing, static, or breathing."];
        }

        if (style.Speed is < LightStyle.MinimumSpeed or > LightStyle.MaximumSpeed)
        {
            errors[$"{fieldName}.speed"] =
                [$"Speed must be between {LightStyle.MinimumSpeed} and {LightStyle.MaximumSpeed}."];
        }

        return new ControlLightStyleDto(color, style.Brightness, effect, style.Speed);
    }

    private static bool IsHexColor(string color)
    {
        if (color.Length != 7 || color[0] != '#')
        {
            return false;
        }

        for (int index = 1; index < color.Length; index++)
        {
            if (!char.IsAsciiHexDigit(color[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryParsePreviewState(string state, out ControlPreviewState parsedState)
    {
        switch (state.Trim().ToLowerInvariant())
        {
            case "thinking":
                parsedState = ControlPreviewState.Thinking;
                return true;
            case "requires-input":
                parsedState = ControlPreviewState.RequiresInput;
                return true;
            case "complete":
                parsedState = ControlPreviewState.Complete;
                return true;
            case "interrupted":
                parsedState = ControlPreviewState.Interrupted;
                return true;
            default:
                parsedState = default;
                return false;
        }
    }

    private static Task StreamEventsAsync(
        HttpContext context,
        IControlPlane controlPlane,
        SemaphoreSlim eventStreamSlots)
    {
        return StreamEventsCoreAsync(
            context,
            controlPlane,
            eventStreamSlots,
            EventWriteDeadline);
    }

    internal static async Task StreamEventsCoreAsync(
        HttpContext context,
        IControlPlane controlPlane,
        SemaphoreSlim eventStreamSlots,
        TimeSpan eventWriteDeadline)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(controlPlane);
        ArgumentNullException.ThrowIfNull(eventStreamSlots);
        if (eventWriteDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(eventWriteDeadline));
        }

        if (!await eventStreamSlots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "15";
            return;
        }

        try
        {
            IAsyncEnumerable<ControlEventDto> eventStream =
                controlPlane.WatchEventsAsync(context.RequestAborted);
            await using IAsyncEnumerator<ControlEventDto> events =
                eventStream.GetAsyncEnumerator(context.RequestAborted);

            // Start the first read before the connected frame. Implementations register
            // on WatchEventsAsync, while this also protects against a lazy iterator.
            Task<bool> pendingEvent = events.MoveNextAsync().AsTask();

            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/event-stream; charset=utf-8";
            context.Response.Headers["Connection"] = "keep-alive";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            IHttpMinResponseDataRateFeature? responseRate =
                context.Features.Get<IHttpMinResponseDataRateFeature>();
            if (responseRate is not null)
            {
                responseRate.MinDataRate = null;
            }

            await WriteEventPayloadAsync(
                context,
                "retry: 3000\n\n",
                eventWriteDeadline);
            await WriteEventAsync(
                context,
                new ControlEventDto(0, "connected", DateTimeOffset.UtcNow),
                eventWriteDeadline);

            using var heartbeatTimer = new PeriodicTimer(EventHeartbeatInterval);
            Task<bool> pendingHeartbeat = heartbeatTimer
                .WaitForNextTickAsync(context.RequestAborted)
                .AsTask();
            while (!context.RequestAborted.IsCancellationRequested)
            {
                Task completed = await Task.WhenAny(pendingEvent, pendingHeartbeat);

                if (completed == pendingEvent)
                {
                    if (!await pendingEvent)
                    {
                        break;
                    }

                    await WriteEventAsync(context, events.Current, eventWriteDeadline);
                    pendingEvent = events.MoveNextAsync().AsTask();
                    continue;
                }

                if (!await pendingHeartbeat)
                {
                    break;
                }

                await WriteEventPayloadAsync(
                    context,
                    ": keep-alive\n\n",
                    eventWriteDeadline);
                pendingHeartbeat = heartbeatTimer
                    .WaitForNextTickAsync(context.RequestAborted)
                    .AsTask();
            }
        }
        finally
        {
            eventStreamSlots.Release();
        }
    }

    private static async Task WriteEventAsync(
        HttpContext context,
        ControlEventDto controlEvent,
        TimeSpan eventWriteDeadline)
    {
        ControlEventDto safeEvent = ControlPlanePrivacy.SanitizeEvent(controlEvent);
        string json = JsonSerializer.Serialize(safeEvent, EventJsonOptions);
        await WriteEventPayloadAsync(
            context,
            $"data: {json}\n\n",
            eventWriteDeadline);
    }

    private static async Task WriteEventPayloadAsync(
        HttpContext context,
        string payload,
        TimeSpan eventWriteDeadline)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted);
        deadline.CancelAfter(eventWriteDeadline);

        try
        {
            await context.Response.WriteAsync(payload, deadline.Token);
            await context.Response.Body.FlushAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        {
            context.Abort();
            throw new TimeoutException("The event-stream client did not accept a response in time.");
        }
    }

    private static Task WriteAssetAsync(HttpContext context, string content, string contentType)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        return context.Response.WriteAsync(content, context.RequestAborted);
    }
}
