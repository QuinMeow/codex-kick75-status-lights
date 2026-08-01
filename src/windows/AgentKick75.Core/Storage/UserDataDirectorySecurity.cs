// SPDX-License-Identifier: MIT
using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace AgentKick75.Core.Storage;

internal interface IAtomicWriteSecurityPolicy
{
    FileStream CreateSecureTemporaryFile(
        string temporaryPath,
        int bufferSize,
        FileOptions options);

    void HardenAndValidateTemporaryFile(string temporaryPath);

    void ValidateDestinationBeforeReplace(string destinationPath);

    void HardenAndValidateDestinationFile(string destinationPath);
}

/// <summary>
/// Creates and registers a Windows user-data root whose protected DACL grants
/// full control only to the current user, LocalSystem, and local Administrators.
/// LocalSystem and Administrators are retained for operating-system maintenance,
/// backup, and administrative recovery; no other principal is permitted.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UserDataDirectorySecurity
{
    private static readonly ConcurrentDictionary<string, UserDataAclPolicy> SecuredRoots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates or repairs a user-data directory, rejects every reparse point in
    /// its existing ancestry/tree, and registers it for atomic-write validation.
    /// </summary>
    public static string EnsureSecureDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows ACL hardening requires Windows.");
        }

        string fullPath = NormalizeDirectoryPath(directoryPath);
        RejectReparsePointsAlongPath(fullPath);
        UserDataAclPolicy policy = UserDataAclPolicy.Create(fullPath);
        _ = FileSystemAclExtensions.CreateDirectory(
            policy.CreateDirectorySecurity(),
            fullPath);
        RejectReparsePointsAlongPath(fullPath);

        policy.RejectReparsePointsInTree();
        policy.HardenTree();
        policy.ValidateTree();
        SecuredRoots[fullPath] = policy;
        return fullPath;
    }

    internal static IAtomicWriteSecurityPolicy? PrepareAtomicWrite(
        string fullPath,
        string directoryPath)
    {
        UserDataAclPolicy? policy = FindPolicy(fullPath);
        if (policy is null)
        {
            return null;
        }

        policy.ValidateWriteLocation(fullPath, directoryPath);
        return policy;
    }

    private static UserDataAclPolicy? FindPolicy(string fullPath)
    {
        UserDataAclPolicy? bestMatch = null;
        foreach (UserDataAclPolicy policy in SecuredRoots.Values)
        {
            if (policy.Contains(fullPath) &&
                (bestMatch is null || policy.RootPath.Length > bestMatch.RootPath.Length))
            {
                bestMatch = policy;
            }
        }

        return bestMatch;
    }

    private static string NormalizeDirectoryPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void RejectReparsePointsAlongPath(string path)
    {
        string? current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (TryGetAttributes(current, out FileAttributes attributes) &&
                (attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"User-data path '{current}' is a reparse point and is not trusted.");
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private sealed class UserDataAclPolicy : IAtomicWriteSecurityPolicy
    {
        private readonly SecurityIdentifier owner;
        private readonly IReadOnlyList<SecurityIdentifier> allowedPrincipals;

        private UserDataAclPolicy(
            string rootPath,
            SecurityIdentifier owner,
            IReadOnlyList<SecurityIdentifier> allowedPrincipals)
        {
            RootPath = rootPath;
            this.owner = owner;
            this.allowedPrincipals = allowedPrincipals;
        }

        public string RootPath { get; }

        public static UserDataAclPolicy Create(string rootPath)
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            SecurityIdentifier currentUser = identity.User ??
                throw new InvalidOperationException("The current Windows identity does not have a user SID.");
            var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier[] allowed = new[] { currentUser, localSystem, administrators }
                .Distinct()
                .ToArray();
            return new UserDataAclPolicy(rootPath, currentUser, allowed);
        }

        public bool Contains(string path)
        {
            string relative = Path.GetRelativePath(RootPath, Path.GetFullPath(path));
            return !Path.IsPathRooted(relative) &&
                !string.Equals(relative, "..", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }

        public void ValidateWriteLocation(string fullPath, string directoryPath)
        {
            if (!Contains(fullPath) || !Contains(directoryPath))
            {
                throw new UnauthorizedAccessException(
                    "The atomic-write destination is outside its registered user-data root.");
            }

            RejectReparsePointsAlongPath(directoryPath);
            HardenDirectory(directoryPath);
            ValidateDirectory(directoryPath);

            if (TryGetAttributes(fullPath, out FileAttributes attributes))
            {
                RejectReparsePoint(fullPath, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    throw new IOException("The atomic-write destination is a directory, not a file.");
                }

                HardenFile(fullPath);
                ValidateFile(fullPath);
            }
        }

        public FileStream CreateSecureTemporaryFile(
            string temporaryPath,
            int bufferSize,
            FileOptions options)
        {
            if (!Contains(temporaryPath))
            {
                throw new UnauthorizedAccessException(
                    "The atomic-write temporary file is outside its registered user-data root.");
            }

            RejectReparsePointsAlongPath(temporaryPath);
            return new FileInfo(temporaryPath).Create(
                FileMode.CreateNew,
                FileSystemRights.Write,
                FileShare.None,
                bufferSize,
                options,
                CreateFileSecurity());
        }

        public void HardenAndValidateTemporaryFile(string temporaryPath)
        {
            if (!Contains(temporaryPath))
            {
                throw new UnauthorizedAccessException(
                    "The atomic-write temporary file is outside its registered user-data root.");
            }

            RejectReparsePointsAlongPath(temporaryPath);
            HardenFile(temporaryPath);
            ValidateFile(temporaryPath);
        }

        public void ValidateDestinationBeforeReplace(string destinationPath)
        {
            RejectReparsePointsAlongPath(destinationPath);
            if (TryGetAttributes(destinationPath, out FileAttributes attributes))
            {
                RejectReparsePoint(destinationPath, attributes);
            }
        }

        public void HardenAndValidateDestinationFile(string destinationPath)
        {
            RejectReparsePointsAlongPath(destinationPath);
            HardenFile(destinationPath);
            ValidateFile(destinationPath);
        }

        public void RejectReparsePointsInTree()
        {
            RejectReparsePointsAlongPath(RootPath);
            RejectReparsePointsBelow(RootPath);
        }

        public void HardenTree()
        {
            HardenDirectory(RootPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(RootPath))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    HardenDirectoryTree(entry);
                }
                else
                {
                    HardenFile(entry);
                }
            }
        }

        public void ValidateTree()
        {
            ValidateDirectory(RootPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(RootPath))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ValidateDirectoryTree(entry);
                }
                else
                {
                    ValidateFile(entry);
                }
            }
        }

        private void RejectReparsePointsBelow(string directoryPath)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    RejectReparsePointsBelow(entry);
                }
            }
        }

        private void HardenDirectoryTree(string directoryPath)
        {
            HardenDirectory(directoryPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    HardenDirectoryTree(entry);
                }
                else
                {
                    HardenFile(entry);
                }
            }
        }

        private void ValidateDirectoryTree(string directoryPath)
        {
            ValidateDirectory(directoryPath);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                RejectReparsePoint(entry, attributes);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ValidateDirectoryTree(entry);
                }
                else
                {
                    ValidateFile(entry);
                }
            }
        }

        private void HardenDirectory(string directoryPath)
        {
            RejectReparsePointsAlongPath(directoryPath);
            new DirectoryInfo(directoryPath).SetAccessControl(CreateDirectorySecurity());
        }

        private void HardenFile(string filePath)
        {
            FileAttributes attributes = File.GetAttributes(filePath);
            RejectReparsePoint(filePath, attributes);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                throw new IOException($"Expected '{filePath}' to be a regular file.");
            }

            new FileInfo(filePath).SetAccessControl(CreateFileSecurity());
        }

        public DirectorySecurity CreateDirectorySecurity()
        {
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier principal in allowedPrincipals)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    principal,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            return security;
        }

        private FileSecurity CreateFileSecurity()
        {
            var security = new FileSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (SecurityIdentifier principal in allowedPrincipals)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    principal,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));
            }

            return security;
        }

        private void ValidateDirectory(string directoryPath)
        {
            DirectorySecurity security = new DirectoryInfo(directoryPath).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            ValidateAcl(security, isDirectory: true, directoryPath);
        }

        private void ValidateFile(string filePath)
        {
            FileSecurity security = new FileInfo(filePath).GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access);
            ValidateAcl(security, isDirectory: false, filePath);
        }

        private void ValidateAcl(FileSystemSecurity security, bool isDirectory, string path)
        {
            if (!security.AreAccessRulesProtected || !security.AreAccessRulesCanonical)
            {
                throw new UnauthorizedAccessException(
                    $"User-data ACL for '{path}' is inherited or non-canonical.");
            }

            IdentityReference? ownerReference = security.GetOwner(typeof(SecurityIdentifier));
            if (ownerReference is not SecurityIdentifier actualOwner || !actualOwner.Equals(owner))
            {
                throw new UnauthorizedAccessException(
                    $"User-data ACL for '{path}' is not owned by the current user.");
            }

            var expected = new HashSet<SecurityIdentifier>(allowedPrincipals);
            var observed = new HashSet<SecurityIdentifier>();
            AuthorizationRuleCollection rules = security.GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>())
            {
                var principal = (SecurityIdentifier)rule.IdentityReference;
                InheritanceFlags expectedInheritance = isDirectory
                    ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                    : InheritanceFlags.None;
                if (rule.IsInherited ||
                    rule.AccessControlType != AccessControlType.Allow ||
                    rule.FileSystemRights != FileSystemRights.FullControl ||
                    rule.InheritanceFlags != expectedInheritance ||
                    rule.PropagationFlags != PropagationFlags.None ||
                    !expected.Contains(principal))
                {
                    throw new UnauthorizedAccessException(
                        $"User-data ACL for '{path}' grants an unexpected access rule.");
                }

                observed.Add(principal);
            }

            if (!observed.SetEquals(expected))
            {
                throw new UnauthorizedAccessException(
                    $"User-data ACL for '{path}' is missing a required trusted principal.");
            }
        }

        private static void RejectReparsePoint(string path, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException($"User-data entry '{path}' is a reparse point and is not trusted.");
            }
        }
    }
}
