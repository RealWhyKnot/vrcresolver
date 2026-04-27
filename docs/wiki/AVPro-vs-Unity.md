# AVPro vs Unity Video Player

Background reading on VRChat's two video engines, what each supports, and how URLs and codecs interact. WKVRCProxy doesn't replace either — it sits in front of the URL-resolution step that feeds them. Knowing what each engine can decode is essential when reasoning about why a given URL plays or doesn't.

## At a glance

VRChat exposes two Udon-accessible video components:

- **`VRCAVProVideoPlayer`** — wraps RenderHeads' AVPro Video plugin. Handles live streams, adaptive HLS/DASH, VR/360 layouts, transparency, broader codec support (VP9, AV1, HEVC), audio piping to Unity AudioSources.
- **`VRCUnityVideoPlayer`** — wraps Unity's built-in `UnityEngine.Video.VideoPlayer`. Simpler. Works in Editor Play Mode with direct files. **Cannot** play live streams. Limited codec support compared to AVPro.

For VRChat playback in 2026, AVPro is what you want for almost any non-trivial use case. The Unity player is fine for testing direct `.mp4` / `.webm` files in the editor.

## What AVPro is reliably good at

- **Live streams** — YouTube Live, Twitch, RTMP, RTSP. Adaptive bitrate works well.
- **Adaptive streaming** — full HLS / MPEG-DASH support with automatic bitrate switching, multiple audio/video/subtitle tracks (subtitle tracks aren't surfaced to Udon though).
- **VR/360/180** — mono, stereo (L/R or T/B), equirectangular, cubemap. Single-draw-call stereo shaders.
- **Transparency** — alpha channel via Hap Alpha / Hap Q Alpha (Win/macOS), packed alpha (L/R or T/B in standard codec), or native HEVC+alpha on newer Apple devices.
- **High-res** — 8K+ on capable hardware (rare in practice in VRChat).
- **Audio piping** — pipe to Unity `AudioSource` for spatial audio via `VRC_SpatialAudioSource`.

Limitations:
- Doesn't play in Unity Editor Play Mode. Must Build & Test for any AVPro work.
- Can be picky about system codec extensions (HEVC Video Extensions, AV1 Video Extension, VP9).
- Some MKV audio quirks on certain Windows installs.

## What Unity Video Player covers

- Direct file playback (`.mp4`, `.webm`) including in Editor Play Mode.
- Render-to-texture / material override for world displays.
- Looping, seeking, speed, volume.
- Stereo 3D layouts.

What it doesn't:
- No HLS / DASH adaptive streaming.
- No live stream support; YouTube/Twitch URLs won't resolve through `yt-dlp` for it.
- Limited codec coverage — subset of OS-native decoders.
- No transparency in practice.

## VRChat's URL resolution pipeline

1. World pastes URL into a player component.
2. VRChat invokes its bundled `yt-dlp.exe` (this is what WKVRCProxy intercepts).
3. yt-dlp scrapes the page, extracts a direct media URL (often `.m3u8` HLS for live/VOD).
4. URL is handed to the player component.
5. Player loads through OS-native decoder (Windows Media Foundation, ExoPlayer/MediaPlayer on Android, etc.) plus AVPro's layer.

WKVRCProxy replaces step 2's `yt-dlp.exe` with its `Redirector.exe`, which runs the [[Resolution Cascade]] and returns either a relay-wrapped URL (for non-trusted hosts) or the pristine URL (for trusted hosts or hosts in the deny-list).

## Trusted-host allowlist

Hardcoded inside AVPro:

```
*.googlevideo.com, *.youtube.com, youtu.be, *.vimeo.com, *.twitch.tv,
*.ttvnw.net, *.twitchcdn.net, *.vrcdn.*, *.facebook.com, *.fbcdn.net,
*.hyperbeam.com, *.hyperbeam.dev, *.mixcloud.com, *.nicovideo.jp,
soundcloud.com, *.sndcdn.com, *.topaz.chat, vod-progressive.akamaized.net,
*.youku.com
```

Anything off this list is silently rejected unless wrapped through the relay (see [[Relay Server]]).

The full canonical list lives at [creators.vrchat.com/worlds/udon/video-players/www-whitelist](https://creators.vrchat.com/worlds/udon/video-players/www-whitelist) and may drift over time.

## Encoding recipes (for world creators)

These aren't WKVRCProxy concerns directly, but are helpful context when the answer to "why doesn't this play?" turns out to be the source file.

**Maximum compatibility (H.264 + AAC MP4)**:
```bash
ffmpeg -i input.mov -c:v libx264 -pix_fmt yuv420p -crf 18 -preset slower \
       -c:a aac -b:a 192k -movflags +faststart output.mp4
```

**HEVC for long-form / Quest** (requires Microsoft HEVC Video Extensions on Windows):
```bash
ffmpeg -i input.mov -c:v libx265 -tag:v hvc1 -pix_fmt yuv420p -crf 18 -preset slower \
       -c:a aac -b:a 192k -movflags +faststart output.mp4
```

**VP9 + Opus WebM**:
```bash
ffmpeg -i input.mov -c:v libvpx-vp9 -pix_fmt yuv420p -crf 18 -b:v 0 \
       -c:a libopus -movflags +faststart output.webm
```

**AV1 (best compression, slower encode, decode-heavy)**:
```bash
ffmpeg -i input.mov -c:v libsvtav1 -pix_fmt yuv420p -crf 23 -preset 6 \
       -c:a aac -movflags +faststart output.mp4
```

Always include `-movflags +faststart` for streaming / progressive playback. Quest deployments should stick to H.264/HEVC at 720p–1080p / 5–12 Mbps.

## Audio channels (PCVR reality)

WKVRCProxy is Windows-only, so this section is framed for PCVR. Quest/iOS users in the same world hit upstream directly and aren't affected by anything we do.

The headline rule, from VRChat's own creators docs: AVPro **reliably plays up to 6 channels (5.1)**. Anything beyond that is codec-specific and full of footguns.

| Source | PCVR outcome |
|---|---|
| AAC-LC ≤ 6ch (5.1) | Plays |
| AAC-LC 7ch | Video plays, audio silent (Media Foundation cap) |
| AAC-LC 8ch / 7.1 | Fails to load |
| EAC3 5.1 / 7.1 | Plays — the only documented 7.1 path |
| AC3 5.1 | Plays (most Win10/11 systems have the extension) |
| Opus 5.1 | Plays on Win10 1607+ |
| FLAC 5.1 | Plays on Win10+ or with LAV Filters |
| Vorbis surround | Treat as unreliable |

Misconceptions to flag in user-facing answers:

- **"AVPro speaker component supports 8 channels"** — the component implies it but VRChat creators docs explicitly say only 6 are reliably playable. The "8" is an internal buffer, not a render path.
- **"It will downmix to stereo automatically"** — often it won't. Without an explicit `VRCAVProVideoSpeaker` mode mapping in the world, center / rear / LFE channels go silent on stereo output devices. Voice-heavy content with the center track unmapped goes silent.
- **"7.1 works on PCVR"** — only with **EAC3**. AAC 7.1 is silent. This is a world-author concern, not something the relay can fix.

WKVRCProxy does not currently filter or transform audio renditions. If a 7+ channel source produces no sound, the source itself is the problem (or the user is missing a Windows codec extension). Tracking ideas for any future relay-side handling live in [issue #11](https://github.com/RealWhyKnot/WKVRCProxy/issues/11).

## Common error: "Loading failed. File not found, codec not supported, video resolution too high or insufficient system resources."

Catch-all from AVPro. Real meanings:
- **Off the trusted-host list** → relay wrap should fix it (the WKVRCProxy default does this).
- **Missing Windows codec extension** → install HEVC / AV1 / VP9 from Microsoft Store.
- **Resolution / bitrate too high** for the user's hardware → re-encode lower.
- **The URL yt-dlp returned isn't actually playable** → that's exactly what WKVRCProxy's playback-feedback demote loop catches; the next request re-cascades.

## More

- [VRChat Wiki video players page](https://wiki.vrchat.com/wiki/Video_players) — community-maintained compatibility table
- [creators.vrchat.com video-players docs](https://creators.vrchat.com/worlds/udon/video-players/) — official
- [RenderHeads AVPro Video docs](https://www.renderheads.com/products/avpro-video/) — feature reference
