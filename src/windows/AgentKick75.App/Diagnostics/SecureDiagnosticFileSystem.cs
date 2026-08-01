// SPDX-License-Identifier: MIT
using System.ComponentModel;
using System.Runtime.InteropServices;
using AgentKick75.Core.Storage;
using Microsoft.Win32.SafeHandles;

namespace AgentKick75.App.Diagnostics;

/// <summary>
/// Inspects every component of the diagnostic directory through a Windows
/// handle and holds a leaf-directory lease with DELETE access that does not
/// share DELETE. This prevents the validated log directory from being renamed,
/// deleted, or replaced for the lifetime of the log instance. File operations
/// open the terminal entry with OPEN_REPARSE_POINT and keep the validated handle
/// for the complete read, write, or delete operation.
/// </summary>
internal sealed class SecureDiagnosticFileSystem : IDisposable
{
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileTypeDisk = 0x0001;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int FileDispositionInfoClass = 4;

    private readonly string directoryPath;
    private readonly IReadOnlyList<SafeFileHandle> directoryLeases;
    private int disposed;

    private SecureDiagnosticFileSystem(
        string directoryPath,
        IReadOnlyList<SafeFileHandle> directoryLeases)
    {
        this.directoryPath = directoryPath;
        this.directoryLeases = directoryLeases;
    }

    public string DirectoryPath => directoryPath;

