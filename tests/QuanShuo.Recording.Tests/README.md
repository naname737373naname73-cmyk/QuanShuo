# Recording keyboard checks

Run on an interactive Windows desktop:

```powershell
dotnet run --project tests/QuanShuo.Recording.Tests -c Release
```

Uses the production recording card and speech window with synthetic records and
callbacks. Transparent windows briefly take keyboard focus. No microphone, ASR,
clipboard, user settings, or recording files are used.

Checks initial focus, Tab into editing, Space pause/resume, held-key repeats,
Space left over from the capture overlay, editable spaces, IME key routing,
finalization guards, and existing Enter/Escape finish actions. The IME check
verifies that Space is passed through while the editor owns focus; it is not a
real input-method candidate-selection test.
