# Starts one proof recording detached and returns at once (the run itself takes -Seconds).
# The helper shows a test video file, not the screen; a continuous reference recording is written next to
# the ring by the helper's debug output. Output: bench\proof\<Tag>.mp4 (+ .ffmpeg.mp4 with -Join both),
# bench\proof\ref-<Tag>-<session>.ts, bench\<Tag>.stdout.txt, bench\<Tag>.pid
#   powershell -ExecutionPolicy Bypass -File proof_run.ps1 -Root <work folder> -Tag proof-hevc -Chain hevc -Seconds 656 -Script "320:killhelper;640:save:0:proof-hevc"
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Tag,
    [string]$Chain = 'hevc',
    [int]$Seconds = 656,
    [Parameter(Mandatory = $true)][string]$Script,
    [string]$Join = 'both',
    [int]$Fps = 60,
    [string]$Size = '1280x720',
    [int]$Buffer = 1800,
    [switch]$NoReference
)
foreach ($v in 'HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
$out = "$Root\bench\proof"
New-Item -ItemType Directory -Force $out | Out-Null
$a = @('run', '--seconds', "$Seconds", '--ring', "`"$Root\bench\rings\$Tag`"", '--chain', $Chain, '--script', "`"$Script`"",
    '--audio', '0', '--videofile', "`"$Root\bench\media\pattern840s.mp4`"",
    '--engine', "`"$Root\ReplayClipper\engine\bin\64bit\ReplayClipperEngine.exe`"", '--ffmpeg', "`"$Root\ReplayClipper\ffmpeg\ffmpeg.exe`"",
    '--data', "`"$Root\bench\data`"", '--out', "`"$out`"", '--size', $Size, '--fps', "$Fps", '--buffer', "$Buffer", '--stat', '120',
    '--log', "`"$Root\bench\$Tag.log`"")
if ($Join.Length -gt 0) { $a += @('--join', $Join) }
if (-not $NoReference) { $a += @('--reference', "`"$out\ref-$Tag.ts`"") }
$p = Start-Process -FilePath "$Root\engine-tools\EngineHarness.exe" -ArgumentList $a -WorkingDirectory "$Root\bench" -WindowStyle Hidden `
    -RedirectStandardOutput "$Root\bench\$Tag.stdout.txt" -RedirectStandardError "$Root\bench\$Tag.stderr.txt" -PassThru
Set-Content -Path "$Root\bench\$Tag.pid" -Value $p.Id -Encoding ascii
"started $Tag as pid $($p.Id)"
