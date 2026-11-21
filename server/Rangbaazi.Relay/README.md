# Rangbaazi.Relay

The online server for the web and Android builds of RANGBAAZI. Browsers can't use UDP, so in those builds all online traffic goes through it. It has two kinds of room:

- **Duel rooms** (`/relay/room/{code}`): it pairs two players by room code and forwards their binary game messages byte for byte, with no game logic. Rollback netcode stays peer-to-peer in logic; the relay only carries the bytes.
- **Arena rooms** (`/relay/arena/{code}`, see "Arena rooms" below): up to 15 players, and the server runs the match itself with the deterministic sim (`src/Rangbaazi.Sim`), sending everyone the state every frame.

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

## Arena rooms

`/relay/arena/{code}?host=1&minutes=10&name=Ravi` (WebSocket). With `host=1` the code must be free and the room is created; without it the room must exist. `minutes` (10 to 15, default 10) is the match length; `name` is cleaned to letters, digits, spaces, `-` and `_`, at most 12 characters. A room holds up to 15 members, people and CPUs; the first person is the host, and the role passes on when the host leaves. CPUs never keep a person out: someone joining a lobby of 15 takes the last-joined CPU's place (the host's CPU count is "up to", so the CPU comes back when a person leaves the lobby), and only 15 people make a room `full`. When its last person leaves, a room stops any match, goes back to its lobby and is kept for 3 minutes (`RelayOptions.EmptyRoomGrace`), so a host whose phone or tab dropped the connection can join the same code again and be its host, with its CPUs and settings; then it closes. Kept rooms never fill the server: when it is at `MAX_ROOMS`, a new host's room takes the place of the room that has been empty longest. Someone who joins during a match watches it and plays in the next one (in a full room, in a CPU's place).

**Client to server.** TEXT requests (JSON, at most 512 bytes): `{"type":"settings","minutes":12}`, `{"type":"bots","count":3}`, `{"type":"start"}` and `{"type":"end"}` (host only, in the lobby; `end` during a match), `{"type":"teams","count":0|2|3|4}` (host only, in the lobby: 0 is a free-for-all, otherwise that many teams of at most 4, never fewer than the room needs: 1 to 8 members 2 teams, 9 to 12 three, 13 to 15 four), `{"type":"team","team":1}` (anyone, in the lobby of a teams room: join that team; a full team takes you only in place of one of its CPUs), `{"type":"chat","phrase":0}` (a quick-chat phrase number, 0 to 15; the words live in the client, so nobody can send free text; at most one a second), `{"type":"ping"}` (keepalive). BINARY `INPUT`: 3 bytes, `1` then the buttons as a little-endian 16-bit number (`Buttons` bits). A button pressed and released between two frames still counts for the next frame.

**Server to client.** TEXT: `lobby` (room, phase, minutes, your id, the host's id, `teams`: 0 or the team count, and the members, each with its `team` and whether it `left` mid-match), `start` (stage, `size`: `normal`, or `big` for 9 or more fighters, who play the arena 1.5 times as wide, minutes, `time_limit_frames`, your fighter index or -1, `teams`, and the fighters' names, whether each is a CPU and its `team`), `chat` (who sent which phrase), `error` (`code` and `message`; `not_allowed`, `team_full` and `teams_needed` are notices and the connection stays). Refusals send `error` and close at once: `old_client` 4000 (the client's `v=` is below the protocol version), `full` 4001, `busy` 4002, `taken` 4004 (a host's code is in use), `no_room` 4005. The close's reason is the same code (`busy` says `server busy`), because Godot's WebSocketPeer can drop the error text and keep only the close; the game reads either (`RoomSession.refusal_code`). BINARY `STATE`, 60 a second during a match (`ArenaBinary.EncodeState`): a 12-byte header (frame, frames left on the clock, match over, the winner, the fighter count), then 34 bytes per fighter (position, facing, action, move, flags, action frame, Josh, KOs, falls, hit-stop, armor, Guts timer, hitstun, dodge cooldown, the weapon and its supply, the last hit taken, the hit count, the main weapon so far, the status bits, the poison or burn's weapon and the hold's weapon), then the props in use (a count and 17 bytes each). In a team match the winner byte is the winning team's MVP, or -1 for a draw. Hidden values (the respawn second wind) are never sent.

**Fields at a glance (arena protocol 5).**

| Message | Field | Meaning |
|---|---|---|
| `lobby` | `teams` | 0 in a free-for-all, else 2 to 4 |
| `lobby` | `players[].team` | 1 to `teams` (0 in a free-for-all) |
| `lobby` | `players[].left` | they left mid-match; the CPU plays their fighter to the end |
| `start` | `size` | `normal`, or `big` from 9 fighters (`StageScaling.SizeFor`); a client that doesn't know the value must refuse the match |
| `start` | `teams`, `fighters[].team` | the match's teams (`MatchRules.TeamOf`); missing means a free-for-all |
| `error` | `code` | `team_full`, `teams_needed`, `not_allowed`: notices, the connection stays; `old_client`, `full`, `busy`, `taken`, `no_room`: the connection closes |

A client works out the rest itself: the lobby's big-level chip from the member count (`size_for`), and the team size (4). The client remembers a host's last mode and match length (`GameSettings.room_teams`, `room_minutes`): the minutes go in the `minutes=` of the address, and the mode as a `teams` request once the lobby says it is the host.

**The match.** `MatchRules.Room`: the host's arena (1.5 times as wide, with 15 spawn points, for 9 or more fighters), a timed match where ring-outs respawn and score a KO for the last attacker (never for a teammate), rage and the hidden second wind. In a teams room teammates never hit each other and the team with the most KOs (then the fewest falls) wins; the STATE winner byte is that team's MVP, or -1 for a draw. One thread runs every room's frame at 60 Hz; CPUs play through `SparringBot`, and so does the fighter of anyone who leaves mid-match, to the end. After the result the room returns to the lobby on its own after 15 seconds.

## Configuration (environment variables)

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `8080` | Port to listen on, on all interfaces (`0.0.0.0`), HTTP/1.1 only. |
| `RELAY_ORIGIN_SECRET` | unset | When set, every request, health check included, must carry header `X-Origin-Secret` with exactly this value. Otherwise it gets `403` before any WebSocket upgrade. The comparison is constant-time. Set the same value as a custom origin header in CloudFront so only CloudFront can reach the relay. |
| `MAX_ROOMS` | `1000` | Most rooms at once. |
| `MAX_CONNECTIONS` | `2000` | Most player connections at once. |
| `ARENA_MATCH_SECONDS` | `0` | Local end-to-end tests only (`client/tests/room_teams_e2e.gd`): every arena match lasts this many seconds instead of the host's minutes. Leave unset in production. |

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
