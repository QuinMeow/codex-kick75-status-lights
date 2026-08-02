// SPDX-License-Identifier: MIT
using System.Text;
using AgentKick75.App.Ipc;
using AgentKick75.Core.Hooks;

namespace AgentKick75.App.Hooks;

public static class HookCommand
{
    public const int MaximumInputBytes = CodexHookNormalizer.DefaultMaxInputBytes;
    public static readonly TimeSpan PipeTimeout = TimeSpan.FromMilliseconds(250);

    public static async Task<int> ExecuteAsync(
        TextReader standardInput,
        IPipeRequestClient pipeClient,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(
            standardInput,
            TextWriter.Null,
            pipeClient,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> ExecuteAsync(
        TextReader standardInput,
        TextWriter standardOutput,
        IPipeRequestClient pipeClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(pipeClient);

        // Hooks are deliberately fail-open. Neither malformed input nor an offline
        // Host may write to stdout/stderr or block the Codex command handler.
        bool isStop = false;
        try
        {
            string? input = await ReadLimitedAsync(standardInput, cancellationToken).ConfigureAwait(false);
            CodexHookEvent? hook = input is null
                ? null
                : new CodexHookNormalizer(MaximumInputBytes).Normalize(input);
            if (hook is null)
            {
                return 0;
            }

            isStop = hook.Kind == CodexHookEventKind.Stop;

            // Project the normalized record onto the exact IPC allowlist. Domain
            // model convenience properties must never become wire fields merely
            // because they are public getters.
            PipeEnvelope envelope = PipeEnvelope.Create(PipeMessageKinds.HookEvent, new
            {
                kind = (int)hook.Kind,
                sessionId = hook.SessionId,
                turnId = hook.TurnId,
                toolName = hook.ToolName,
            });
            _ = await pipeClient.SendAsync(
                envelope,
                expectResponse: false,
                PipeTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fail-open is a product requirement. The Host status/logging path owns
            // diagnostics; the synchronous hook remains completely silent.
        }

        if (isStop)
        {
            try
            {
                // Match the upstream helper: Stop expects valid JSON on stdout.
                // An empty object acknowledges the event without steering Codex.
                await standardOutput.WriteLineAsync("{}").ConfigureAwait(false);
                await standardOutput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Output failure is also fail-open.
            }
        }

        return 0;
    }

    private static async ValueTask<string?> ReadLimitedAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[MaximumInputBytes + 1];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total == buffer.Length)
        {
            return null;
        }

        string input = new(buffer, 0, total);
        return Encoding.UTF8.GetByteCount(input) <= MaximumInputBytes ? input : null;
    }
}
