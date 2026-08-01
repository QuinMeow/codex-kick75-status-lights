# Windows Hardware Test Matrix

> Updated: 2026-08-01
> Earlier USB attempts used decoded `currentMode=0`. They received protocol ACK/readback but
> produced no visible side-light change on `v4.0.18` or the user's rollback firmware. The official
> NuPhyIO path instead reads the active mode with `0xA0` and uses that handle for D5/D6. The corrected
> path has now passed the USB physical gate on the current device/rollback-firmware combination;
> the exact rollback version is unknown and `v4.0.18` has not been retested with the corrected path.

## Support Matrix

| Profile | VID:PID | Interface evidence | Current status | Restore cycles |
| --- | --- | --- | --- | --- |
| Kick75 NuPhyIO USB | `19F5:1026` | `MI_03`, Usage Page `0001`, Usage `0000`; 65-byte native input/output buffers carrying Report ID 0 + 64-byte protocol frame | `Verified` on this device/current rollback-firmware combination | 20/20 formal physical cycles + 20/20 protocol rerun |
| Kick75 through U1 2.4G | `19F5:2620` | Same descriptor shape, but official NuPhyIO exposes it as a generic U1 capability rather than a Kick75 keyboard API | Diagnostic only; writes blocked | Not applicable |
| Kick75 High diagnostic identity | `19F5:1027` | Read-only identity evidence only | Diagnostic only; writes blocked | Not applicable |
| U1 boot/upgrader | `19F5:1020` | Firmware-update identity | Permanently excluded | Not applicable |
| QMK/VIA or Bluetooth | Varies | No validated raw-HID lighting path | Unsupported | Not applicable |

USB `Verified` is deliberately scoped to the tested device and current rollback firmware. It does not verify
`v4.0.18`, other devices, or U1. U1 presence alone does not prove a remote Kick75 identity, and prior receiver
ACKs do not upgrade it from diagnostic-only. Mock coverage never upgrades a profile to physical support.

## M1 Verification Record

Create one row per physical test run and never replace a failure with a retry:

| Date | Profile | Keyboard/receiver firmware | Safe interface fingerprint | Buffer behavior | Handle sharing/open evidence | Wake/pairing state | Timing evidence | Result | Baseline restored byte-for-byte |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-07-31 | USB | Not read | `19F5:1026/0001:0000/in=65/out=65` | Native 65 / protocol 64 | Not captured; enumeration only | Not captured | Not captured | Read-only enumeration; write gate not run | Not applicable |
| 2026-07-31 | U1 2.4G | Not read | `19F5:2620/0001:0000/in=65/out=65` | Native 65 / protocol 64 | No protocol handle opened | Remote keyboard state not established | Not applicable | Read-only receiver enumeration; write gate not run | Not applicable |
| 2026-07-31 | USB | `4.0.18` | `19F5:1026/0001:0000/in=65/out=65` | Old single-step `9/8` ACK/readback | Not captured | User-visible keyboard was active | Not captured | No visible side-light change; not a pass | Final captured bytes matched, but physical restoration was not independently established |
| 2026-07-31 | U1 2.4G | `4.0.18` keyboard reported by user | `19F5:2620/0001:0000/in=65/out=65` | 20 old protocol cycles reported ACK/readback | Not captured | Pairing/wake state not independently recorded | Not captured | No visible side-light change; U1 identity/envelope unproven | Protocol cleanup only; profile now write-blocked |
| 2026-08-01 | USB | User rollback version (exact value not captured) | `19F5:1026/0001:0000/in=65/out=65` | `9/8 → 10/1`, but decoded handle remained `0` | Exclusive target and fresh restore sessions; OS sharing mode not captured | User-visible keyboard was active | Hold duration captured; ACK latency not captured | No visible side-light change; not a pass | Captured bytes were restored; physical restoration was not observable because target never changed |
| 2026-08-01 | USB | User rollback version (exact value not captured) | `19F5:1026/0001:0000/in=65/out=65` | Corrected A0 flow, `currentMode=1`; 30-second extended diagnostic | Exclusive target and fresh restore sessions; OS sharing mode not captured | User-visible keyboard was active | 30-second hold; per-report latency not captured | Green was visible and original color returned; `AllBaselinesRestored=true`, but an additional target-stage check failed for an undetermined reason, so this run is not a formal cycle | Yes; user confirmed original color returned |
| 2026-08-01 | USB | User rollback version (exact value not captured) | `19F5:1026/0001:0000/in=65/out=65` | Corrected A0 flow, `currentMode=1`; 5-second preflight; baseline `02 28 01 00 00 44 E7 B3` | Exclusive target and fresh restore sessions; OS sharing mode not captured | User-visible keyboard was active | 5-second hold; per-report latency not captured | Every reported stage was `true`, `Error=null`; user confirmed green visible, original color restored, and main-key lights/keys normal | Yes; ownership released |
| 2026-08-01 | USB | User rollback version (exact value not captured) | `19F5:1026/0001:0000/in=65/out=65` | First formal batch: 20 × 5 seconds, corrected A0/D6 flow, `currentMode=1` | Each target connection closed before a fresh same-descriptor restore session; OS sharing mode not captured | User confirmed keys, pairing and M1/M2 normal | 20 × 5-second holds; per-report latency not captured | 20/20 protocol cycles passed; user confirmed all green/restores, main-key lights, keys, pairing, and M1/M2 were normal | 20/20; physical gate passed |
| 2026-08-01 | USB | User rollback version (exact value not captured) | `19F5:1026/0001:0000/in=65/out=65` | Second batch: 20 × 5 seconds, corrected A0/D6 flow, `currentMode=1` | Each target connection closed before a fresh same-descriptor restore session; OS sharing mode not captured | No separate post-run observation recorded | 20 × 5-second holds; per-report latency not captured | 20/20 protocol cycles passed; no separate post-run human observation was recorded | 20/20; final `isOwned=false` |

