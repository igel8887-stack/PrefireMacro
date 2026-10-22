$ErrorActionPreference = 'Stop'
if (-not [string]::IsNullOrEmpty($env:PREFIRE_SIGNING_KEY)) { throw 'App-Tests duerfen keinen Signierschluessel in ihrer Prozessumgebung erhalten.' }
$projectRoot = Split-Path $PSScriptRoot
& (Join-Path $projectRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'App-Build fehlgeschlagen.' }
$reportRoot = Join-Path $projectRoot 'artifacts/tests'
[IO.Directory]::CreateDirectory($reportRoot) | Out-Null
$report = Join-Path $reportRoot 'self-test.txt'
$test = Start-Process -FilePath (Join-Path $projectRoot 'PrefireMacro.exe') -ArgumentList '--self-test',('"{0}"' -f $report) -WindowStyle Hidden -PassThru
if (-not $test.WaitForExit(60000)) { throw 'Selbsttest nach 60 Sekunden noch nicht abgeschlossen.' }
if ($test.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report) -or -not ([IO.File]::ReadAllText($report).StartsWith('PASS'))) { throw 'App-Selbsttest fehlgeschlagen.' }
Write-Output 'Prefire-Build und Selbsttest bestanden. Alle Eingaben im Test simuliert.'
