// SPDX-License-Identifier: MIT
using System.Security.AccessControl;
using System.Security.Principal;
using AgentKick75.Core.Configuration;
using AgentKick75.Core.Storage;

namespace AgentKick75.Integration.Tests;

public sealed class UserDataDirectorySecurityTests
{
    [Fact]
    public void EnsureSecureDirectory_PermissiveParentAndChild_ProtectsChildAcl()
    {
        using var temporary = new TemporaryDirectory();
        string permissiveParent = Path.Combine(temporary.Path, "permissive-parent");
        string dataDirectory = Path.Combine(permissiveParent, "AgentKick75");
        string nestedDirectory = Path.Combine(dataDirectory, "existing-child");
        string existingFile = Path.Combine(nestedDirectory, "existing.json");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(existingFile, "{}");
        SetPermissiveDirectoryAcl(permissiveParent);
        SetPermissiveDirectoryAcl(dataDirectory);
        SetPermissiveDirectoryAcl(nestedDirectory);
        SetPermissiveFileAcl(existingFile);

        string secured = UserDataDirectorySecurity.EnsureSecureDirectory(dataDirectory);

        Assert.Equal(Path.GetFullPath(dataDirectory), secured);
        AssertSecureAcl(new DirectoryInfo(dataDirectory), isDirectory: true);
        AssertSecureAcl(new DirectoryInfo(nestedDirectory), isDirectory: true);
        AssertSecureAcl(new FileInfo(existingFile), isDirectory: false);
    }

    [Fact]
    public async Task ConfigurationSave_AtomicReplacement_RemovesExpandedFileAcl()
    {
        using var temporary = new TemporaryDirectory();
        string dataDirectory = Path.Combine(temporary.Path, "AgentKick75");
        UserDataDirectorySecurity.EnsureSecureDirectory(dataDirectory);
        string configurationPath = Path.Combine(dataDirectory, "config.json");
        var store = new ConfigurationStore(configurationPath);
        await store.SaveAsync(AgentKick75Configuration.Default);
        SetPermissiveFileAcl(configurationPath);

        await store.SaveAsync(AgentKick75Configuration.Default);

        AssertSecureAcl(new FileInfo(configurationPath), isDirectory: false);
        Assert.Empty(Directory.EnumerateFiles(dataDirectory, ".*.tmp"));
    }

    [DirectorySymbolicLinkFact]
    public void EnsureSecureDirectory_ReparsePointRoot_IsRejected()
    {
        using var temporary = new TemporaryDirectory();
        string target = Path.Combine(temporary.Path, "real-data");
        string link = Path.Combine(temporary.Path, "linked-data");
        Directory.CreateDirectory(target);
        _ = Directory.CreateSymbolicLink(link, target);

        IOException error = Assert.Throws<IOException>(
            () => UserDataDirectorySecurity.EnsureSecureDirectory(link));
        Assert.Contains("reparse point", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetPermissiveDirectoryAcl(string path)
    {
        SecurityIdentifier currentUser = GetCurrentUser();
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            world,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void SetPermissiveFileAcl(string path)
    {
        SecurityIdentifier currentUser = GetCurrentUser();
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            world,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void AssertSecureAcl(FileSystemInfo entry, bool isDirectory)
    {
        FileSystemSecurity security = entry switch
        {
            DirectoryInfo directory => directory.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            FileInfo file => file.GetAccessControl(
                AccessControlSections.Owner | AccessControlSections.Access),
            _ => throw new ArgumentOutOfRangeException(nameof(entry)),
        };
        SecurityIdentifier currentUser = GetCurrentUser();
        var localSystem = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var expected = new HashSet<SecurityIdentifier>(
            new[] { currentUser, localSystem, administrators });

        Assert.True(security.AreAccessRulesProtected);
        Assert.True(security.AreAccessRulesCanonical);
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        Assert.Equal(currentUser, Assert.IsType<SecurityIdentifier>(owner));

        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier));
        var observed = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in rules.Cast<FileSystemAccessRule>())
        {
            SecurityIdentifier principal = (SecurityIdentifier)rule.IdentityReference;
            Assert.False(rule.IsInherited);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
            Assert.Contains(principal, expected);
            Assert.Equal(
                isDirectory
                    ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                    : InheritanceFlags.None,
                rule.InheritanceFlags);
            Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
            observed.Add(principal);
        }

        Assert.True(observed.SetEquals(expected));
    }

    private static SecurityIdentifier GetCurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ??
            throw new InvalidOperationException("The test identity does not have a Windows SID.");
    }

    private sealed class DirectorySymbolicLinkFactAttribute : FactAttribute
    {
        public DirectorySymbolicLinkFactAttribute()
        {
            Skip = GetUnavailableReason();
        }

        private static string? GetUnavailableReason()
        {
            using var temporary = new TemporaryDirectory();
            string target = Path.Combine(temporary.Path, "probe-target");
            string link = Path.Combine(temporary.Path, "probe-link");
            Directory.CreateDirectory(target);

            try
            {
                _ = Directory.CreateSymbolicLink(link, target);
                return null;
            }
            catch (Exception exception) when (exception is
                UnauthorizedAccessException or
                IOException or
                PlatformNotSupportedException or
                NotSupportedException)
            {
                return $"This Windows environment cannot create a test symlink: {exception.GetType().Name}.";
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"agent-kick75-acl-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