The unconfirmed `auto` diagnostic selected the USB profile while both paths
were present. This is selector evidence only, not a successful device session.

NuPhyIO coexistence currently has automated production-chain coverage for
`DeviceBusy → Reconnecting (first delay 2 seconds) → Ready`, including SSE/API convergence. A physical
NuPhyIO-held USB session has not been executed and remains pending explicit user-supervised approval; mock
coverage does not count as this matrix evidence.

For each profile, the guarded hardware test must:

1. Match VID/PID, usage, report sizes, and the permitted transport profile.
2. Complete the `0xEE` challenge-response, then read `0xA0 address=0,length=8` and accept only
   `currentMode` 0 or 1. Persist that mode with the baseline journal.
3. Read all 17 lighting bytes with that handle and save the original eight side-light bytes.
4. On USB only, use exact custom green `02 64 01 00 00 00 FF 00`; after one A0 mode check,
   write `D6 9/8` immediately followed by `D6 10/1`, with no intervening packet and with both D6
   packets carrying the same `currentMode XOR sessionKey` in byte 7.
5. After both ACKs, wait a 100 ms settle interval, perform immediate D5, preserve the full
   observation hold even on mismatch (or, unless cancelled, an invalid/timeout D5), then perform
   end-of-hold D5 when the connection remains trustworthy. Any mismatch still fails the protocol gate.
6. Close the target connection, open the same descriptor with a new session, re-read and require the same
   `currentMode`, restore the same adjacent `9/8 baseline` + `10/1 baseline[1]` pair, and require two
   matching D5 reads.
7. Confirm keys, main lighting, firmware mode, and pairing are unchanged.
8. Repeat 20 consecutive cycles; each cycle includes the new restoration session. Treat device wake,
   physical reconnect, and other M4 edge cases as separate matrix rows rather than silently folding them
   into the formal count.

Before each observed write, wake the keyboard and confirm the user-visible all-lights switch and side-light
brightness are on. The guarded path does not change the independent `gameOptimization`/global-light state.

A profile becomes `Verified` only after all 20 protocol cycles and the corresponding physical observation
pass. The first formal USB batch met that threshold. The second batch raises the aggregate to 40 successful
protocol cycles but is supporting protocol evidence, not a second independently observed physical gate.
USB success never upgrades U1 status, or vice versa. Any uncertain interface, invalid response, or failed
restoration is a no-go and keeps writes disabled by default.
