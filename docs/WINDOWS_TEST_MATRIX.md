# Windows Hardware Test Matrix

> Updated: 2026-07-31
> M0 records evidence only. No lighting command has been sent by the Windows
> implementation.

## Support Matrix

| Profile | VID:PID | Interface evidence | Current status | Restore cycles |
| --- | --- | --- | --- | --- |
| Kick75 NuPhyIO USB | `19F5:1026` | Usage Page `0001`, Usage `0000`, Report ID 0, 64-byte reports | Enumerated / Unverified | 0/20 |
| Kick75 through U1 2.4G | `19F5:2620` | `MI_03`, Usage Page `0001`, Usage `0000`; official NuPhyIO association | Enumerated / Unverified | 0/20 |
| Kick75 High diagnostic identity | `19F5:1027` | Read-only identity evidence only | Diagnostic only; writes blocked | 0/20 |
| U1 boot/upgrader | `19F5:1020` | Firmware-update identity | Permanently excluded | Not applicable |
| QMK/VIA or Bluetooth | Varies | No validated raw-HID lighting path | Unsupported | Not applicable |

`Enumerated / Unverified` means the endpoint and descriptor evidence are
known, but no M1 handshake, read, write, acknowledgement, or restoration cycle
has run. U1 presence alone does not prove that the wireless keyboard is awake,
paired, or responsive.

## M1 Verification Record Template

Create one row per physical test run and never replace a failure with a retry:

| Date | Profile | Keyboard/receiver firmware | Interface fingerprint | Buffer behavior | Result | Baseline restored byte-for-byte |
| --- | --- | --- | --- | --- | --- | --- |
| — | USB | — | — | 64/65 bytes pending | Not run | — |
| — | U1 2.4G | — | — | 64/65 bytes pending | Not run | — |

For each profile, the guarded hardware test must:

1. Match VID/PID, usage, report sizes, and the permitted transport profile.
2. Complete the `0xEE` session exchange and validate direction, command,
   checksum, key, timeout, and response length.
3. Read all 17 lighting bytes and save the original eight side-light bytes.
4. Write static green for five seconds without touching the main-key region.
5. Restore the exact saved bytes in a `finally` path and verify the readback.
6. Confirm keys, main lighting, firmware mode, and pairing are unchanged.
7. Repeat 20 consecutive times, including reconnect and wake scenarios.

A profile becomes `Verified` only after all 20 cycles pass. USB success never
upgrades U1 status, or vice versa. Any uncertain interface, invalid response, or
failed restoration is a no-go and keeps writes disabled by default.
