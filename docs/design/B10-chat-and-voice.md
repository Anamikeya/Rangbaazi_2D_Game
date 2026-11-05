# B10 — Chat & Voice (GDD addendum)

*v0.1 · 2026-11-05 · Status: proposal. Extends Part B3 (modes), B7 (social), B9 (tech) and the Part C1 content ramp of the RANGBAAZI GDD.*

Online team modes (Gang War, co-op Survival, Royale squads) need players to talk. Chat and voice must also be safe for a 12+ global audience that includes children, and they must never affect netcode.

## Goals

1. **Coordinate without slowing the fight.** One-press callouts mid-match; voice for teams and parties.
2. **Chat is part of the scene.** A quick-chat wheel of filmi one-liners ("Dialogue-baazi") fits pillar 1, "Every fight is a scene".
3. **Safe by default.** Restrictive defaults by age band, one-tap block, report with context. Passes Apple, Google, Steam and console requirements.
4. **Zero netcode cost.** Chat and voice never travel over the rollback P2P session, so they add no input delay and expose no player IPs.

## Features

| Feature | What it does | Notes |
|---|---|---|
| **Quick-chat wheel "Dialogue-baazi"** | 8-slot wheel of pre-written lines: callouts ("Prop Box here!"), social ("GG!") and filmi one-liners ("*Hero ki entry hamesha late hoti hai!*"). | Only the line ID goes over the network; each client shows its own language, and Hinglish lines get an English subtitle. Safe for every age band. Lines can be unlocked as cosmetics and can trigger the hero's voice bark (reuses the B4 personality pack). |
| **Text chat** | Channels: Lobby, Party, Team, Match (all chat), Whisper (friends DM), System. | 200 characters, sanitised, filtered (client + server), rate-limited (burst 5, then 1 per 2 s). During ranked play only team chat is open; all chat reopens on the results screen. On controller and mobile, free text is lobby-only (on-screen keyboard) and quick-chat is used in-match. |
| **Voice chat** | Party, Team, Match (custom rooms only, opt-in), Proximity (Box Office Royale, Season 1+). | Push-to-talk is the default on PC and console; voice-activated is the default on mobile (with a mic toggle). Speaking indicator on name tags and player cards, per-player volume and mute, input/output device selection. Proximity voice fades with distance and between layers (rooftop, ground, underground). |
| **Block / mute / report** | Block = hide text + mute voice + stop invites everywhere. Mute = local only. Report attaches the last messages as context. | One list (`BlockList`) shared by chat and voice. |
| **Streamer mode** | Hides other players' names, masks chat, disables voice joins from strangers. | Clip and streaming culture is a core audience (B1). |
| **Accessibility** | Chat font size, subtitles for filmi lines, text-to-speech for incoming text and speech-to-text for incoming voice (stretch). | Extends the B8 accessibility list. |

## Safety defaults by age band

Implemented in `src/Rangbaazi.Social/Safety/SocialPolicy.cs`. Players can always choose *less* than their default; they can never go above the maximum.

| Age band | Text default | Text max | Voice default | Voice max |
|---|---|---|---|---|
| Unknown / under 13 | Quick-chat only | Quick-chat only | Off | Off |
| 13–17 | Friends only | Everyone | Party only | Party + Team |
| 18+ | Everyone | Everyone | Party + Team | Everyone |

These are design proposals, **not legal advice**. Check them with counsel for each launch region before Early Access, in particular:
- **US COPPA**, **UK Age Appropriate Design Code**, **EU GDPR Art. 8**: free text or voice for under-13s needs verifiable parental consent.
- **India DPDP Act 2023**: it defines a child as under 18 and requires verifiable parental consent to process a child's personal data. Confirm whether teen voice/text needs consent for Indian players.
- **Platform rules**: Apple App Review 1.2 and Google Play UGC policy (filtering, reporting, blocking, published contact). Xbox, PlayStation and Nintendo communication and parental-control settings must be respected, including across cross-play.
- **Voice recording for moderation**: disclose it in the privacy policy, obtain consent, and define retention.

