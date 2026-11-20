param(
    [string]$SdkRoot = $env:ANDROID_HOME,
    [string]$JavaHome = $env:JAVA_HOME,
    [string]$BuildTools,
    [string]$Platform,
    [string]$CompatibilityJar
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $BuildTools) { $BuildTools = Join-Path $SdkRoot 'build-tools/35.0.0' }
if (-not $Platform) { $Platform = Join-Path $SdkRoot 'platforms/android-35/android.jar' }
if (-not $CompatibilityJar) { $CompatibilityJar = Join-Path $SdkRoot 'platforms/android-19/android.jar' }
if (-not $JavaHome) { throw 'Set JAVA_HOME to a JDK installation (17 or later).' }
$JavaHome = $env:JAVA_HOME = (Resolve-Path -LiteralPath $JavaHome).Path
$BuildTools = (Resolve-Path -LiteralPath $BuildTools).Path
$Platform = (Resolve-Path -LiteralPath $Platform).Path
$CompatibilityJar = (Resolve-Path -LiteralPath $CompatibilityJar).Path

function Invoke-Tool([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Failed: $Tool (exit $LASTEXITCODE)" }
}

$build = Join-Path $root 'build'
$classes = Join-Path $build 'classes'
$signing = Join-Path $root '.signing'
$key = Join-Path $signing 'metrik-dev.p12'
$savedPassword = Join-Path $signing 'metrik-dev-password.xml'
$apk = Join-Path $build 'Metrik-Tablet.apk'
New-Item -ItemType Directory -Path $build,$classes,$signing -Force | Out-Null
Get-ChildItem -LiteralPath $classes -Recurse -File -Filter '*.class' |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName }

$aapt2 = Join-Path $BuildTools 'aapt2.exe'
$res = Join-Path $build 'resources.zip'
$unsigned = Join-Path $build 'unsigned.apk'
$aligned = Join-Path $build 'aligned.apk'
$javaArchive = Join-Path $build 'compiled.jar'
Invoke-Tool $aapt2 @('compile', '--dir', (Join-Path $root 'res'), '-o', $res)
Invoke-Tool $aapt2 @('link', '-I', $Platform, '--manifest', (Join-Path $root 'AndroidManifest.xml'), '-o', $unsigned, $res)

$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -Filter '*.java' | ForEach-Object FullName)
Invoke-Tool (Join-Path $JavaHome 'bin/javac.exe') (@('-encoding', 'UTF-8', '--release', '8',
    '-classpath', $CompatibilityJar, '-d', $classes) + $sources)
Invoke-Tool (Join-Path $JavaHome 'bin/jar.exe') @('--create', '--file', $javaArchive, '-C', $classes, 'com')
Invoke-Tool (Join-Path $BuildTools 'd8.bat') @('--release', '--min-api', '19', '--lib', $CompatibilityJar, '--output', $build, $javaArchive)
Push-Location -LiteralPath $build
try { Invoke-Tool (Join-Path $BuildTools 'aapt.exe') @('add', $unsigned, 'classes.dex') }
finally { Pop-Location }
Invoke-Tool (Join-Path $BuildTools 'zipalign.exe') @('-f', '4', $unsigned, $aligned)

$readPassword = -not $env:METRIK_ANDROID_KEYSTORE_PASSWORD
if ($readPassword) {
    $password = if (Test-Path -LiteralPath $savedPassword) {
        Import-Clixml -LiteralPath $savedPassword
    } else {
        Read-Host 'Metrik Android imza parolası' -AsSecureString
    }
    if ($password -isnot [System.Security.SecureString] -or $password.Length -lt 6) {
        throw 'İmza parolası en az 6 karakter olmalı.'
    }
    $env:METRIK_ANDROID_KEYSTORE_PASSWORD = [Net.NetworkCredential]::new('', $password).Password
}
try {
    if (-not (Test-Path -LiteralPath $key)) {
        Invoke-Tool (Join-Path $JavaHome 'bin/keytool.exe') @('-genkeypair', '-keystore', $key,
            '-storetype', 'PKCS12', '-storepass:env', 'METRIK_ANDROID_KEYSTORE_PASSWORD', '-alias', 'metrik',
            '-keyalg', 'RSA', '-keysize', '2048', '-validity', '10000', '-dname', 'CN=Metrik Local Build', '-noprompt')
    }
    Invoke-Tool (Join-Path $BuildTools 'apksigner.bat') @('sign', '--ks', $key, '--ks-key-alias', 'metrik',
        '--ks-pass', 'env:METRIK_ANDROID_KEYSTORE_PASSWORD', '--v1-signing-enabled', 'true', '--v2-signing-enabled', 'true',
        '--out', $apk, $aligned)
} finally {
    if ($readPassword) { Remove-Item Env:METRIK_ANDROID_KEYSTORE_PASSWORD }
}
Invoke-Tool (Join-Path $BuildTools 'apksigner.bat') @('verify', '--verbose', $apk)
Invoke-Tool (Join-Path $BuildTools 'zipalign.exe') @('-c', '4', $apk)
Get-Item -LiteralPath $apk | Select-Object FullName,Length
