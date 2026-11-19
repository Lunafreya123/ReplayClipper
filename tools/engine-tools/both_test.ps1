# Saves the same footage with the direct join and with ffmpeg ("--join both") and compares the two MP4s
# box by box and sample by sample (mp4_check.py). The helper shows a test video file, not the screen.
#   powershell -ExecutionPolicy Bypass -File both_test.ps1 -Root <work folder> [-Chains hevc,x264,h264] [-Tag t]
#              [-Fps 60] [-Size 1280x720] [-Seg 2] [-Audio 0]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$Chains = 'hevc,x264,h264',
    [string]$Tag = 'both',
    [int]$Fps = 60,
    [string]$Size = '1280x720',
    [int]$Seg = 2,
    [int]$Audio = 0,
    [string]$Python = 'python'
)
$t = "$Root\engine-tools"
foreach ($v in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
$out = "$Root\bench\proof"
New-Item -ItemType Directory -Force $out | Out-Null
Set-Location "$Root\bench"
$common = @('--audio', "$Audio", '--videofile', "$Root\bench\media\pattern840s.mp4",
    '--engine', "$Root\ReplayClipper\engine\bin\64bit\ReplayClipperEngine.exe", '--ffmpeg', "$Root\ReplayClipper\ffmpeg\ffmpeg.exe",
    '--data', "$Root\bench\data", '--out', $out, '--size', $Size, '--fps', "$Fps", '--seg', "$Seg", '--buffer', '600', '--stat', '600', '--join', 'both')
foreach ($chain in $Chains.Split(',')) {
    "##### $chain  ${Size}@$Fps  segments of $Seg s"
    $script = "5:save:0:$Tag-$chain-start;7:killhelper;15:killhelper;24:killhelper;30:save:9:$Tag-$chain-mid;36:save:0:$Tag-$chain-3breaks"
    & "$t\EngineHarness.exe" run --seconds 40 --ring "$Root\bench\rings\$Tag-$chain" --chain $chain --script $script @common --log "$Root\bench\$Tag-$chain.log" |
        Select-String 'recording started|saved|save:|ERROR|EXCEPTION|FAILED|failed' | Select-Object -First 12 |
        ForEach-Object { ($_.Line -replace [regex]::Escape("$out\"), '').Trim() }
    foreach ($name in 'start', 'mid', '3breaks') {
        $f = "$out\$Tag-$chain-$name.mp4"
        if (-not (Test-Path "$f.ffmpeg.mp4")) { "   $Tag-$chain-$name : no ffmpeg twin (the direct join was not used)"; continue }
        $r = & $Python -I "$t\mp4_check.py" $f "$f.ffmpeg.mp4"
        $lines = $r | Select-String 'DIFFERENT|PROBLEM|identical|RESULT' | ForEach-Object { $_.Line.Trim() -replace 'chunk layout boxes stsc/stco are expected to differ', 'chunk layout aside' }
        "   $Tag-$chain-$name : " + ($lines -join ' | ')
    }
}
