param(
    [string]$Cli = "$env:TEMP\aibridge-perf-current\AIBridgeCLI.exe",
    [string]$ProjectRoot = "C:\lys-work\ET9-SNMSL",
    [string]$Symbol = "PlayerSystem",
    [string[]]$BatchSymbols = @(
        "PlayerSystem",
        "C2G_LoginGateHandler",
        "EnterMapHelper",
        "GateSessionKeyComponent",
        "SessionPlayerComponent",
        "RealmGateAddressHelper",
        "ITransfer",
        "IClientPullable",
        "ComponentSyncPacket",
        "MarqueeDefine",
        "DelaySendPlayerOnlineEvent",
        "OnNewPlayerLoginGame",
        "CodeIndexCommand",
        "CodeIndexWorkspace",
        "CodeIndexServer",
        "CodeIndexQueryScheduler",
        "AIBridgeCodeIndexEditorUtility",
        "AIBridgeCodeIndexSnapshotUtility",
        "SkillInstaller",
        "AIBridgeProjectSettings"
    ),
    [int]$ParallelSymbolCount = 30,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Invoke-CodeIndex {
    param(
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & $Cli @Arguments
    $exitCode = $LASTEXITCODE
    $text = ($output | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "code_index returned empty output for: $($Arguments -join ' ')"
    }

    $json = $text | ConvertFrom-Json
    if (-not $AllowFailure -and ($exitCode -ne 0 -or -not $json.success)) {
        throw "code_index failed for '$($Arguments -join ' ')': $text"
    }

    return $json
}

function Get-CodeIndexDaemonPids {
    $indexDirectory = Join-Path $ProjectRoot ".aibridge\code-index"
    $paths = @()
    $primary = Join-Path $indexDirectory "daemon-process.json"
    if (Test-Path $primary) {
        $paths += $primary
    }

    $markerDirectory = Join-Path $indexDirectory "daemon-processes"
    if (Test-Path $markerDirectory) {
        $paths += Get-ChildItem -Path $markerDirectory -Filter "*.json" | ForEach-Object { $_.FullName }
    }

    $pids = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($path in $paths) {
        try {
            $marker = Get-Content -Path $path -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($marker.projectRoot -ne $ProjectRoot) {
                continue
            }

            $daemonProcessId = [int]$marker.daemonPid
            $process = Get-Process -Id $daemonProcessId -ErrorAction SilentlyContinue
            if ($null -ne $process) {
                [void]$pids.Add($daemonProcessId)
            }
        }
        catch {
        }
    }

    return @($pids)
}

if (-not (Test-Path $Cli)) {
    throw "CLI not found: $Cli"
}

Write-Host "Reset Code Index state..."
Invoke-CodeIndex @("code_index", "reset", "--project-root", $ProjectRoot, "--timeout", "60000") | Out-Null

Write-Host "Warmup Code Index daemon..."
$warmup = Invoke-CodeIndex @("code_index", "warmup", "--project-root", $ProjectRoot, "--timeout", "60000")
Assert-True ($warmup.state -eq "ready") "warmup did not reach ready state."

$status = Invoke-CodeIndex @("code_index", "status", "--project-root", $ProjectRoot, "--timeout", "5000")
Assert-True ($status.state -eq "ready") "status is not ready after warmup."

Write-Host "Run $ParallelSymbolCount concurrent symbol queries..."
$runDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("aibridge-codeindex-regression-" + [System.Guid]::NewGuid().ToString("N"))
New-Item -Path $runDirectory -ItemType Directory -Force | Out-Null
$processes = @()
try {
    for ($i = 0; $i -lt $ParallelSymbolCount; $i++) {
        $stdoutPath = Join-Path $runDirectory ($i.ToString() + ".out.json")
        $stderrPath = Join-Path $runDirectory ($i.ToString() + ".err.txt")
        $process = Start-Process `
            -FilePath $Cli `
            -ArgumentList @("code_index", "symbol", "--project-root", $ProjectRoot, "--query", $Symbol, "--timeout", "60000") `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath `
            -WindowStyle Hidden `
            -PassThru

        $processes += [pscustomobject]@{
            Process = $process
            Stdout = $stdoutPath
            Stderr = $stderrPath
        }
    }

    Start-Sleep -Seconds 1
    $statusDuringConcurrency = Invoke-CodeIndex @("code_index", "status", "--project-root", $ProjectRoot, "--timeout", "5000")
    Assert-True ($statusDuringConcurrency.success -eq $true) "status failed while concurrent symbol queries were running."

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $running = @($processes | Where-Object { -not $_.Process.HasExited })
        if ($running.Count -eq 0) {
            break
        }

        Start-Sleep -Milliseconds 250
    }

    $running = @($processes | Where-Object { -not $_.Process.HasExited })
    if ($running.Count -gt 0) {
        foreach ($item in $running) {
            try {
                Stop-Process -Id $item.Process.Id -Force
            }
            catch {
            }
        }

        throw "concurrent symbol processes did not finish within $TimeoutSeconds seconds."
    }

    $symbolResults = foreach ($item in $processes) {
        $text = Get-Content -Path $item.Stdout -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($text)) {
            $errorText = if (Test-Path $item.Stderr) { Get-Content -Path $item.Stderr -Raw -Encoding UTF8 } else { "" }
            throw "concurrent symbol process returned empty output. stderr=$errorText"
        }

        $text.Trim() | ConvertFrom-Json
    }
}
finally {
    if (Test-Path $runDirectory) {
        Remove-Item -Path $runDirectory -Recurse -Force
    }
}

$successCount = @($symbolResults | Where-Object { $_.success -eq $true }).Count
Assert-True ($successCount -eq $ParallelSymbolCount) "concurrent symbol success count is $successCount/$ParallelSymbolCount."

$daemonPids = Get-CodeIndexDaemonPids
Assert-True ($daemonPids.Count -eq 1) "expected 1 active daemon marker, got $($daemonPids.Count): $($daemonPids -join ', ')"

Write-Host "Compare independent symbol calls with batch..."
$independentWatch = [System.Diagnostics.Stopwatch]::StartNew()
foreach ($item in $BatchSymbols) {
    Invoke-CodeIndex @("code_index", "symbol", "--project-root", $ProjectRoot, "--query", $item, "--timeout", "60000") | Out-Null
}
$independentWatch.Stop()

$payload = @{
    timing = $true
    continueOnError = $true
    items = @($BatchSymbols | ForEach-Object {
        @{
            action = "symbol"
            parameters = @{
                query = $_
            }
        }
    })
} | ConvertTo-Json -Depth 6 -Compress

$batchInputPath = [System.IO.Path]::GetTempFileName()
$batchOutputPath = [System.IO.Path]::GetTempFileName()
$batchErrorPath = [System.IO.Path]::GetTempFileName()
try {
    [System.IO.File]::WriteAllText($batchInputPath, $payload, [System.Text.UTF8Encoding]::new($false))
    $batchWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $batchProcess = Start-Process `
        -FilePath $Cli `
        -ArgumentList @("code_index", "batch", "--project-root", $ProjectRoot, "--stdin", "--timeout", "60000") `
        -RedirectStandardInput $batchInputPath `
        -RedirectStandardOutput $batchOutputPath `
        -RedirectStandardError $batchErrorPath `
        -WindowStyle Hidden `
        -Wait `
        -PassThru
    $batchWatch.Stop()

    $batchText = Get-Content -Path $batchOutputPath -Raw -Encoding UTF8
    if ($batchProcess.ExitCode -ne 0 -and [string]::IsNullOrWhiteSpace($batchText)) {
        $batchError = Get-Content -Path $batchErrorPath -Raw -Encoding UTF8
        throw "batch command failed. stderr=$batchError"
    }

    $batch = $batchText.Trim() | ConvertFrom-Json
}
finally {
    Remove-Item -Path $batchInputPath, $batchOutputPath, $batchErrorPath -Force -ErrorAction SilentlyContinue
}

Assert-True ($batch.success -eq $true) "batch symbol query failed."
if ($BatchSymbols.Count -ge 10) {
    Assert-True ($batchWatch.ElapsedMilliseconds -lt $independentWatch.ElapsedMilliseconds) "batch was not faster than independent calls. independent=$($independentWatch.ElapsedMilliseconds)ms batch=$($batchWatch.ElapsedMilliseconds)ms"
}
elseif ($batchWatch.ElapsedMilliseconds -ge $independentWatch.ElapsedMilliseconds) {
    Write-Warning "Batch was not faster on a small sample. independent=$($independentWatch.ElapsedMilliseconds)ms batch=$($batchWatch.ElapsedMilliseconds)ms"
}

Write-Host "Verify cache hit..."
$first = Invoke-CodeIndex @("code_index", "symbol", "--project-root", $ProjectRoot, "--query", $Symbol, "--timeout", "60000")
$second = Invoke-CodeIndex @("code_index", "symbol", "--project-root", $ProjectRoot, "--query", $Symbol, "--timeout", "60000")
Assert-True ($second.cacheHit -eq $true) "second symbol query did not hit cache."
Assert-True ($second.executionMs -le $first.executionMs) "cached query executionMs did not improve."

Write-Host "Code Index performance regression checks passed."
