@echo off
setlocal
set "ROOT=%~dp0"
if not defined DOTNET set "DOTNET=dotnet"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_GENERATE_ASPNET_CERTIFICATE=false"
set "DOTNET_NOLOGO=true"
set "DOTNET_CLI_UI_LANGUAGE=en"
"%DOTNET%" build "%ROOT%Windows\Translated.Windows.csproj" -c Release --nologo
if errorlevel 1 exit /b 1
"%DOTNET%" build "%ROOT%Windows\Tests\Translated.Tests.csproj" -c Release -f net48 --nologo
if errorlevel 1 exit /b 1
"%ROOT%Windows\Tests\bin\Release\net48\Translated.Tests.exe"
if errorlevel 1 exit /b 1
"%ROOT%Windows\Tests\bin\Release\net48\Translated.Tests.exe" --lifecycle
if errorlevel 1 exit /b 1
if /I "%~1"=="--desktop" (
  "%ROOT%Windows\Tests\bin\Release\net48\Translated.Tests.exe" --desktop
  if errorlevel 1 exit /b 1
)
echo Build and requested checks completed. Output: Windows\bin\Release\net48\Translated.exe
exit /b 0
