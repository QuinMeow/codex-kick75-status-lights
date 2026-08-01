using System.Text;

namespace AgentKick75.Core.Storage;

public static class AtomicFile
{
    public static async Task WriteUtf8Async(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string fullPath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("The file path must have a parent directory.", nameof(path));
        }

        Directory.CreateDirectory(directory);
        IAtomicWriteSecurityPolicy? securityPolicy = OperatingSystem.IsWindows()
            ? UserDataDirectorySecurity.PrepareAtomicWrite(fullPath, directory)
            : null;
        string temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(content);
            const int bufferSize = 4096;
            const FileOptions fileOptions = FileOptions.Asynchronous | FileOptions.WriteThrough;
            await using (FileStream stream = securityPolicy?.CreateSecureTemporaryFile(
                    temporaryPath,
                    bufferSize,
                    fileOptions) ??
                new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize,
                    fileOptions))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            securityPolicy?.HardenAndValidateTemporaryFile(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();
            securityPolicy?.ValidateDestinationBeforeReplace(fullPath);
            // The temporary file lives in the destination directory, so an
            // overwrite move remains a same-volume atomic rename on Windows.
            // File.Replace is needlessly restrictive in sandboxed user
            // profiles and can fail even when both paths are writable.
            File.Move(temporaryPath, fullPath, overwrite: true);
            securityPolicy?.HardenAndValidateDestinationFile(fullPath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
