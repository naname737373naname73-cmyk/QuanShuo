# End-to-end installer test. Drives the real window with posted messages, so it never
# touches the global mouse. Run it after changing src/QuanShuo.Installer:
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/test-installer.ps1
#
# Why it exists: the installer once hung forever on "Browse" because its main thread was
# not STA. That cannot be seen at build time, only by clicking the real button.
#
# It writes the real uninstall entry and Start Menu shortcut, so it refuses to run while
# QuanShuo is running or installed, and restores the autostart value when done.
# This file stays ASCII: PowerShell 5.1 reads BOM-less scripts as GBK.
param(
  [string]$Setup = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\installer\QuanShuo-Setup.exe'),
  [string]$ShotDir = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Text; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class P {
  public delegate bool Cb(IntPtr h, IntPtr l);
  public struct R { public int L,T,Rr,B; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(Cb c, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, Cb c, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int ht, uint f);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
  public static string Text(IntPtr h){ var s=new StringBuilder(1024); GetWindowText(h,s,1024); return s.ToString(); }
  public static string Cls(IntPtr h){ var s=new StringBuilder(256); GetClassName(h,s,256); return s.ToString(); }
  public static List<IntPtr> Top(uint pid){ var o=new List<IntPtr>(); EnumWindows((h,l)=>{ uint p; GetWindowThreadProcessId(h,out p); if(p==pid&&IsWindowVisible(h)) o.Add(h); return true;},IntPtr.Zero); return o; }
  public static List<IntPtr> Kids(IntPtr w){ var o=new List<IntPtr>(); EnumChildWindows(w,(h,l)=>{o.Add(h);return true;},IntPtr.Zero); return o; }
  public static bool Alive(IntPtr h, uint ms){ IntPtr r; return SendMessageTimeout(h,0,IntPtr.Zero,IntPtr.Zero,2,ms,out r)!=IntPtr.Zero; }
  public static void Click(IntPtr h){ R r; GetClientRect(h,out r); var xy=new IntPtr(((r.B/2)<<16)|(r.Rr/2)); PostMessage(h,0x201,new IntPtr(1),xy); PostMessage(h,0x202,IntPtr.Zero,xy); }
}
'@
[void][P]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))

