# Test tool: watches the engine helper (and every process it starts) for network activity.
# Samples the TCP connections and UDP endpoints owned by those processes and logs any it sees.
#   powershell -File netwatch.ps1 -EngineExe <path to ReplayClipperEngine.exe> -Seconds 1900 -Log <file>
param([string]$EngineExe, [int]$Seconds = 120, [string]$Log = 'netwatch.log')
$sw = [Diagnostics.Stopwatch]::StartNew()
$samples = 0; $hits = 0; $seen = @{}
"start $(Get-Date -Format s) watching $EngineExe" | Set-Content $Log -Encoding utf8
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
    $procs = @(Get-CimInstance Win32_Process)
    $helpers = @($procs | Where-Object { $_.ExecutablePath -eq $EngineExe })
    $ids = @()
    foreach ($h in $helpers) {
        $ids += $h.ProcessId
        $ids += @($procs | Where-Object { $_.ParentProcessId -eq $h.ProcessId } | ForEach-Object { $seen[$_.Name] = 1; $_.ProcessId })
    }
    if ($ids.Count -gt 0) {
        $samples++
        $tcp = @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object { $ids -contains $_.OwningProcess })
        $udp = @(Get-NetUDPEndpoint -ErrorAction SilentlyContinue | Where-Object { $ids -contains $_.OwningProcess })
        foreach ($c in $tcp) { $hits++; "$(Get-Date -Format HH:mm:ss) TCP pid $($c.OwningProcess) $($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort) $($c.State)" | Add-Content $Log -Encoding utf8 }
        foreach ($u in $udp) { $hits++; "$(Get-Date -Format HH:mm:ss) UDP pid $($u.OwningProcess) $($u.LocalAddress):$($u.LocalPort)" | Add-Content $Log -Encoding utf8 }
    }
    # dense at the start (module load is when the stock updater would fire), sparse later
    if ($sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 300 } else { Start-Sleep -Seconds 4 }
}
"end $(Get-Date -Format s): $samples samples with the helper running, $hits socket sightings; child processes seen: $(($seen.Keys | Sort-Object) -join ', ')" | Add-Content $Log -Encoding utf8
