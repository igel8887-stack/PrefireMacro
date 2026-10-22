param([Parameter(Mandatory=$true)][int]$BuildNumber,[Parameter(Mandatory=$true)][string]$Commit)
$ErrorActionPreference = 'Stop'
if ($BuildNumber -lt 1 -or $BuildNumber -gt 2000000000 -or $Commit -notmatch '^[0-9a-f]{40}$') { throw 'Ungueltige Buildkennung.' }
$projectRoot = Split-Path $PSScriptRoot
$assemblyPath = Join-Path $projectRoot 'AssemblyInfo.cs'
$assemblyText = [IO.File]::ReadAllText($assemblyPath)
$versionMatch = [regex]::Match($assemblyText,'AssemblyFileVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)')
if (-not $versionMatch.Success) { throw 'App-Version fehlt.' }
# Every main build gets a higher numeric revision, even if only source files change.
$revisionMajor = [int][Math]::Floor($BuildNumber / 60000)
$revisionMinor = $BuildNumber % 60000
$version = '{0}.{1}.{2}.{3}' -f $versionMatch.Groups[1].Value,$versionMatch.Groups[2].Value,$revisionMajor,$revisionMinor
$assemblyText = [regex]::Replace($assemblyText,'(Assembly(?:File)?Version\(")[^"]+("\))',('${1}' + $version + '${2}'))
[IO.File]::WriteAllText($assemblyPath,$assemblyText,[Text.UTF8Encoding]::new($true))
& (Join-Path $PSScriptRoot 'Test-Prefire.ps1')
$tag = 'build-{0}-{1}' -f $BuildNumber,$Commit.Substring(0,12)
$assetRoot = "https://github.com/igel8887-stack/PrefireMacro/releases/download/$tag"
$loaderVersion = '1.3.{0}.{1}' -f $revisionMajor,$revisionMinor
$loaderParameters = @{ AssetRoot=$assetRoot; LoaderVersion=$loaderVersion }
if ($env:GITHUB_ACTIONS -eq 'true') { $loaderParameters.UseGitHubSecret = $true }
& (Join-Path $projectRoot 'Loader/build-loader.ps1') @loaderParameters
$publicRoot = Join-Path $projectRoot 'Loader/public'
$engineReport = Join-Path $projectRoot ('artifacts/loader-tests-' + [Guid]::NewGuid().ToString('N'))
$engineTest = Start-Process -FilePath (Join-Path $projectRoot 'Loader/PrefireLoader.exe') -ArgumentList '--self-test',('"{0}"' -f $engineReport) -WindowStyle Hidden -PassThru
if (-not $engineTest.WaitForExit(60000)) { throw 'Loader-Selbsttest nach 60 Sekunden noch nicht abgeschlossen.' }
if ($engineTest.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $engineReport 'result.txt')) -or -not ([IO.File]::ReadAllText((Join-Path $engineReport 'result.txt')).StartsWith('PASS'))) { throw 'Loader-Selbsttest fehlgeschlagen.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'Loader/START-HIER-LOADER.txt') -Destination (Join-Path $publicRoot 'START-HIER.txt') -Force
$zipPath = Join-Path $publicRoot 'PrefireLoader.zip'
Compress-Archive -LiteralPath (Join-Path $publicRoot 'PrefireLoader.exe'),(Join-Path $publicRoot 'START-HIER.txt') -DestinationPath $zipPath -Force
$metadata = [ordered]@{tag=$tag;version=$version;loaderVersion=$loaderVersion;commit=$Commit;build=$BuildNumber}
[IO.File]::WriteAllText((Join-Path $projectRoot 'artifacts/release.json'),($metadata | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
Write-Output "Freigabe $version vorbereitet; App und Loader getestet. Noch nicht veroeffentlicht."
