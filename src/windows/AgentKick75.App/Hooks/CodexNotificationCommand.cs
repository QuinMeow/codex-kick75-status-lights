// SPDX-License-Identifier: MIT
using System.Diagnostics;
using System.Text.Json;
using AgentKick75.App.Ipc;
using AgentKick75.Core.Hooks;

namespace AgentKick75.App.Hooks;

public static class CodexNotificationCommand
{
    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> arguments,
        IPipeRequestClient pipeClient,
        CancellationToken cancellationToken = default)
    {
        string? json = arguments.Count >= 3 ? arguments[^1] : null;
        try
        {
            if (TryCreateEnvelope(json, out PipeEnvelope? envelope) && envelope is not null)
            {
                _ = await pipeClient.SendAsync(
                    envelope,
                    expectResponse: false,
                    HookCommand.PipeTimeout,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }

        await ForwardAsync(arguments, json, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static bool TryCreateEnvelope(string? json, out PipeEnvelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!TryGetString(root, "type", out string? type)
            || type != "agent-turn-complete"
            || !TryGetString(root, "thread-id", out string? sessionId)
            || string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        _ = TryGetString(root, "turn-id", out string? turnId);
        envelope = PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
        {
            kind = (int)CodexHookEventKind.Stop,
            sessionId,
            turnId,
            toolName = (string?)null,
        });
        return true;
    }

    private static async Task ForwardAsync(
        IReadOnlyList<string> arguments,
        string? json,
        CancellationToken cancellationToken)
    {
        if (json is null || arguments.Count < 5 || arguments[2] != "--forward")
        {
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = arguments[3],
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            for (int index = 4; index < arguments.Count - 1; index++)
            {
                startInfo.ArgumentList.Add(arguments[index]);
            }

            startInfo.ArgumentList.Add(json);
            using Process? process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString()) is not null;
    }
}
