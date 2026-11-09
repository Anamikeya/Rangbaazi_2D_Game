# Netcode: online 1v1

GDD B9 layer 2 and the C3 milestone "Prototype Backdash 1v1 between two PCs". Code: `src/Rangbaazi.Net`; tests: `tests/Rangbaazi.Net.Tests`; game side: `game/scripts/Online/OnlinePlay.cs` and the online mode of `GrayboxMatch`. How to play is in the README ("Online 1v1").

GDD targets: input delay 1-2 frames, max rollback 7-8 frames, KPI median 2 frames delay / 4 frames rollback.

## Shape

```
GrayboxMatch._PhysicsProcess (60 Hz, main thread)
  └─ OnlinePlay.Tick: local buttons (P1's keys/pad, or a SparringBot with --bot)
       └─ OnlineMatch.Tick(buttons)
            ├─ control messages: Hello / Checksum / Bye           (ours)
            └─ Backdash session: BeginFrame → AddLocalInput → SynchronizeInputs
                 → Simulation.Advance(state, rules, CurrentInputs) → AdvanceFrame
                 (rollbacks run inside BeginFrame: LoadState, then re-simulate)
                      │
                 GuardedSocket (IPeerSocket) ── IDatagramTransport ── UdpTransport / InMemoryNetwork
```

`OnlineMatch` phases: **Handshaking** → **Synchronizing** → **Running** → **Ended** (with an `OnlineEndReason` and a message for the player). Once ended the network is closed; a rematch is a new `OnlineMatch` on the same port.

- **Handshake (ours, before Backdash starts).** Each side sends `Hello {protocol version, match id (random nonce), the peer's match id it has seen, its slot, fingerprint}` every 10 ticks. A match starts once both have seen each other's id. It is refused, with a message on both screens, if the versions differ, the fingerprints differ, or both picked the same side. The match seed is mixed from both ids, so every rematch gets a new seed.
- **Fingerprint** (`MatchFingerprint`): protocol version + a reflection hash of every field reachable from `MatchRules` (stage, tuning, frame data) + the checksum of a 600-frame scripted bot fight under those rules, which catches two builds whose sim code differs.
- **Desync detection (ours).** `ConfirmedInputLog` records each frame's inputs as Backdash confirms them. A frame's state is *final* once every input before it is confirmed and it has been simulated (`FinalFrame`). Every 30 ticks each side sends the checksum of its newest final frame; the other compares it with its own once that frame is final there too. Both sides compare only final states, so there are no false alarms, and a real desync is found at the first compared frame after it. A mismatch ends the match on both sides (`Bye(Desync)`).
- **Goodbye (ours).** `Bye {reason}` is sent three times on leave, rematch, desync, error or giving up, so the opponent learns at once instead of after Backdash's 5 s timeout.
- **State for rollback** (`GameStateCodec`): our 64-bit checksum, the `GameState` fields, then the `Fighter` array as raw memory (the bytes never leave the machine, so a new `Fighter` field is saved automatically). Loading checks the size and the checksum. Backdash's 32-bit checksum is ours folded (`GameStateCodec.Fold`), replacing its Fletcher-32.
- **Timeouts:** Backdash shows "connection interrupted" after 0.75 s of silence and ends the match after 5 s. Ours: 20 s to synchronize once both said hello, and 15 s frozen at the prediction limit while the connection is still alive (a stopped or paused game, which Backdash never times out).
- **Time sync:** when Backdash says this side runs ahead, `OnlineMatch` skips that many ticks, one every other tick so it never looks like a freeze.

Settings (`OnlineMatchOptions`): input delay 2 frames (`--delay`), prediction 8 frames (so rollbacks are at most 7-8 frames deep).

## Backdash 0.7.8

- **Pinned to `0.7.8-preview`.** It is the last release that targets net8.0, which matches Godot 4.7.2's GodotSharp; 0.8.x is net10.0 only. MIT licence (notice in `THIRD-PARTY-NOTICES.txt`). No dependencies.
- **Upgrading later:** `INetcodeSession<T>`, `INetcodeSessionHandler` and `IPeerSocket` are unchanged in 0.8.1. Changes to handle then: `LocalPort` 0 means "any" in 0.8 (it throws in 0.7.8), `LogLevel` moves to namespace `Backdash`, `Build()` validates options more strictly. Re-check the wire-format table below.
- Facts the code relies on (from reading its source):
  - Every handler callback runs on the thread that calls the session (the Godot main thread). Only the log writer and the socket run on thread-pool threads. Terminal events are recorded in callbacks and acted on after the session call returns, because a session must not be disposed from its own callback.
  - `AddLocalInput` → `InputDropped` means the input *was* taken (re-adding it trips a live assert); `PredictionThreshold` means stalled, skip the tick.
  - Max rollback is `PredictionFrames - 1`. `RollbackFrames` is a moving average of frames behind, not a depth; `RollbackStats` counts re-simulated frames instead.
  - Its consistency check is **off**: it compares possibly-predicted frames (false alarms), and a mismatch closes the session.
  - Its default log writer closes `Console.Out` when disposed; we always pass our own.
  - `Dispose` sends nothing and doesn't wait for background work.
  - Both peers must add players in the same order (slot = order added); nothing on the wire checks it, our hello does.

