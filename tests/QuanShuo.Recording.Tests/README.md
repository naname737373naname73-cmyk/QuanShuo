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

## Global Space integration checks

After building, run the actual Windows keyboard path on an interactive desktop:

```powershell
dotnet run --project tests/QuanShuo.Recording.Tests -c Release -- --global-space
```

This suite opens a separate child process containing a disposable text editor.
It sends real Space key events with `SendInput`, checking that the foreground
window belongs to the test before each keydown (including held-key repeats).
Do not run another focus-taking GUI test in parallel or interact with the
desktop while it runs. The child editor is closed at the end; input is never
deliberately sent to an existing user application. No microphone, ASR, network,
clipboard, or user configuration is used. Only temporary test data is created.

The 68 checks cover both skins and both recording surfaces: first Space without
clicking, pause/resume from another process, held-key repeat suppression,
ordinary spaces while editing, return from editing to another app, automatic
pause/resume, release on finish/close/hide, a held Space while opening, shortcut
conflicts and recovery, multiple surfaces, and release before asynchronous
coordinator shutdown completes. IME composition is simulated in `LiveTextBox`;
a real Windows input-method candidate window has not been tested by this suite.

Results are written to `global-space-after.json` in the working directory.
For a before-fix comparison, run an isolated build of the original source with
the baseline-capable test harness and `--global-space --baseline`. This runs
only the four opening/external-focus checks and writes `global-space-before.json`.
The flag does not revert source code or select an older installed application.
