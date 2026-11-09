class_name NetProtocol
## The binary messages the two players exchange through the relay (all little-endian, fixed size
## per kind; anything else is rejected by decode()). The relay forwards them verbatim and in order
## (it's a WebSocket, so nothing is lost or reordered), and adds JSON text messages of its own
## (welcome, start, peer_left, full, busy, error): see server/Rangbaazi.Relay/README.md.
##
## HELLO     version, fingerprint of the rules and sim: both must match or the match is refused
## INPUT     the sender's buttons for one frame, plus its clock for time sync
## CHECKSUM  the checksum of a frame whose inputs are final on the sender's side (desync check)
## BYE       leaving, and why
## PING/PONG round-trip time, for the HUD

const VERSION := 1

enum Kind { HELLO = 1, INPUT = 2, CHECKSUM = 3, BYE = 4, PING = 5, PONG = 6 }
enum Bye { LEFT = 1, REMATCH = 2, DESYNC = 3, REJECTED = 4, ERROR = 5, GAVE_UP = 6 }

const SIZES := { Kind.HELLO: 11, Kind.INPUT: 13, Kind.CHECKSUM: 13, Kind.BYE: 2, Kind.PING: 5, Kind.PONG: 5 }


static func hello(fingerprint: int) -> PackedByteArray:
	var b := _new(Kind.HELLO)
	b.encode_u16(1, VERSION)
	b.encode_s64(3, fingerprint)
	return b


## buttons for `frame`, sent when the sender was on `sender_frame`, with its measured advantage
## (its frame minus the newest sender_frame it has received from us).
static func input(frame: int, buttons: int, sender_frame: int, advantage: int) -> PackedByteArray:
	var b := _new(Kind.INPUT)
	b.encode_s32(1, frame)
	b.encode_u16(5, buttons & Buttons.KNOWN)
	b.encode_s32(7, sender_frame)
	b.encode_s16(11, clampi(advantage, -32768, 32767))
	return b


static func checksum(frame: int, value: int) -> PackedByteArray:
	var b := _new(Kind.CHECKSUM)
	b.encode_s32(1, frame)
	b.encode_s64(5, value)
	return b


static func bye(reason: int) -> PackedByteArray:
	var b := _new(Kind.BYE)
	b.encode_u8(1, reason)
	return b


static func ping(kind: int, stamp: int) -> PackedByteArray:
	var b := _new(kind)
	b.encode_u32(1, stamp & 0xFFFFFFFF)
	return b


## The message as a Dictionary with "kind" and its fields, or {} if it isn't a valid message.
static func decode(b: PackedByteArray) -> Dictionary:
	if b.is_empty():
		return {}
	var kind := b.decode_u8(0)
	if not SIZES.has(kind) or b.size() != SIZES[kind]:
		return {}
	match kind:
		Kind.HELLO:
			return { kind = kind, version = b.decode_u16(1), fingerprint = b.decode_s64(3) }
		Kind.INPUT:
			var frame := b.decode_s32(1)
			var sender_frame := b.decode_s32(7)
			if frame < 0 or sender_frame < 0:
				return {}
			return { kind = kind, frame = frame, buttons = b.decode_u16(5) & Buttons.KNOWN,
				sender_frame = sender_frame, advantage = b.decode_s16(11) }
		Kind.CHECKSUM:
			var frame := b.decode_s32(1)
			if frame < 0:
				return {}
			return { kind = kind, frame = frame, value = b.decode_s64(5) }
		Kind.BYE:
			return { kind = kind, reason = b.decode_u8(1) }
		Kind.PING, Kind.PONG:
			return { kind = kind, stamp = b.decode_u32(1) }
	return {}


static func _new(kind: int) -> PackedByteArray:
	var b := PackedByteArray()
	b.resize(SIZES[kind])
	b.encode_u8(0, kind)
	return b
