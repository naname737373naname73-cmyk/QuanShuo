# Voice activity fixture

`16k-speech.raw` is the 16 kHz, signed 16-bit little-endian mono test fixture from
[WebRtcVad.NET](https://github.com/devnull/WebRtcVad.NET/blob/a52dac432a64c2c609addf978c1f766bde6a5264/src/WebRtcVad.NET.Tests/TestData/16k-speech.raw),
pinned to commit `a52dac432a64c2c609addf978c1f766bde6a5264` (the NuGet 1.0.0 source).
The upstream license is preserved in `LICENSE` and in the application's
`THIRD_PARTY_NOTICES.md`. No user's recordings are included.

The recording tests loop this sample at normal and reduced volume to check that
speech resets the idle interval even when no transcription results arrive.
This fixture does not establish accuracy on every microphone or noise condition.