function Str([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }
$AppName   = Str 0x5708, 0x8BF4                  # the app name, also the shortcut name
$TxtBrowse = (Str 0x6D4F, 0x89C8, 0x2026)
$TxtInstall = Str 0x5B89, 0x88C5
$TxtDone   = Str 0x5B8C, 0x6210
$TxtLaunch = (Str 0x5B89, 0x88C5, 0x540E, 0x6253, 0x5F00) + $AppName
$TxtDesktop = Str 0x521B, 0x5EFA, 0x684C, 0x9762, 0x5FEB, 0x6377, 0x65B9, 0x5F0F

$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\QuanShuo'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$StartDir = Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs\QuanShuo'
$StartLnk = Join-Path $StartDir "$AppName.lnk"
$DesktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
$failures = New-Object System.Collections.ArrayList

function Check([bool]$ok, [string]$name) {
  if ($ok) { Write-Output "  ok   $name" } else { Write-Output "  FAIL $name"; [void]$failures.Add($name) }
}
function WaitFor([scriptblock]$condition, [double]$seconds) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.Elapsed.TotalSeconds -lt $seconds) {
    if (& $condition) { return $true }
    Start-Sleep -Milliseconds 100
  }
  return (& $condition)
}
function Child([IntPtr]$window, [string]$text) {
  # EnumChildWindows on a null handle walks every top-level window on the desktop
  if ($window -eq [IntPtr]::Zero) { throw 'no installer window' }
  foreach ($k in [P]::Kids($window)) { if ([P]::Text($k) -ceq $text) { return $k } }
  return [IntPtr]::Zero
}
function Shot([IntPtr]$window, [string]$name) {
  if (-not $ShotDir) { return }
  $r = New-Object P+R; [void][P]::GetWindowRect($window, [ref]$r)
  $bmp = New-Object System.Drawing.Bitmap ($r.Rr - $r.L), ($r.B - $r.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc()
  [void][P]::PrintWindow($window, $dc, 2)
  $g.ReleaseHdc($dc); $g.Dispose()
  $bmp.Save((Join-Path $ShotDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
function OpenSetup() {
  $proc = Start-Process $Setup -PassThru
  [void](WaitFor { $proc.HasExited -or [P]::Top([uint32]$proc.Id).Count -gt 0 } 20)
  if ($proc.HasExited -or [P]::Top([uint32]$proc.Id).Count -eq 0) { throw 'installer window did not appear' }
  $window = [P]::Top([uint32]$proc.Id)[0]
  # WinForms only accepts a click when the window under the point is the control itself.
  # A background script cannot take the foreground, so keep the window on top instead.
  [void][P]::SetWindowPos($window, [IntPtr]::new(-1), 0, 0, 0, 0, 0x13)
  Start-Sleep -Milliseconds 300
  return @{ Proc = $proc; Window = $window }
}
function SetOption($ui, [string]$text) { [P]::Click((Child $ui.Window $text)); Start-Sleep -Milliseconds 150 }
function SetPath($ui, [string]$path) {
  if ($ui.Window -eq [IntPtr]::Zero) { throw 'no installer window' }
  foreach ($k in [P]::Kids($ui.Window)) { if ([P]::Cls($k) -like '*EDIT*') { [void][P]::SendMessage($k, 0x000C, [IntPtr]::Zero, $path) } }
}
# Installs through the window. Returns the longest time the window stayed unresponsive.
function InstallThroughUi($ui) {
  [P]::Click((Child $ui.Window $TxtInstall))
  $worst = 0.0; $since = $null
  [void](WaitFor {
    if ($ui.Proc.HasExited) { return $true }
    if ([P]::Alive($ui.Window, 250)) { $script:since = $null } else {
      if (-not $script:since) { $script:since = [Diagnostics.Stopwatch]::StartNew() }
      $worst = [Math]::Max($worst, $script:since.Elapsed.TotalSeconds)
    }
    return ((Child $ui.Window $TxtDone) -ne [IntPtr]::Zero)
  } 90)
  return $worst
}
function FinishUi($ui) {
  $done = Child $ui.Window $TxtDone
  if ($done -ne [IntPtr]::Zero) { [P]::Click($done) }
  [void](WaitFor { $ui.Proc.HasExited } 5)
  if (-not $ui.Proc.HasExited) { $ui.Proc.Kill() }
}
function RunUninstall([string]$exe) {
  $proc = Start-Process $exe -ArgumentList '--uninstall' -PassThru
  $box = [IntPtr]::Zero
  [void](WaitFor {
    foreach ($w in [P]::Top([uint32]$proc.Id)) { if ([P]::Cls($w) -eq '#32770') { $script:box = $w; return $true } }
    return $proc.HasExited
  } 15)
  if ($script:box -ne [IntPtr]::Zero) {
    foreach ($k in [P]::Kids($script:box)) { if ([P]::Cls($k) -eq 'Button') { [void][P]::PostMessage($k, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) } }
  }
  [void](WaitFor { $proc.HasExited } 5)
  if (-not $proc.HasExited) { $proc.Kill() }
}

if (-not (Test-Path $Setup)) { throw "missing $Setup" }
if (Get-Process -Name QuanShuo -ErrorAction SilentlyContinue) { throw 'QuanShuo is running. Exit it first.' }
if (Test-Path $UninstallKey) { throw 'QuanShuo is already installed. Uninstall it first.' }
if (Test-Path $DesktopLnk) { throw 'A desktop shortcut already exists.' }
if ($ShotDir) { New-Item -ItemType Directory -Force $ShotDir | Out-Null }

$portable = Join-Path (Split-Path -Parent (Split-Path -Parent $Setup)) 'publish\portable\QuanShuo.exe'
$sandbox = Join-Path $env:TEMP ("quanshuo-setup-test-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$mixed = Join-Path $sandbox 'existing folder'
$clean = Join-Path $sandbox 'clean\QuanShuo'
New-Item -ItemType Directory -Force $mixed | Out-Null
Set-Content (Join-Path $mixed 'keep-me.txt') 'user file'
New-Item -ItemType Directory -Force (Join-Path $mixed 'keep-dir') | Out-Null
Set-Content (Join-Path $mixed 'keep-dir\inner.txt') 'user file'
$savedRun = (Get-ItemProperty $RunKey -Name QuanShuo -ErrorAction SilentlyContinue).QuanShuo

try {
  Write-Output '[1] window and browse dialog'
  $ui = OpenSetup
  Check (-not $ui.Proc.HasExited) 'installer stays open'
  $dpi = [P]::GetDpiForWindow($ui.Window)
  $client = New-Object P+R; [void][P]::GetClientRect($ui.Window, [ref]$client)
  Check ([Math]::Abs($client.Rr - 640 * $dpi / 96) -le 2 -and [Math]::Abs($client.B - 460 * $dpi / 96) -le 2) "window scales with dpi $dpi ($($client.Rr)x$($client.B))"
  Shot $ui.Window '1-open'
  [P]::Click((Child $ui.Window $TxtBrowse))
  $dialog = [IntPtr]::Zero
  [void](WaitFor { foreach ($w in [P]::Top([uint32]$ui.Proc.Id)) { if ([P]::Cls($w) -eq '#32770') { $script:dialog = $w; return $true } }; return $false } 8)
  Check ($script:dialog -ne [IntPtr]::Zero) 'browse opens the folder dialog'
  Start-Sleep -Seconds 2
  Check ([P]::Alive($ui.Window, 1500)) 'window responds behind the dialog'
  if ($script:dialog -ne [IntPtr]::Zero) { [void][P]::PostMessage($script:dialog, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }
  Check (WaitFor { [P]::Top([uint32]$ui.Proc.Id).Count -eq 1 } 5) 'dialog closes'
  Check ([P]::Alive($ui.Window, 1500)) 'window responds after the dialog'

  Write-Output '[2] install into a folder that already holds user files'
  SetPath $ui $mixed
  SetOption $ui $TxtLaunch
  Shot $ui.Window '2-before-install'
  $worst = InstallThroughUi $ui
  Check ($worst -lt 1) ("window stayed responsive (worst {0:N1}s)" -f $worst)
  Shot $ui.Window '3-done'
  FinishUi $ui
  Check ((Get-FileHash (Join-Path $mixed 'QuanShuo.exe')).Hash -eq (Get-FileHash $portable).Hash) 'installed exe matches the build'
  Check ((Get-FileHash (Join-Path $mixed 'QuanShuo-Setup.exe')).Hash -eq (Get-FileHash $Setup).Hash) 'uninstaller copied intact'
  Check (Test-Path (Join-Path $mixed 'README.md')) 'readme written'
  Check (-not (Test-Path (Join-Path $mixed 'QuanShuo.exe.tmp'))) 'no temp file left'
  Check ((New-Object -ComObject WScript.Shell).CreateShortcut($StartLnk).TargetPath -eq (Join-Path $mixed 'QuanShuo.exe')) 'start menu shortcut points at the exe'
  Check ((Get-ItemProperty $UninstallKey).InstallLocation -eq $mixed) 'uninstall entry registered'
  Check (-not (Test-Path $DesktopLnk)) 'no desktop shortcut unless asked'

  Write-Output '[3] upgrade in place, then open the app'
  $ui = OpenSetup
  SetPath $ui $mixed
  [void](InstallThroughUi $ui)
  Check (WaitFor { $ui.Proc.HasExited } 5) 'installer closes after launching'
  $app = $null
  Check (WaitFor { $script:app = Get-Process -Name QuanShuo -ErrorAction SilentlyContinue | Select-Object -First 1; $null -ne $script:app } 10) 'app started'
  if ($script:app) {
    Check (WaitFor { foreach ($w in [P]::Top([uint32]$script:app.Id)) { if ([P]::Text($w) -ceq $AppName) { return $true } }; return $false } 20) 'app window is visible, not tray only'
  }

  Write-Output '[4] app running: install and uninstall are refused'
  $ui = OpenSetup
  SetPath $ui $mixed
  [P]::Click((Child $ui.Window $TxtInstall))
  Start-Sleep -Seconds 2
  $install = Child $ui.Window $TxtInstall
  Check ($install -ne [IntPtr]::Zero -and [P]::IsWindowEnabled($install)) 'install refused and can be retried'
  Check ([P]::Alive($ui.Window, 1500)) 'window responds after the refusal'
  Shot $ui.Window '4-refused'
  $ui.Proc.Kill(); $ui.Proc.WaitForExit()
  RunUninstall (Join-Path $mixed 'QuanShuo-Setup.exe')
  Check (Test-Path (Join-Path $mixed 'QuanShuo.exe')) 'uninstall refused while running'

  Get-Process -Name QuanShuo -ErrorAction SilentlyContinue | Stop-Process -Force
  [void](WaitFor { -not (Get-Process -Name QuanShuo -ErrorAction SilentlyContinue) } 5)
  Check ((Get-ItemProperty $RunKey -Name QuanShuo -ErrorAction SilentlyContinue).QuanShuo -like "*$mixed*") 'app registered autostart'

  Write-Output '[5] uninstall'
  $before = (Get-Item $Setup).LastWriteTimeUtc
  RunUninstall $Setup
  Check ((Test-Path $Setup) -and (Get-Item $Setup).LastWriteTimeUtc -eq $before) 'uninstall from another folder deletes nothing'
  Check (Test-Path $UninstallKey) 'uninstall from another folder keeps the install'

  RunUninstall (Join-Path $mixed 'QuanShuo-Setup.exe')
  Check (WaitFor { -not (Test-Path (Join-Path $mixed 'QuanShuo-Setup.exe')) } 8) 'uninstaller removes itself'
  $left = (Get-ChildItem $mixed -Recurse -File | ForEach-Object { $_.Name } | Sort-Object) -join ','
  Check ($left -eq 'inner.txt,keep-me.txt') "user files survive ($left)"
  Check (-not (Test-Path $UninstallKey)) 'uninstall entry removed'
  Check (-not (Test-Path $StartDir)) 'start menu folder removed'
  Check ($null -eq (Get-ItemProperty $RunKey -Name QuanShuo -ErrorAction SilentlyContinue)) 'autostart removed'

  Write-Output '[6] clean folder with a desktop shortcut'
  $ui = OpenSetup
  SetPath $ui $clean
  SetOption $ui $TxtLaunch
  SetOption $ui $TxtDesktop
  [void](InstallThroughUi $ui)
  FinishUi $ui
  Check (Test-Path $DesktopLnk) 'desktop shortcut created'
  RunUninstall (Join-Path $clean 'QuanShuo-Setup.exe')
  Check (WaitFor { -not (Test-Path $clean) } 8) 'empty install folder removed'
  Check (-not (Test-Path $DesktopLnk)) 'desktop shortcut removed'
}
finally {
  Get-Process -Name 'QuanShuo', 'QuanShuo-Setup' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 500
  Remove-Item $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $StartDir -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $DesktopLnk -Force -ErrorAction SilentlyContinue
  if ($null -ne $savedRun) { Set-ItemProperty $RunKey -Name QuanShuo -Value $savedRun }
  else { Remove-ItemProperty $RunKey -Name QuanShuo -ErrorAction SilentlyContinue }
  Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) { Write-Output "FAILED: $($failures.Count)"; exit 1 }
Write-Output 'ALL PASSED'
