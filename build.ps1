param([string]$OutputPath = (Join-Path $PSScriptRoot "LoveMyShelf.exe"))

$ErrorActionPreference = "Stop"
$release = Join-Path $PSScriptRoot "LoveMyShelf.exe"
# Source files live in src; the program is built next to this script.
$source = Join-Path $PSScriptRoot "src"
$inPlace = [IO.Path]::GetFullPath($OutputPath) -eq $release
# Building in place compiles to a staged file first, so a running LoveMyShelf never blocks the build.
$compileTo = if ($inPlace) { Join-Path $PSScriptRoot ([Guid]::NewGuid().ToString('N') + '.build.tmp') } else { $OutputPath }
$framework = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319"
if (-not (Test-Path -LiteralPath $framework)) {
    $framework = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
}
$references = @("System.dll", "System.Core.dll", "System.Data.dll", "System.Xml.dll", "System.Xml.Linq.dll", "System.Drawing.dll", "System.Windows.Forms.dll", "System.Xaml.dll", "WPF\PresentationCore.dll", "WPF\PresentationFramework.dll", "WPF\WindowsBase.dll")
$arguments = @("/nologo", "/target:winexe", "/platform:anycpu", "/optimize+", "/codepage:65001", "/main:LoveMyShelf.Program", "/out:$compileTo", "/win32manifest:$source\app.manifest", "/resource:$source\Theme.xaml,LoveMyShelf.Theme.xaml", "/resource:$source\release-notes.xml,LoveMyShelf.release-notes.xml")
foreach ($reference in $references) { $arguments += "/reference:" + (Join-Path $framework $reference) }
# The Windows share window comes from Windows Runtime metadata that ships with Windows 10 and 11.
$winmd = Join-Path $env:WINDIR "System32\WinMetadata"
foreach ($reference in @("Windows.ApplicationModel.winmd", "Windows.Foundation.winmd", "Windows.Storage.winmd")) { $arguments += "/reference:" + (Join-Path $winmd $reference) }
foreach ($reference in @("System.Runtime.WindowsRuntime.dll", "System.Runtime.dll", "System.Runtime.InteropServices.WindowsRuntime.dll")) { $arguments += "/reference:" + (Join-Path $framework $reference) }
$arguments += @(Get-ChildItem -LiteralPath $source -Filter "*.cs" | ForEach-Object { $_.FullName })
$arguments += "/win32icon:$source\gallery.ico"
& (Join-Path $framework "csc.exe") @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

if (-not $inPlace) { return }

# Install the staged build as LoveMyShelf.exe.
# A running copy can't be overwritten but can be renamed, so it is moved aside to a .TMP name;
# the change shows the next time Love My Shelf starts.
function Install-Build([string]$source, [string]$target) {
    $copy = Join-Path $PSScriptRoot ([Guid]::NewGuid().ToString('N') + '.copy.tmp')
    Copy-Item -LiteralPath $source -Destination $copy
    try {
        if (Test-Path -LiteralPath $target) {
            try { [IO.File]::Replace($copy, $target, [System.Management.Automation.Language.NullString]::Value); return }
            catch [System.IO.IOException] { }
            catch [System.UnauthorizedAccessException] { }
            $aside = $target + '~RF' + [Guid]::NewGuid().ToString('N').Substring(0, 7) + '.TMP'
            [IO.File]::Move($target, $aside)
        }
        [IO.File]::Move($copy, $target)
    } finally {
        if (Test-Path -LiteralPath $copy) { Remove-Item -LiteralPath $copy }
    }
}
try {
    Install-Build $compileTo $release
} finally {
    Remove-Item -LiteralPath $compileTo -ErrorAction SilentlyContinue
}
# Remove copies moved aside earlier once they are no longer running.
Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.exe~RF*.TMP" | ForEach-Object {
    try { Remove-Item -LiteralPath $_.FullName -ErrorAction Stop } catch { }
}
