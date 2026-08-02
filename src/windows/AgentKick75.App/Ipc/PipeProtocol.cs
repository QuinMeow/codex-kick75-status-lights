// SPDX-License-Identifier: MIT
using System.Buffers.Binary;
using System.Text.Json;

namespace AgentKick75.App.Ipc;

public sealed record PipeEnvelope(int Version, string Kind, JsonElement Payload)
{
    public const int CurrentVersion = 1;

    public static PipeEnvelope Create<T>(string kind, T payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        return new(CurrentVersion, kind, JsonSerializer.SerializeToElement(payload, PipeJson.Options));
    }
}

public static class PipeMessageKinds
{
    public const string HookEvent = "hook-event";
    public const string StatusRequest = "status-request";
    public const string StatusResponse = "status-response";
    public const string PrepareUninstallRequest = "prepare-uninstall-request";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

public sealed class PipeProtocolException : Exception
{
    public PipeProtocolException(string message)
        : base(message)
    {
    }
}

public static class PipeMessageSchema
{
    public static void Validate(PipeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (envelope.Kind is PipeMessageKinds.StatusRequest or PipeMessageKinds.PrepareUninstallRequest &&
            !IsStrictEmptyObject(envelope.Payload))
        {
            throw new PipeProtocolException(
                "A status or prepare-uninstall request payload must be an empty JSON object.");
        }
    }

    public static bool IsStrictEmptyObject(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object &&
            !payload.EnumerateObject().Any();
    }
}

public static class PipeFraming
{
    public const int MaximumMessageBytes = 32 * 1024;
    private const int HeaderLength = sizeof(int);

    public static async ValueTask WriteAsync(
        Stream stream,
        PipeEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(envelope);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(envelope, PipeJson.Options);
        if (json.Length > MaximumMessageBytes)
        {
            throw new PipeProtocolException($"Pipe message exceeds {MaximumMessageBytes} bytes.");
        }

        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<PipeEnvelope> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[HeaderLength];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaximumMessageBytes)
        {
            throw new PipeProtocolException("Pipe message has an invalid length.");
        }

        byte[] json = new byte[length];
        await ReadExactlyAsync(stream, json, cancellationToken).ConfigureAwait(false);

        PipeEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<PipeEnvelope>(json, PipeJson.Options);
        }
        catch (JsonException exception)
        {
            throw new PipeProtocolException($"Pipe message is not valid JSON: {exception.Message}");
        }

        if (envelope is null || string.IsNullOrWhiteSpace(envelope.Kind))
        {
            throw new PipeProtocolException("Pipe message envelope is incomplete.");
        }

        if (envelope.Version != PipeEnvelope.CurrentVersion)
        {
            throw new PipeProtocolException($"Unsupported pipe protocol version {envelope.Version}.");
        }

        PipeMessageSchema.Validate(envelope);

        return envelope;
    }

    private static async ValueTask ReadExactlyAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Pipe closed before a complete message was received.");
            }

            offset += read;
        }
    }
}

internal static class PipeJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };
}
