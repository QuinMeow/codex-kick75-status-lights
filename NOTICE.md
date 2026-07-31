# Third-Party Notices

This repository is a fork of
[Pixelmoss/codex-kick75-status-lights](https://github.com/Pixelmoss/codex-kick75-status-lights).
The Windows work starts from release `v0.2.0`, commit
`e32648ee86a8a729734060ac09bd7f8a1213876f`. The original Git history,
copyright notice, and MIT `LICENSE` are retained.

[alvis-HaoH/agent-kick75-status-lights](https://github.com/alvis-HaoH/agent-kick75-status-lights)
at commit `bf2dcb48f2c87c1794d524b9194d9aae96827cc4` is a secondary reference.
M0 copies no implementation code from that repository. Reviewed behavioral
findings and safety constraints are identified in the Windows planning and
baseline documents. Any later code port must record its source commit in the
corresponding commit or pull request.

The 64-byte request reports under
`tests/windows/AgentKick75.Protocol.Tests/Fixtures/` are deterministic
derivations of the pinned Pixelmoss source, not new M0 hardware captures or an
official NuPhy protocol specification. The baseline payload is an upstream
documented device-read example and is not a universal restore value.
