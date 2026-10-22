param([string]$FeedRoot = 'https://prefire-updates-igel8887-1179.vercel.app', [string]$LoaderVersion = '1.2.2.0', [switch]$InitializeSigningKey)
$ErrorActionPreference = 'Stop'
$expectedSignerFile = Join-Path $env:LOCALAPPDATA 'PrefirePublisher/release-key.dpapi'
if (-not (Test-Path -LiteralPath $expectedSignerFile) -and -not $InitializeSigningKey) {
    throw 'Kein Freigabeschluessel auf diesem PC. Fuer normale Mitarbeit nur build.ps1 verwenden. Nur der Herausgeber baut und signiert Loader-Freigaben. -InitializeSigningKey erstellt eine neue Vertrauensbasis und ist kein Import des bestehenden Schluessels.'
}
Add-Type -AssemblyName System.Security
$loaderRoot = $PSScriptRoot
$appFile = Join-Path (Split-Path $loaderRoot) 'PrefireMacro.exe'
$publicRoot = Join-Path $loaderRoot 'public'
$testRoot = Join-Path $loaderRoot 'fixtures'
$ownerRoot = Join-Path $env:LOCALAPPDATA 'PrefirePublisher'
[IO.Directory]::CreateDirectory($ownerRoot) | Out-Null
$ownerAcl = Get-Acl -LiteralPath $ownerRoot
$ownerAcl.SetAccessRuleProtection($true, $false)
$ownerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$ownerAcl.SetAccessRule([Security.AccessControl.FileSystemAccessRule]::new($ownerSid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
$existingRules = (Get-Acl -LiteralPath $ownerRoot).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])
$restricted = (Get-Acl -LiteralPath $ownerRoot).AreAccessRulesProtected -and $existingRules.Count -eq 1 -and $existingRules[0].IdentityReference -eq $ownerSid -and $existingRules[0].AccessControlType -eq 'Allow' -and $existingRules[0].FileSystemRights -eq 'FullControl'
if (-not $restricted) { Set-Acl -LiteralPath $ownerRoot -AclObject $ownerAcl }
$keyFile = Join-Path $ownerRoot 'release-key.dpapi'
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
$rsa.PersistKeyInCsp = $false
try {
 if (Test-Path -LiteralPath $keyFile) {
  $privateBytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($keyFile), $null, 'CurrentUser')
  try { $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($privateBytes)) } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
 } else {
  $privateBytes = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
  try { [IO.File]::WriteAllBytes($keyFile, [Security.Cryptography.ProtectedData]::Protect($privateBytes, $null, 'CurrentUser')) } finally { [Array]::Clear($privateBytes, 0, $privateBytes.Length) }
 }
 [IO.Directory]::CreateDirectory($publicRoot) | Out-Null
 [IO.Directory]::CreateDirectory($testRoot) | Out-Null
 $appBytes = [IO.File]::ReadAllBytes($appFile)
 $appHash = (Get-FileHash -LiteralPath $appFile -Algorithm SHA256).Hash.ToLowerInvariant()
 $appVersion = [Reflection.AssemblyName]::GetAssemblyName($appFile).Version.ToString()
 $settings = @"
namespace PrefireDistribution { internal static class LoaderSettings {
 internal const string PublicKey = @"$($rsa.ToXmlString($false))";
 internal const string BundledHash = "$appHash";
 internal const string BundledVersion = "$appVersion";
 internal const string Feed = "$FeedRoot/latest.release";
 internal const string LoaderFeed = "$FeedRoot/loader.release";
 internal const string LoaderVersion = "$LoaderVersion";
} }
"@
 [IO.File]::WriteAllText((Join-Path $loaderRoot 'LoaderSettings.cs'), $settings, [Text.UTF8Encoding]::new($true))
 $gzipFile = Join-Path $loaderRoot 'main.gz'
 $outputStream = [IO.File]::Create($gzipFile)
 $zipStream = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionMode]::Compress)
 try { $zipStream.Write($appBytes, 0, $appBytes.Length) } finally { $zipStream.Dispose(); $outputStream.Dispose() }
 $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
 & $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll ("/resource:{0},main.gz" -f $gzipFile) ("/out:{0}" -f (Join-Path $loaderRoot 'PrefireLoader.exe')) (Join-Path $loaderRoot 'PrefireLoader.cs') (Join-Path $loaderRoot 'LoaderSettings.cs') (Join-Path (Split-Path $loaderRoot) 'DarkTheme.cs')
 if ($LASTEXITCODE -ne 0) { throw 'Loader-Kompilierung fehlgeschlagen.' }
 function Write-Release([string]$Version, [string]$Name, [string]$Binary, [string]$Destination) {
  $releaseHash = (Get-FileHash -LiteralPath $Binary -Algorithm SHA256).Hash.ToLowerInvariant()
  $releaseSize = (Get-Item -LiteralPath $Binary).Length
  $body = "$Version`n$FeedRoot/$Name`n$releaseHash`n$releaseSize`n"
  $signature = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($body), 'SHA256'))
  [IO.File]::WriteAllText($Destination, $body + $signature + "`n", [Text.UTF8Encoding]::new($false))
 }
 Copy-Item -LiteralPath $appFile -Destination (Join-Path $publicRoot 'PrefireMacro.exe')
 Copy-Item -LiteralPath (Join-Path $loaderRoot 'PrefireLoader.exe') -Destination (Join-Path $publicRoot 'PrefireLoader.exe')
 Write-Release $appVersion 'PrefireMacro.exe' $appFile (Join-Path $publicRoot 'latest.release')
 Write-Release $LoaderVersion 'PrefireLoader.exe' (Join-Path $publicRoot 'PrefireLoader.exe') (Join-Path $publicRoot 'loader.release')
 Write-Release '99.0.0.0' 'PrefireMacro.exe' $appFile (Join-Path $testRoot 'upgrade.release')
 Write-Release '100.0.0.0' 'PrefireMacro.exe' $appFile (Join-Path $testRoot 'next.release')
 Write-Release '0.1.0.0' 'PrefireMacro.exe' $appFile (Join-Path $testRoot 'older.release')
 Write-Output "Loader erstellt. App-Version $appVersion. Signierschlüssel bleibt lokal und ist mit Windows DPAPI geschützt."
} finally { $rsa.Dispose() }
