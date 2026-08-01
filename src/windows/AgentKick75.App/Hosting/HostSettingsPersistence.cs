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
    private readonly SemaphoreSlim gate = new(1, 1);
    private AgentKick75Configuration current;

    public CoreHostSettingsPersistence(
        ConfigurationStore store,
        AgentKick75Configuration current)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.current = current ?? throw new ArgumentNullException(nameof(current));
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
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            current = updated;
        }
        finally
        {
            gate.Release();
        }
    }
}
