# Offline regression checks for the 2026-11-03 review

Run from the repository root. These checks do not start QuanShuo, an installer,
microphone capture, or an ASR connection. C# tests use synthetic files under
`artifacts/core-tests`; the browser tests run headless with the real App and
StageView and a delayed MockBridge. They do not prove WebView2/native integration.

## Commands

```powershell
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
& $dotnet restore tests/QuanShuo.Core.Tests/QuanShuo.Core.Tests.csproj --configfile tests/nuget.offline.config -p:NuGetAudit=false
& $dotnet run --project tests/QuanShuo.Core.Tests --no-restore
node frontend/scripts/test-races.mjs
$env:QS_SKIN = 'crabapple'
node frontend/scripts/test-races.mjs
Remove-Item Env:QS_SKIN
```

Both suites accept individual case names as arguments. For C#, put the case
name after `--`. Existing tests remain available with `npm.cmd --prefix frontend
run test:drafts`, `test:manage`, and `test:skins`. `npm.cmd` avoids PowerShell's
restriction on loading `npm.ps1`.

## Independent fixes and evidence

| Fix | Production locations | Cases that failed on the baseline and pass with the fix |
| --- | --- | --- |
| Retry survives selection changes | StageView retry/renderSingle | retry-cannot-be-bypassed-by-selection; retry-save-must-not-replace-new-selection |
| Late query cannot replace newer search/edit | App loadRecords and updated handler | latest-search-wins; old-query-cannot-revert-saved-text |
| Batch delete waits for drafts | App deleteMarked | batch-delete-waits-for-draft; batch-delete-keeps-failed-draft |
| Late single-delete callback keeps the new selection | StageView delete | delete-result-keeps-new-selection |
| Missing file is not a successful editor save | MarkdownRecordFile return value; RecordStore return type; HistoryWindow record.update | missing-record-save-must-fail |
| Image line does not consume the next paragraph | MarkdownRecordFile Parse | markdown-image-adjacent-paragraph (LF, CRLF, image-only, text-only and roundtrip) |
| Concurrent writes own separate temporary files | AtomicFile text/bytes | atomic-text-concurrent; atomic-bytes-concurrent; atomic-cancel-preserves-original |
| Replacing the destination waits for same-path writers and brief readers (added in review: separate temp files alone still failed 6/40 runs of atomic-text-concurrent at `File.Move`) | AtomicFile PublishAsync | atomic-text-concurrent; atomic-waits-for-brief-reader |
| An open history window is not reloaded by tray/second-instance activation; tray "设置" sends `view.open` | HistoryWindow Open; App `view.open` | view-open-settings-keeps-draft (frontend half; the native half needs a real window) |

The history bridge checks the missing-file result and rejects the acknowledgement,
allowing StageView to keep its draft. The local ASR health audit also makes native
recording/retry and editor-save callers check it before claiming success or deleting WAVs.
The core assertion tests the actual result, not the WinForms message handler.

Atomic publication does not merge two editors' text or guarantee their logical
save order. Cross-window conflict detection is outside this patch.

## Mutation checks

```powershell
node tests/run-mutations.mjs
```

Run this alone, without concurrent builds/tests. It changes one production
guard at a time, runs the relevant test, and restores the exact source bytes in
`finally`. All 14 mutations were caught. The script requires the expected
assertion/I/O failure, and rejects compiler/import/timeout failures as evidence.
Logs are written to `artifacts/mutations`. The last case also tests the existing
`test:drafts` suite by removing the wait before copying.

The existing suites still have limits: `test:drafts` initializes `__writes` and
`__mockFailUpdate` but MockBridge does not consume them; its close test checks
the list after a mock close rather than destruction of a WebView2. `test:manage`
allows its ordering assertion to pass when the collected text array is empty.
No coverage percentage or simplification claim is made.

## Offline packaging and byte verification

On the review machine, the sandbox's default package cache differs from the
pre-populated cache. Use the existing cache as a read-only fallback and keep any
new cache under this checkout. The configuration has no remote package sources.

```powershell
$env:RestoreConfigFile = (Resolve-Path tests/nuget.offline.config).Path
$env:RestoreFallbackFolders = (Join-Path $env:USERPROFILE '.nuget\packages')
$env:NUGET_PACKAGES = Join-Path (Get-Location) 'artifacts\nuget'
$env:NuGetAudit = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
powershell -NoProfile -ExecutionPolicy Bypass -File tools/build.ps1
& $dotnet run --project tests/QuanShuo.Core.Tests --no-restore -- verify-delivery
```

The verifier reads .NET single-file manifests and PE resources without loading
or executing the program. It compares every bundled Frontend file to dist,
checks both application assemblies against the Release output, and compares
the installer payload and ZIP executable to the portable executable by SHA-256.
Negative checks were also run: changing dist/index.html produced
`Delivery mismatch: Frontend/index.html`; changing a non-manifest portable EXE
byte produced `Delivery mismatch: Installer payload`. Both artifacts were
restored byte-for-byte, and the verifier passed again.

The font was read through cached fontverter/harfbuzzjs: all 384 Han characters
found in uncommented frontend TypeScript (excluding MockBridge), digits, and
the time colon were covered. This is a glyph-coverage check, not a visual or
font-fallback assessment.

## Local ASR health audit

### Recording and idle pause

`dotnet run --project tests/QuanShuo.Recording.Tests` checks both recording
surfaces under both skins (64 keyboard/state checks), plus the 60-second silence
boundary, resets on speech and resume, fragmented PCM, stale coordinator
notifications, and WAV/keepalive behavior during automatic pause. The real VAD
is exercised with digital silence and an attributed upstream speech fixture at
normal and reduced volume. No microphone, real service, user recording or API
key is used. This does not replace testing different microphones and background
noise conditions. The delivery verifier also checks the bundled VAD dependency.

```powershell
& $dotnet run --project tests/QuanShuo.LocalAsr.Tests
npm.cmd --prefix frontend run test:local-asr
npm.cmd --prefix frontend run test:local-asr-races
```

The 34 local tests use a localhost WebSocket fixture, synthetic WAVs, isolated
records and native editor controls. They do not use a microphone, model, cloud
key, private recording or real desktop screenshot. They cover protocol errors,
cancellation, cumulative text, retries, failed persistence, queued/deferred final
text, recording failure, bounded draining, close/save races, capture reentry and
the application CLI. Some coordinator scenarios set internal fields by reflection
to reach recording states without opening a microphone; this is not hardware QA.

The two-skin UI suite and seven settings race cases run headlessly. The latter
uses delayed bridge replies to verify configuration snapshots, stale callbacks,
duplicate test prevention, save ordering and late settings reads.

For a newly published executable, set `QUANSHUO_TEST_EXE` to its absolute path
when running the local suite: the CLI case then executes that artifact. Run
`verify-delivery` after the full build to compare frontend and assembly bytes,
the installer/ZIP executable, and all three embedded/shipped prompt copies.

The dedicated two-pack cleaner audit is recorded in
`artifacts/audit-quality/quality-report.md`. Its six C# behavior mutations run in
isolated copies: four originally survived and all six are caught after stronger
protocol assertions. This is targeted evidence, not a project coverage score.
