$ErrorActionPreference = 'Stop'
$prefireCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $prefireCompiler)) {
    throw 'Der 64-Bit-.NET-Framework-C#-Compiler wurde nicht gefunden.'
}
Push-Location -LiteralPath $PSScriptRoot
try {
    & $prefireCompiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /out:PrefireMacro.exe PrefireMacro.cs CaptureWizard.cs InputSequence.cs HealthBarDetector.cs HealthBarTracker.cs HudTransition.cs PrefireGate.cs GateSequenceTests.cs DetectionTrace.cs MjpegAviWriter.cs GameplayRecorder.cs HudReplay.cs ShotVerifier.cs MaterialHudTests.cs HudAimGuard.cs AdaptiveBuildTiming.cs AdaptiveTimingTests.cs DarkTheme.cs MainInterface.cs DesktopFrameCapture.cs AssemblyInfo.cs
    if ($LASTEXITCODE -ne 0) { throw 'Kompilierung fehlgeschlagen.' }
    Write-Output 'PrefireMacro.exe wurde erstellt.'
}
finally { Pop-Location }
