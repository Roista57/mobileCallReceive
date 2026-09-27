param([switch]$WaitForAndroid)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $repo 'artifacts/win-x64/CallReceiver.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish the win-x64 application first.' }
$check = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 18080)
try { $check.Start() } finally { $check.Stop() }
$dataDir = Join-Path $repo ('artifacts/smoke/' + [guid]::NewGuid().ToString())
[System.IO.Directory]::CreateDirectory($dataDir) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
$settings = @{
    listenAddress='127.0.0.1'; listenPort=18080; timeFormat='yyyy-MM-dd HH:mm:ss'
    monitor=[System.Windows.Forms.Screen]::PrimaryScreen.DeviceName
    popupX=30; popupY=30; popupWidth=350; popupHeight=140
    displayDurationSeconds=3; topMost=$false; playSound=$false
    startWithWindows=$false; minimizeToTray=$false
}
[System.IO.File]::WriteAllText((Join-Path $dataDir 'settings.json'),
    ($settings | ConvertTo-Json), [System.Text.UTF8Encoding]::new($false))
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class McsWindows {
    public delegate bool Callback(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(Callback cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    public static long[] Popups(int processId) {
        var found = new List<long>();
        EnumWindows((h,p) => {
            uint process; GetWindowThreadProcessId(h, out process);
            var title = new StringBuilder(256); GetWindowText(h,title,256);
            if (process == processId && IsWindowVisible(h) && title.ToString().EndsWith(" 01012345678")) found.Add(h.ToInt64());
            return true;
        }, IntPtr.Zero);
        return found.ToArray();
    }
}
'@
$arguments = '--settings --data-dir "' + $dataDir + '"'
$process = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
$handles = [System.Collections.Generic.HashSet[long]]::new()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($process.HasExited) { throw 'The published app exited during startup.' }
        try { $health = Invoke-RestMethod 'http://127.0.0.1:18080/api/health' -TimeoutSec 2 } catch { $health = $null }
        if ($health.success) { break }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    if (-not $health.success) { throw 'Health timeout.' }

    $second = Start-Process -FilePath $exe -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $second.WaitForExit(10000)) { throw 'Single-instance activation failed.' }
    if ($process.HasExited) { throw 'Primary instance unexpectedly exited.' }
    Write-Output 'WPF_PUBLISHED_READY port=18080; single-instance activation passed'
    if (-not $WaitForAndroid) {
        $event = @{ schemaVersion=1; eventId=[guid]::NewGuid().ToString()
            phoneNumber='01012345678'; receivedAt=[DateTimeOffset]::Now.ToString('O')
            sentAt=[DateTimeOffset]::Now.ToString('O'); isTest=$true }
        $body = $event | ConvertTo-Json
        $ack = Invoke-RestMethod 'http://127.0.0.1:18080/api/call' -Method Post -ContentType 'application/json' -Body $body
        $duplicate = Invoke-RestMethod 'http://127.0.0.1:18080/api/call' -Method Post -ContentType 'application/json' -Body $body
        if ($ack.eventId -ne $event.eventId -or $duplicate.eventId -ne $event.eventId) { throw 'Acknowledgement mismatch.' }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(150)
    $firstSeen = $null
    do {
        foreach ($handle in [McsWindows]::Popups($process.Id)) {
            [void]$handles.Add($handle)
            if ($null -eq $firstSeen) { $firstSeen = [DateTime]::UtcNow; Write-Output 'WPF_POPUP_OBSERVED' }
        }
        if ($null -ne $firstSeen -and ([DateTime]::UtcNow - $firstSeen).TotalSeconds -gt 8) { break }
        if ($process.HasExited) { throw 'Application crashed.' }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($handles.Count -ne 1) { throw "Expected one popup, observed $($handles.Count)." }
    $result = @{ success=$true; popupWindows=$handles.Count; android=[bool]$WaitForAndroid; dataDir=$dataDir }
    $result | ConvertTo-Json | Tee-Object -FilePath (Join-Path $dataDir 'result.json')
}
finally {
    if (-not $process.HasExited) {
        $process.Refresh()
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(15000)) { $process.Kill(); throw 'Graceful shutdown failed.' }
    }
}
