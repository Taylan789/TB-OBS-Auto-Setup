# TB OBS Auto Setup V5 Ultimate

Professional Windows x64 WPF utility for OBS Studio configuration, diagnostics and recovery.

## V5 modules
- Hardware detection: CPU / GPU / RAM / thread count
- Encoder recommendation: NVENC / AMF / QSV / x264
- Kick / YouTube / Twitch / manual profile selection
- Upload test and bitrate planner with network safety margin
- Resolution/FPS planner: 1080p60, 1664x936, 720p60, 30 FPS
- OBS WebSocket 5.x connection on localhost:4455
- One-click OBS optimization with pre-change backup
- Advanced Output profile parameters attempted individually with safe fallback
- 5 professional scenes: game, just chatting, starting, BRB, ending
- Game Capture, webcam, desktop audio, microphone and text/background sources
- Microphone filter chain: noise suppression, compressor, expander
- Replay Buffer start/check
- Recording safety: MKV and local TB OBS Recordings folder
- OBS Health: FPS, render time, render drops, output drops, OBS CPU
- 5-second performance test
- Scene/source inventory
- Backup browser and rollback with pre-rollback safety backup
- No stream key or account password collection
- Self-contained single-file EXE through GitHub Actions

## Build
GitHub Actions runs `dotnet publish` with .NET 8, win-x64, self-contained and single-file options. The user's PC does not need .NET installed.

## Security
The WebSocket password is only used to authenticate to the local OBS WebSocket server. Stream keys and platform account credentials are not requested or scraped. OBS configuration backups stay local in Documents/TB_OBS_Backups.

## Important
Some OBS profile parameter names and source defaults vary by OBS version, encoder and hardware. V5 treats many optional operations as best-effort so one unsupported parameter does not stop the entire setup.
