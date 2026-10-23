# TB OBS Auto Setup V4 Ultimate

Windows x64 / .NET 8 WPF / self-contained single-file build.

## V4 modules

- Hardware analysis: CPU, GPU, RAM, thread count
- Encoder recommendation: NVENC / AMF / QSV / x264
- OBS auto launch
- OBS WebSocket 5.x connection
- Automatic OBS backup
- 1080p60 video configuration
- Advanced Output attempt
- Recording directory
- 5 professional TB scenes
- Game Capture
- Webcam source
- Desktop audio + microphone
- Text/background sources
- Microphone filter chain attempt:
  - Noise Suppression
  - Compressor
  - Expander
- Replay Buffer start attempt
- OBS health scan with GetStats
- 5-second performance comparison
- Upload speed test
- Rollback to latest backup
- Does not request or collect stream keys/account passwords

## Build

GitHub Actions is included. The workflow publishes a self-contained Windows x64 EXE.

## Important

Some OBS settings are hardware/build dependent. The app uses OBS WebSocket requests and logs failures instead of stopping the entire setup.

OBS Studio 28+ includes obs-websocket. The default WebSocket port is 4455.
