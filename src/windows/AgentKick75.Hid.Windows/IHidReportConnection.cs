// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public interface IHidReportConnection : IAsyncDisposable
{
    HidInterfaceDescriptor Device { get; }

    HidDeviceState State { get; }

    ValueTask WriteReportAsync(
        ReadOnlyMemory<byte> protocolFrame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    ValueTask<byte[]> ReadReportAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public interface IHidConnectionFactory
{
    ValueTask<IHidReportConnection> OpenAsync(
        HidDeviceSelection selection,
        CancellationToken cancellationToken = default);
}

public class HidTransportException : IOException
{
    public HidTransportException(string message)
        : base(message)
    {
    }

    public HidTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class HidDeviceBusyException : HidTransportException
{
    public HidDeviceBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class HidDeviceDisconnectedException : HidTransportException
{
    public HidDeviceDisconnectedException(string message)
        : base(message)
    {
    }

    public HidDeviceDisconnectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
