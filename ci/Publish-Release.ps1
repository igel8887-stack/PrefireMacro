$ErrorActionPreference = 'Stop'
$repository = 'igel8887-stack/PrefireMacro'
if ($env:GITHUB_REPOSITORY -ne $repository -or $env:GITHUB_REF -ne 'refs/heads/main' -or [string]::IsNullOrWhiteSpace($env:GH_TOKEN)) { throw 'Freigabe nur aus main im festgelegten Repository.' }
$projectRoot = Split-Path $PSScriptRoot
$metadata = Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/release.json') -Raw | ConvertFrom-Json
if ($metadata.commit -ne $env:GITHUB_SHA) { throw 'Build gehoert nicht zu diesem Commit.' }
$latestCommit = & gh api "repos/$repository/commits/main" --jq '.sha'
if ($LASTEXITCODE -ne 0) { throw 'main konnte nicht geprueft werden.' }
if ($latestCommit.Trim() -ne $metadata.commit) { Write-Output 'Neuerer Commit auf main: aelteren Build nicht veroeffentlichen.'; exit 0 }
$publicRoot = Join-Path $projectRoot 'Loader/public'
$names = @('PrefireMacro.exe','PrefireLoader.exe','latest.release','loader.release','PrefireLoader.zip','START-HIER.txt')
$files = @(Get-ChildItem -LiteralPath $publicRoot -File)
if ($files.Count -ne $names.Count -or @($files | Where-Object {$_.Name -notin $names}).Count -ne 0) { throw 'Unerwartete Freigabedateien.' }
$notes = @"
## Prefire $($metadata.version)

Diese Freigabe wurde aus main gebaut und mit den App- und Loader-Selbsttests geprueft.
Quellstand: $($metadata.commit)

Fuer den ersten Start nur PrefireLoader.exe oder PrefireLoader.zip herunterladen.
Danach dieselbe Loader-Datei behalten: Sie prueft GitHub Releases, verifiziert die signierte Prefire-Version und bietet den Start per Schaltflaeche an. Die App startet pausiert.

Keine persoenlichen Farben, Einstellungen, Aufnahmen oder Zugangsdaten enthalten.
Selbsttests simulieren Eingaben; sie belegen keine Reaktionszeit oder Wandplatzierung im Spiel.
"@
$notesFile = Join-Path $projectRoot 'artifacts/release-notes.md'
[IO.File]::WriteAllText($notesFile,$notes,[Text.UTF8Encoding]::new($false))
# Draft first: latest must never point at a release with missing assets.
& gh release create $metadata.tag --repo $repository --target $metadata.commit --draft --title "Prefire $($metadata.version)" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw 'Release-Entwurf konnte nicht erstellt werden.' }
$assets = @($names | ForEach-Object { Join-Path $publicRoot $_ })
& gh release upload $metadata.tag @assets --repo $repository
if ($LASTEXITCODE -ne 0) { throw 'Dateiupload fehlgeschlagen; Entwurf bleibt unveroeffentlicht.' }
$uploadedText = & gh release view $metadata.tag --repo $repository --json assets
if ($LASTEXITCODE -ne 0) { throw 'Uploadkontrolle fehlgeschlagen.' }
$uploaded = ($uploadedText | ConvertFrom-Json).assets
foreach ($file in $files) {
 $match = @($uploaded | Where-Object {$_.name -eq $file.Name -and $_.size -eq $file.Length})
 if ($match.Count -ne 1) { throw "Datei fehlt oder Groesse abweichend: $($file.Name)" }
}
& gh release edit $metadata.tag --repo $repository --draft=false --latest
if ($LASTEXITCODE -ne 0) { throw 'Release konnte nicht freigegeben werden.' }
Write-Output "GitHub Release $($metadata.tag) veroeffentlicht."
