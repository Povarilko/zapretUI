# zapret UI release build (native launcher with embedded payload)
# Result: NativeLauncher\publish\zapret-ui.exe
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Run-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet exited with code $LASTEXITCODE" }
}

# 1) Framework-dependent app - becomes the launcher's payload
Run-Dotnet @('publish', 'zapret-ui.csproj', '-c', 'Release', '-o', 'bin/Release/fdd', '-p:SelfContained=false', '-p:PublishSingleFile=false')

# 2) Copy payload files next to the launcher sources
New-Item -ItemType Directory -Path NativeLauncher\payload -Force | Out-Null
Copy-Item bin\Release\fdd\zapret-ui.dll, bin\Release\fdd\zapret-ui.runtimeconfig.json, bin\Release\fdd\zapret-ui.deps.json, bin\Release\fdd\zapret-ui.exe -Destination NativeLauncher\payload -Force

# 3) Native launcher (cl.exe from Visual Studio)
$vcvars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found: $vcvars" }
New-Item -ItemType Directory -Path NativeLauncher\publish -Force | Out-Null
cmd /c "`"$vcvars`" >nul && cd /d NativeLauncher && rc /nologo launcher.rc && cl /nologo /O1 /Os /MD /GS- /utf-8 launcher.c launcher.res /link /SUBSYSTEM:WINDOWS /ENTRY:wWinMainCRTStartup /OUT:publish\zapret-ui.exe"
if ($LASTEXITCODE -ne 0) { throw "native build failed with code $LASTEXITCODE" }

Write-Host ""
Write-Host "Done: NativeLauncher\publish\zapret-ui.exe" -ForegroundColor Green
