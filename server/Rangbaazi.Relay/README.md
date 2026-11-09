# Rangbaazi.Relay

A small WebSocket relay for the web and Android builds of RANGBAAZI. Browsers can't use UDP, so in those builds all online traffic goes through this relay. It pairs two players by room code and forwards their binary game messages byte for byte. It contains no game logic and never references the sim. Rollback netcode stays peer-to-peer in logic; the relay only carries the bytes.

It runs on an AWS EC2 `t4g.nano` (Graviton, Amazon Linux 2023) behind CloudFront. CloudFront terminates `https`/`wss` and forwards `/relay/*` to the instance over plain HTTP on port 8080.

## Protocol

| Endpoint | Behaviour |
|---|---|
| `GET /relay/health` | `200`, body `ok` (text/plain). |
| `/relay/room/{code}` (WebSocket) | Join room `code`: 4 to 8 ASCII letters or digits, case-insensitive (`abcd` and `ABCD` are the same room). A plain HTTP request here gets `400`. |

Don't request a WebSocket subprotocol. The relay doesn't select one, and browsers fail the handshake if they asked for one and got none.

**Rooms.** A room holds at most 2 players: slot 0 (P1) and slot 1 (P2). A joining player takes the lowest free slot. A room is deleted when its last player leaves.

**Server to client, TEXT frames (JSON):**

| Message | When |
|---|---|
| `{"type":"welcome","slot":0,"room":"ABCD"}` | Right after a successful join. `room` is the upper-cased code. |
| `{"type":"start","seed":"123456789","slot":0}` | Each time the room goes from 1 to 2 players, sent to both. The seed is the same for both players and fresh for every pairing. It is an integer in [1, 2^53-1], sent as a decimal string. `slot` is the receiver's own slot. |
| `{"type":"peer_left"}` | The other player disconnected (close, error or abort). You keep your slot and stay connected; the next joiner triggers a new `start`. |
| `{"type":"full"}` then close `4001` | The room already has 2 players. |
| `{"type":"error","message":"..."}` then close `4000` | Bad room code. |
| `{"type":"busy"}` then close `4002` | The server is at `MAX_ROOMS` (only when creating a room) or `MAX_CONNECTIONS`. |

**Client to server:**

- **BINARY** frames are forwarded verbatim and in order to the other player, as BINARY frames. With no other player in the room they are dropped. Fragmented messages are reassembled first.
- **TEXT** frames are ignored, whatever their size. They still count as activity, so use one as a keepalive (see below).

Ordering: everything a player receives from its partner arrives between that pairing's `start` and the `peer_left` that ends it. Nothing the old partner sent ever arrives after `peer_left`, or after the next `start`.

**Limits and closes.** Each of these ends the connection, and a partner who is still connected gets `peer_left`:

| Limit | Close code |
|---|---|
| A BINARY message (after reassembly) over **256 bytes** | `1009` (MessageTooBig) |
| More than the rate limit: token bucket of **600** messages, refilling **300/s** | `1008` (PolicyViolation) |
| Nothing received for **60 s** | `4003` (idle timeout) |
| The player's outgoing queue (256 messages) overflows because it isn't reading | connection aborted |
| Server shutting down (also sent to anyone who connects while it shuts down) | `1001` (EndpointUnavailable) |

How the rate limit counts: each frame the relay reads takes one token. A message sent as one frame, which is what browsers and Godot do, is one token. A fragmented message takes one token per fragment, and a TEXT frame over 256 bytes takes one token per 257 bytes. That way empty or endless fragments can't get around the limit.

Keepalive (the client must do this): every 15 s the server sends a keep-alive control frame. On .NET 8 that is an unsolicited pong, which clients don't answer. It keeps CloudFront and NAT boxes from dropping a quiet connection, but it tells the relay nothing about the client, and control frames from the client (pings, pongs) don't count as activity either. So a client waiting for a partner must send a small TEXT frame, for example `{"type":"ping"}`, every 20 to 30 s, or it is closed with `4003` after 60 s. During a match the game traffic is enough.

## Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | Port to listen on, on all interfaces (`0.0.0.0`), HTTP/1.1 only. |
| `RELAY_ORIGIN_SECRET` | unset | When set, every request, health check included, must carry header `X-Origin-Secret` with exactly this value. Otherwise it gets `403` before any WebSocket upgrade. The comparison is constant-time. Set the same value as a custom origin header in CloudFront so only CloudFront can reach the relay. |
| `MAX_ROOMS` | `1000` | Most rooms at once. |
| `MAX_CONNECTIONS` | `2000` | Most player connections at once. |

Logs go to stdout, one line per event: rooms created and closed, joins, leaves, rejections and limit closes, with counts. They never contain IP addresses, room codes or message contents (player privacy; see the age-band safety rules in `docs/design/B10-chat-and-voice.md`). Framework logs are filtered to Warning, because at lower levels they would include paths and client addresses.

## Run locally

```powershell
dotnet run --project server/Rangbaazi.Relay
```

Then connect a WebSocket client to `ws://localhost:8080/relay/room/TEST`. Run a second one to pair, and both get `start`. Health check: `http://localhost:8080/relay/health`. Set `$env:PORT` to use another port.

Tests (they start the real app on a free loopback port): `dotnet test tests/Rangbaazi.Relay.Tests`.

## Publish for the EC2 instance (linux-arm64)

```powershell
dotnet publish server/Rangbaazi.Relay -c Release -r linux-arm64 --self-contained -p:PublishSingleFile=true -o $env:TEMP\relay-publish
```

This produces one self-contained executable, `Rangbaazi.Relay` (about 100 MB; the instance needs no .NET install and no ICU, since globalization is invariant). Copy it to the instance, `chmod +x` it and run it under systemd with `Environment=RELAY_ORIGIN_SECRET=...`. Keep publish output out of the repo.

Notes for the instance:
- The relay uses the workstation GC to keep memory low on the 512 MB nano.
- In the security group, open port 8080 only to the CloudFront origin-facing managed prefix list (`com.amazonaws.global.cloudfront.origin-facing`).
- In CloudFront, the `/relay/*` behaviour needs caching disabled, the viewer headers forwarded (for example the `AllViewerExceptHostHeader` origin request policy, which includes the `Sec-WebSocket-*` headers) and the `X-Origin-Secret` custom origin header.
