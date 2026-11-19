# Times whole saves (helper + client) on prepared rings, with either join, warm or cold file cache.
#   powershell -ExecutionPolicy Bypass -File measure.ps1 -Root <work folder> -Runs "r30:direct:warm,r30:ffmpeg:cold,..."
# "cold": the ring's files are first dropped from the Windows file cache (JoinBench evict), so the save reads
# them from the disk. Each run starts a helper on the ring (which adds a few seconds of new footage), saves
# everything once and deletes the clip.
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Runs
)
$t = "$Root\engine-tools"
foreach ($v in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
$out = "$Root\bench\out"
New-Item -ItemType Directory -Force $out | Out-Null
Set-Location "$Root\bench"
$common = @('--audio', '0', '--videofile', "$Root\bench\media\pattern840s.mp4",
    '--engine', "$Root\ReplayClipper\engine\bin\64bit\ReplayClipperEngine.exe", '--ffmpeg', "$Root\ReplayClipper\ffmpeg\ffmpeg.exe",
    '--data', "$Root\bench\data", '--out', $out, '--buffer', '40000', '--stat', '600')
foreach ($run in $Runs.Split(',')) {
    $ring, $join, $cache = $run.Split(':')
    if ($cache -eq 'cold') { & "$t\JoinBench.exe" evict "$Root\bench\rings\$ring" 0 | Out-Null }
    else { & "$t\JoinBench.exe" read "$Root\bench\rings\$ring" 0 | Out-Null }
    $o = & "$t\EngineHarness.exe" run --seconds 5 --ring "$Root\bench\rings\$ring" --join $join --script "2:save:0:m-$ring-$join" @common --log "$Root\bench\measure.log"
    $save = ($o | Select-String 'SAVE .*asked' | ForEach-Object { ($_.Line -replace '^\s*[\d.]+\s+SAVE [^:]+: asked 0s -> ', '' -replace '\s+\(.*$', '').Trim() }) -join ' '
    $how = ($o | Select-String 'saved .*\((direct|ffmpeg)\)' | ForEach-Object { if ($_.Line -match '\((direct|ffmpeg)\)\s*$') { $Matches[1] } }) -join ''
    $bad = ($o | Select-String 'FAILED|failed|EXCEPTION' | ForEach-Object { $_.Line.Trim() }) -join ' | '
    "{0,-4} {1,-7} {2,-5} {3}  [{4}] {5}" -f $ring, $join, $cache, $save, $how, $bad
    Get-ChildItem $out -File | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -Confirm:$false }
}