## Controls

| Action | Keyboard | Gamepad | Touch |
|---|---|---|---|
| Quick-chat wheel | T | View/Back (hold) + stick | Chat-bubble button |
| Open text chat | Enter | — (lobby: on-screen keyboard) | Tap chat box (lobby only) |
| Push-to-talk | V (hold) | R3 (hold) | Mic toggle |

All bindings are remappable (B8). R3-hold mid-fight is awkward for some players, so playtest it.

## Technical design

```
Game UI ──► IChatService ──► LocalChatHub (offline / dev / tests)   ── today
                         └─► NakamaChatService (online)                ── Early Access
Game UI ──► VoiceController ──► IVoiceService ──► NullVoiceService (couch / offline)  ── today
                                              └─► EosVoiceService (online)           ── Early Access
Shared: SocialPolicy · BlockList · ChatFilter · ChatRateLimiter · QuickChatCatalog
```

- **Text: Nakama realtime chat.** Nakama is already the B9 backend candidate. It provides rooms, groups, direct messages, persisted history and server hooks for authoritative filtering and moderation. If services move to EOS, keep Nakama (or a hosted chat vendor) just for chat.
- **Voice: Epic Online Services (EOS) RTC voice.** Free; runs on PC, Android, iOS and consoles; offers lobby and trusted-server voice rooms with per-participant mute. EOS Connect lets players use it without an Epic account (Steam, console or device login). The EOS C# SDK works from Godot .NET.
  - Alternatives: Discord Social SDK (voice + text, cross-platform), Vivox.
  - Not recommended: self-hosted Opus over WebRTC. Echo cancellation, noise suppression, NAT traversal, console certification and moderation would all become our problem.
- **Moderation.** The server-side text filter is authoritative; the client filter (`ChatFilter`) only gives instant feedback. Commercial filters are weak on romanised Hinglish and regional languages, so build custom word lists with the B8 cultural consultants. Voice moderation vendors (ToxMod, GGWP, etc.) are a budget decision for 1.0.
- **Netcode isolation.** Chat and voice connect to provider servers, never through the Backdash P2P session.
- **Budgets.** Opus voice uses roughly 16–32 kbps per active speaker. On mobile, cap simultaneous decoded speakers (Royale proximity: nearest 8).

## Phasing (adds to the C1 content ramp)

| Milestone | Chat & voice scope |
|---|---|
| **Vertical slice** | Quick-chat wheel in online 1v1 (no free text, no voice). `LocalChatHub` for UI work. |
| **Next Fest demo** | + post-match text chat, block/report, server-side filter. |
| **Early Access** | + Lobby/Party/Team text, Party + Team voice (PC), speaking indicators, streamer mode. |
| **1.0** | + mobile voice (voice-activated), cross-play voice and text honouring platform settings, parental controls, moderation pipeline. |
| **Season 1** | + Royale squad voice and proximity voice, custom-room match voice. |

## Risks

| Risk | Mitigation |
|---|---|
| Toxicity drives churn and store complaints | Quick-chat first, safe defaults, all-chat closed during ranked play, one-tap block, fast report triage. |
| Child-safety non-compliance | Age gate at first launch, restrictive defaults, legal review per region before EA. |
| Hinglish and regional abuse evades filters | Custom lists with consultants; server-side moderation; player reports feed the list. |
| Vendor lock-in or cost | Everything sits behind `IChatService` and `IVoiceService`; EOS voice is free. |
| Voice hurting netcode | Separate transport; never on the rollback socket. |

## Open questions

1. Age gate: our own, or rely on platform account ages only?
2. Voiced quick-chat: record filmi lines per hero? (VO budget = lines × heroes × languages.)
3. Record voice for moderation? (Needs consent and storage.)
4. Proximity voice at Season 1 launch, or later in the season?
