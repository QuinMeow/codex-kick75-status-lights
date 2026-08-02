// SPDX-License-Identifier: MIT
using AgentKick75.Core.Configuration;
using AgentKick75.Core.Lighting;

namespace AgentKick75.App.Hosting;

public interface IHostSettingsPersistence
{
    ValueTask SaveAsync(
        LightingSettings settings,
        bool startAtLogin,
        CancellationToken cancellationToken = default);
}

public sealed class NullHostSettingsPersistence : IHostSettingsPersistence
{
    public ValueTask SaveAsync(
        LightingSettings settings,
        bool startAtLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

public sealed class CoreHostSettingsPersistence : IHostSettingsPersistence
{
    private readonly ConfigurationStore store;
    private readonly Action<bool>? setStartupEnabled;
    private readonly SemaphoreSlim gate = new(1, 1);
    private AgentKick75Configuration current;

    public CoreHostSettingsPersistence(
        ConfigurationStore store,
        AgentKick75Configuration current,
        Action<bool>? setStartupEnabled = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.current = current ?? throw new ArgumentNullException(nameof(current));
        this.setStartupEnabled = setStartupEnabled;
    }

    public async ValueTask SaveAsync(
        LightingSettings settings,
        bool startAtLogin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = new AgentKick75Configuration(
                settings,
                current.StaleSessionTtl,
                startAtLogin,
                current.SchemaVersion);
            bool startupChanged = current.StartAtLogin != startAtLogin;
            if (startupChanged)
            {
                setStartupEnabled?.Invoke(startAtLogin);
            }

            try
            {
                await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                current = updated;
            }
            catch
            {
                if (startupChanged)
                {
                    setStartupEnabled?.Invoke(current.StartAtLogin);
                }

                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
