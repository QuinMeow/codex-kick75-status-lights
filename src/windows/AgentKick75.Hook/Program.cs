// SPDX-License-Identifier: MIT
using AgentKick75.App.Hooks;

if (args.Length >= 2 && args[0] == "notify" && args[1] == "codex")
{
    return await CodexNotificationCommand.ExecuteAsync(
        args,
        new LoopbackHookRequestClient()).ConfigureAwait(false);
}

if (args.Length != 2 || args[0] != "hook" || args[1] != "codex")
{
    return 0;
}

return await HookCommand.ExecuteAsync(
    Console.In,
    Console.Out,
    new LoopbackHookRequestClient()).ConfigureAwait(false);