### Defects we work around

| Defect (present in every Backdash version so far) | Mitigation here |
|---|---|
| An Input packet with a peer count above 4 writes past a 4-entry buffer before the sender is checked (memory corruption from one datagram) | `GuardedSocket` drops datagrams from anyone but the peer, and `BackdashPacketValidator` drops malformed ones |
| One unexpected exception or socket error ends its receive or send loop for good, silently | Transports never throw except on close; malformed packets never reach it; sends always report full length |
| Debug asserts are live in the release package and leave the session stuck | Any exception from a session call ends the match |
| A frozen peer is never timed out (keep-alives come from timers) | Our stall timeout |
| Race between its input-resend timer and the game thread on the send queue | Mostly closed by 8 prediction frames (133 ms) < 200 ms resend interval; residual risk, see below |

### 0.7.8 wire format (big-endian), as checked by `BackdashPacketValidator`

| Type | Body after the 6-byte header (u16 type, u16 sync, u16 seq) | Total |
|---|---|---|
| 1 SyncRequest | u16 random, i64 ping | 16 |
| 2 SyncReply | u32 random, i64 pong | 18 |
| 3 Input | i32 start, i32 ack, bool disconnect, u8 peer count (≤ 4), peer count × (bool, i32), u8 input size (2), u16 bit count (≤ 4096), ⌈bits/8⌉ bytes | 19 + 5·peers + ⌈bits/8⌉ |
| 4 QualityReport | i32 frame advantage, i64 ping | 18 |
| 5 QualityReply | i64 pong | 14 |
| 6 KeepAlive | none | 6 |
| 7 InputAck | i32 frame | 10 |
| 8 ConsistencyCheckRequest | i32 frame | 10 |
| 9 ConsistencyCheckReply | i32 frame, u32 checksum | 14 |
| 10 ConsistencyCheckFail | i32 frame, u32 local, u32 remote | 18 |

Every datagram on our socket starts with a channel byte: `0xB0` Backdash, `0xC0` our control messages.

## Tests

| What | Where |
|---|---|
| Codec round trip; every `GameState` field covered; folded checksum never 0 | `GameStateCodecTests` |
| Fingerprint: equal for equal rules; changed by every rule, tuning value, the stage, Guts | `MatchFingerprintTests` |
| Control messages; packet validator against good and malformed (peer count 255, truncated, unknown) packets | `WireFormatTests` |
| Strangers dropped; malformed packets never reach Backdash; a throwing transport doesn't stop the socket | `GuardedSocketTests` |
| Backdash SyncTest: roll back 1 or 7 frames every frame, bots and random inputs; state kept outside `GameState` is caught | `SyncTestTests` |
| Two matches over the in-memory network (latency 0-6 ticks, jitter, up to 15 % loss), bots on both sides: same seed, equal checksums, equal confirmed inputs, offline replay equal, rollbacks within the window | `OnlineMatchTests` |
| Injected desync caught on both sides; different rules and same side refused; late opponent; leave; rematch; lost connection; frozen opponent | `OnlineMatchTests` |
| Real UDP on 127.0.0.1; a port in use can't be shared | `UdpOnlineTests` |
| Command-line parsing | `OnlineLaunchOptionsTests` |
| In Godot: two headless processes with bots over UDP, 900 frames, same checksum, replay equal | `tools/godot.ps1 online-smoke` |

Backdash's own test suite never builds a session, so these are the only end-to-end tests of its rollback flow.

## Open decisions and next steps

- **Privacy:** direct P2P shows each player's IP address to the opponent, with no NAT traversal and no encryption. Fine for LAN and friends; before public play, decide whether under-age bands get relay-only play (Steam Datagram Relay as another `IDatagramTransport`) and record it beside `docs/design/B10-chat-and-voice.md`.
- **Before internet playtests:** vendor the Backdash 0.7.8 source with patches for the packet bounds check, per-packet exception handling and the resend race (or get them upstream), or move to a fixed release.
- **Internet without port forwarding:** a small rendezvous server for UDP hole punching, then a relay fallback.
- **In-game connect screen** instead of command-line options; spectators; desync reports saved to disk (the confirmed-input log and checksum history already have what a replay needs).
