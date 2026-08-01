// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentKick75.Hid.Windows;

public sealed class Win32HidConnectionFactory : IHidConnectionFactory
{
    public ValueTask<IHidReportConnection> OpenAsync(
        HidDeviceSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Win32 HID transport requires Windows.");
        }

        if (!selection.IsWritable || selection.Device is null || selection.Profile is null)
        {
            throw new InvalidOperationException(
                "The HID selection does not contain one descriptor-validated writable profile.");
        }

        if (!HidTransportProfiles.IsWritableAllowlisted(selection.Profile))
        {
            throw new InvalidOperationException(
                "The selected transport profile is not the explicitly allowlisted Kick75 USB write profile.");
        }

        if (selection.Device.InputReportByteLength != HidTransportProfile.ProtocolReportLength + 1 ||
            selection.Device.OutputReportByteLength != HidTransportProfile.ProtocolReportLength + 1)
        {
            throw new NotSupportedException(
                "Win32 WriteFile requires native 65-byte reports: Report ID 0 plus the complete " +
                "64-byte Kick75 protocol frame. A 64-byte HIDP_CAPS report is diagnostic-only " +
                "and cannot be opened for writes without truncating the protocol frame.");
        }

        if (!selection.Profile.MatchesWritableDescriptor(selection.Device))
        {
            throw new InvalidOperationException("The selected HID descriptor no longer matches its profile.");
        }

        SafeFileHandle handle = Win32HidNative.CreateFile(
            selection.Device.DevicePath,
            Win32HidNative.GenericRead | Win32HidNative.GenericWrite,
            0,
            IntPtr.Zero,
            Win32HidNative.OpenExisting,
            Win32HidNative.FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            Win32Exception inner = new(error);
            if (error is Win32HidNative.ErrorAccessDenied or Win32HidNative.ErrorSharingViolation)
            {
                throw new HidDeviceBusyException(
                    "The selected HID interface is already in use or access was denied.",
                    inner);
            }

            if (error is Win32HidNative.ErrorFileNotFound or
                Win32HidNative.ErrorPathNotFound or
                Win32HidNative.ErrorDeviceNotConnected)
            {
                throw new HidDeviceDisconnectedException(
                    "The selected HID interface disconnected before it could be opened.",
                    inner);
            }

            throw new HidTransportException("Unable to open the selected HID interface.", inner);
        }

        // HidD_FlushQueue removes reports that were queued before this handle
        // became the owner of a new protocol session. It must happen before the
        // first 0xEE write; flushing after a write could discard that command's
        // response and make request/response correlation less trustworthy.
        if (!Win32HidNative.HidD_FlushQueue(handle))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new HidTransportException(
                "Unable to clear pending HID input reports before starting a protocol session.",
                new Win32Exception(error));
        }

        try
        {
            IHidReportConnection connection = new Win32HidReportConnection(
                selection.Device,
                selection.State,
                handle);
            return ValueTask.FromResult(connection);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}

internal sealed class Win32HidReportConnection : IHidReportConnection
{
    private readonly FileStream stream;
    private bool disposed;

    internal Win32HidReportConnection(
        HidInterfaceDescriptor device,
        HidDeviceState initialState,
        SafeFileHandle handle)
    {
        Device = device;
        State = initialState;
        int bufferSize = Math.Max(device.InputReportByteLength, device.OutputReportByteLength);
        stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize, isAsync: true);
    }

    public HidInterfaceDescriptor Device { get; }

    public HidDeviceState State { get; private set; }

    public async ValueTask WriteReportAsync(
        ReadOnlyMemory<byte> protocolFrame,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] nativeReport = HidReportBufferAdapter.ToNativeOutput(
            protocolFrame.Span,
            Device.OutputReportByteLength);
        using CancellationTokenSource timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        try
        {
            await stream.WriteAsync(nativeReport, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            State = HidDeviceState.Unresponsive;
            throw new TimeoutException("Timed out writing a HID output report.", exception);
        }
        catch (IOException exception)
        {
            if (IsDisconnectError(exception))
            {
                State = HidDeviceState.Disconnected;
                throw new HidDeviceDisconnectedException(
                    "The HID interface disconnected while writing an output report.",
                    exception);
            }

            State = HidDeviceState.Faulted;
            throw new HidTransportException("Failed to write a HID output report.", exception);
        }
    }

    public async ValueTask<byte[]> ReadReportAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        byte[] nativeReport = new byte[Device.InputReportByteLength];
        using CancellationTokenSource timeoutSource = CreateTimeoutSource(timeout, cancellationToken);
        try
        {
            int offset = 0;
            while (offset < nativeReport.Length)
            {
                int read = await stream.ReadAsync(
                    nativeReport.AsMemory(offset),
                    timeoutSource.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    State = HidDeviceState.Disconnected;
                    throw new HidDeviceDisconnectedException(
                        "The HID interface closed while awaiting an input report.");
                }

                offset += read;
            }

            return HidReportBufferAdapter.FromNativeInput(nativeReport);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            State = HidDeviceState.Unresponsive;
            throw new TimeoutException("Timed out waiting for a HID input report.", exception);
        }
        catch (IOException exception) when (exception is not HidTransportException)
        {
            if (IsDisconnectError(exception))
            {
                State = HidDeviceState.Disconnected;
                throw new HidDeviceDisconnectedException(
                    "The HID interface disconnected while reading an input report.",
                    exception);
            }

            State = HidDeviceState.Faulted;
            throw new HidTransportException("Failed to read a HID input report.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        State = HidDeviceState.Disconnected;
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static CancellationTokenSource CreateTimeoutSource(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive or infinite.");
        }

        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            source.CancelAfter(timeout);
        }

        return source;
    }

    private static bool IsDisconnectError(IOException exception)
    {
        int win32Error = exception.HResult & 0xFFFF;
        return win32Error is
            Win32HidNative.ErrorFileNotFound or
            Win32HidNative.ErrorPathNotFound or
            Win32HidNative.ErrorDeviceNotConnected;
    }
}
