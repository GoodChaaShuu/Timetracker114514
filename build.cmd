@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] "dotnet" not found. Install the .NET 8 SDK first:
    echo         https://dotnet.microsoft.com/download/dotnet/8.0
    echo         Then reopen the terminal and run this script again.
    pause
    exit /b 1
)

echo === [1/2] Cleaning previous output ===
if exist dist rmdir /s /q dist

echo.
echo === [2/2] Publishing single-file EXE ^(first run restores packages^) ===
dotnet publish TimeTracker.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -p:SatelliteResourceLanguages=en ^
  -o dist

if errorlevel 1 (
    echo.
    echo [FAILED] Publish failed. See the messages above.
    pause
    exit /b 1
)

echo.
echo === Done ===
echo Output: %~dp0dist\TimeTracker.exe
for %%F in ("%~dp0dist\TimeTracker.exe") do echo Size  : %%~zF bytes
echo.
echo Double-click the EXE to run. No .NET runtime required on the target machine.
pause