    public static SecureDiagnosticFileSystem Acquire(string directoryPath)
    {
        string securedPath = UserDataDirectorySecurity.EnsureSecureDirectory(directoryPath);
        var leases = new List<SafeFileHandle>();
        try
        {
            foreach (string component in EnumerateDirectoryComponents(securedPath))
            {
                uint desiredAccess = FileReadAttributes;
                if (string.Equals(component, securedPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Requesting DELETE on the leaf makes the sharing contract
                    // explicit: a rename/delete requires a second DELETE handle,
                    // which conflicts with this lease's missing FILE_SHARE_DELETE.
                    desiredAccess |= DeleteAccess;
                }

                SafeFileHandle handle = OpenNative(
                    component,
                    desiredAccess,
                    FileShareRead | FileShareWrite,
                    OpenExisting,
                    FileFlagBackupSemantics | FileFlagOpenReparsePoint);
                try
                {
                    NativeFileInformation information = GetInformation(handle, component);
                    if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                        (information.FileAttributes & FileAttributeReparsePoint) != 0)
                    {
                        throw new IOException(
                            $"Diagnostic directory component '{component}' is not a trusted directory.");
                    }

                    leases.Add(handle);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            // Re-run ACL and tree validation after every path component was
            // inspected. The leaf DELETE lease now prevents direct replacement
            // of the validated diagnostic directory until Dispose.
            string revalidatedPath = UserDataDirectorySecurity.EnsureSecureDirectory(securedPath);
            if (!string.Equals(revalidatedPath, securedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The diagnostic directory resolved outside its secured location.");
            }

            var fileSystem = new SecureDiagnosticFileSystem(securedPath, leases.AsReadOnly());
            fileSystem.ValidateDirectoryLeases();
            return fileSystem;
        }
        catch
        {
            DisposeHandles(leases);
            throw;
        }
    }

    public void ValidateDirectoryLeases()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        foreach (SafeFileHandle handle in directoryLeases)
        {
            NativeFileInformation information = GetInformation(handle, directoryPath);
            if ((information.FileAttributes & FileAttributeDirectory) == 0 ||
                (information.FileAttributes & FileAttributeReparsePoint) != 0)
            {
                throw new IOException("A diagnostic directory lease is no longer trusted.");
            }
        }
    }

    public bool TryCreateNewWriteStream(string path, out FileStream? stream)
    {
        ValidateDirectoryLeases();
        string fullPath = ValidateChildPath(path);
        SafeFileHandle handle = CreateFileW(
            fullPath,
            GenericWrite | FileReadAttributes | DeleteAccess,
            FileShareRead,
            IntPtr.Zero,
            CreateNew,
            FileAttributeNormal |
                FileFlagOverlapped |
                FileFlagOpenReparsePoint |
                FileFlagSequentialScan,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileExists or ErrorAlreadyExists)
            {
                stream = null;
                return false;
            }

            throw CreateNativeException("create diagnostic log", fullPath, error);
        }

        try
        {
            // Harden the newly inherited file ACL while the exact new object is
            // held open, then validate that same object rather than reopening by
            // path after a check/use window.
            string revalidatedPath = UserDataDirectorySecurity.EnsureSecureDirectory(directoryPath);
            if (!string.Equals(revalidatedPath, directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The diagnostic directory resolved outside its secured location.");
            }

            ValidateDirectoryLeases();
            _ = ValidateRegularFileHandle(handle, fullPath);
            stream = new FileStream(handle, FileAccess.Write, bufferSize: 4096, isAsync: true);
            return true;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public FileStream OpenReadStream(string path)
    {
        ValidateDirectoryLeases();
        string fullPath = ValidateChildPath(path);
        SafeFileHandle handle = OpenNative(
            fullPath,
            GenericRead | FileReadAttributes | DeleteAccess,
            FileShareRead,
            OpenExisting,
            FileFlagOverlapped | FileFlagOpenReparsePoint | FileFlagSequentialScan);
        try
        {
            _ = ValidateRegularFileHandle(handle, fullPath);
            return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool TryGetTrustedFileLength(string path, out long length)
    {
        ValidateDirectoryLeases();
        string fullPath = ValidateChildPath(path);
        SafeFileHandle handle;
        try
        {
            handle = OpenNative(
                fullPath,
                FileReadAttributes | DeleteAccess,
                FileShareRead | FileShareWrite,
                OpenExisting,
                FileFlagOpenReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            length = 0;
            return false;
        }

        using (handle)
        {
            try
            {
                NativeFileInformation information = ValidateRegularFileHandle(handle, fullPath);
                length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                length = 0;
                return false;
            }
        }
    }

    public void ValidateActiveFile(FileStream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateDirectoryLeases();
        string fullPath = ValidateChildPath(path);
        _ = ValidateRegularFileHandle(stream.SafeFileHandle, fullPath);
    }

    public bool TryDeleteTrustedFile(string path)
    {
        ValidateDirectoryLeases();
        string fullPath = ValidateChildPath(path);
        SafeFileHandle handle;
        try
        {
            handle = OpenNative(
                fullPath,
                DeleteAccess | FileReadAttributes,
                FileShareRead | FileShareWrite,
                OpenExisting,
                FileFlagOpenReparsePoint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using (handle)
        {
            try
            {
                _ = ValidateRegularFileHandle(handle, fullPath);
                MarkHandleForDeletion(handle, fullPath);
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public void MarkOpenFileForDeletion(FileStream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ValidateDirectoryLeases();
        string fullPath = ValidateChildPath(path);
        _ = ValidateRegularFileHandle(stream.SafeFileHandle, fullPath, requireSingleLink: false);
        MarkHandleForDeletion(stream.SafeFileHandle, fullPath);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        DisposeHandles(directoryLeases);
    }

    private static IEnumerable<string> EnumerateDirectoryComponents(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ??
            throw new IOException("The diagnostic directory does not have a Windows path root.");
        yield return root;

        string relative = Path.GetRelativePath(root, fullPath);
        if (string.Equals(relative, ".", StringComparison.Ordinal))
        {
            yield break;
        }

        string current = root;
        foreach (string component in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            yield return current;
        }
    }

    private string ValidateChildPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string? parent = Path.GetDirectoryName(fullPath);
        if (!string.Equals(parent, directoryPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException(
                "The diagnostic log path is outside its secured directory.");
        }

        return fullPath;
    }

    private static SafeFileHandle OpenNative(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint creationDisposition,
        uint flagsAndAttributes)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creationDisposition,
            flagsAndAttributes,
            IntPtr.Zero);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw CreateNativeException("open diagnostic filesystem entry", path, error);
    }

    private static NativeFileInformation ValidateRegularFileHandle(
        SafeFileHandle handle,
        string path,
        bool requireSingleLink = true)
    {
        NativeFileInformation information = GetInformation(handle, path);
        if ((information.FileAttributes &
             (FileAttributeDirectory | FileAttributeReparsePoint)) != 0)
        {
            throw new IOException("The diagnostic log is not a trusted regular file.");
        }

        // A pre-existing hard link could alias a file outside the secured log
        // directory. Reject it before any read, write, or handle-based delete.
        if (requireSingleLink && information.NumberOfLinks != 1)
        {
            throw new IOException("A multiply linked diagnostic log is not trusted.");
        }

        return information;
    }

    private static void MarkHandleForDeletion(SafeFileHandle handle, string path)
    {
        var disposition = new FileDispositionInformation { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfoClass,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw CreateNativeException(
                "delete diagnostic log",
                path,
                Marshal.GetLastWin32Error());
        }
    }

    private static NativeFileInformation GetInformation(SafeFileHandle handle, string path)
    {
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new IOException("A diagnostic filesystem handle is not available.");
        }

        if (GetFileType(handle) != FileTypeDisk)
        {
            throw new IOException("The diagnostic filesystem entry is not disk-backed.");
        }

        if (!GetFileInformationByHandle(handle, out NativeFileInformation information))
        {
            throw CreateNativeException(
                "inspect diagnostic filesystem entry",
                path,
                Marshal.GetLastWin32Error());
        }

        return information;
    }

    private static Exception CreateNativeException(string operation, string path, int error)
    {
        var native = new Win32Exception(error);
        string message = $"Unable to {operation} '{path}': {native.Message}";
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException(message, native)
            : new IOException(message, native);
    }

    private static void DisposeHandles(IEnumerable<SafeFileHandle> handles)
    {
        foreach (SafeFileHandle handle in handles.Reverse())
        {
            handle.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        internal byte DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out NativeFileInformation fileInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}
