extends TestCase
## The binary messages between players (NetProtocol).


func test_messages_round_trip() -> void:
	var m := NetProtocol.decode(NetProtocol.hello(-1234567890123456789))
	check_eq(m.get("kind"), NetProtocol.Kind.HELLO, "hello kind")
	check_eq(m.get("version"), NetProtocol.VERSION, "hello version")
	check_eq(m.get("fingerprint"), -1234567890123456789, "hello fingerprint")

	m = NetProtocol.decode(NetProtocol.input(12345, Buttons.LEFT | Buttons.HEAVY, 12343, -3))
	check_eq(m.get("frame"), 12345, "input frame")
	check_eq(m.get("buttons"), Buttons.LEFT | Buttons.HEAVY, "input buttons")
	check_eq(m.get("sender_frame"), 12343, "input sender frame")
	check_eq(m.get("advantage"), -3, "input advantage")

	m = NetProtocol.decode(NetProtocol.checksum(600, -42))
	check_eq(m.get("frame"), 600, "checksum frame")
	check_eq(m.get("value"), -42, "checksum value")

	check_eq(NetProtocol.decode(NetProtocol.bye(NetProtocol.Bye.REMATCH)).get("reason"), NetProtocol.Bye.REMATCH, "bye")
	check_eq(NetProtocol.decode(NetProtocol.ping(NetProtocol.Kind.PONG, 99)).get("stamp"), 99, "pong")


func test_garbage_is_rejected() -> void:
	check(NetProtocol.decode(PackedByteArray()).is_empty(), "empty")
	check(NetProtocol.decode(PackedByteArray([99, 1, 2])).is_empty(), "unknown kind")
	var hello := NetProtocol.hello(1)
	check(NetProtocol.decode(hello.slice(0, hello.size() - 1)).is_empty(), "truncated")
	hello.append(0)
	check(NetProtocol.decode(hello).is_empty(), "too long")
	check(NetProtocol.decode(NetProtocol.input(-1, 0, 0, 0)).is_empty(), "negative frame")
	check(NetProtocol.decode(NetProtocol.checksum(-5, 0)).is_empty(), "negative checksum frame")


func test_unknown_buttons_are_masked() -> void:
	var b := NetProtocol.input(1, 0, 0, 0)
	b.encode_u16(5, 0xFFFF)
	check_eq(NetProtocol.decode(b).get("buttons"), Buttons.KNOWN, "only known buttons survive")


func test_relay_url() -> void:
	var url := OnlineSession.relay_url_for_room("ab12")
	check(url.ends_with("/relay/room/AB12"), "room code upper-cased at the end of the URL: " + url)
	check(url.begins_with("ws://") or url.begins_with("wss://"), "a WebSocket URL: " + url)
