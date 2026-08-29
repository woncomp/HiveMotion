@echo off
setlocal

rem Builds the HiveMotion app into publish\ (flat). With the "installer"
rem argument, also builds the Inno Setup installer into Installer\.
rem
rem   build-publish.bat            - app only:  publish\HiveMotion.exe
rem   build-publish.bat installer  - also builds Installer\HiveMotion-Setup-{version}.exe
rem
rem The version is read from HiveMotionVersion in Directory.Build.props;
rem no version argument is needed or accepted.

set "ROOT=%~dp0"

for /f "delims=" %%v in ('dotnet msbuild "%ROOT%Directory.Build.props" -getProperty:HiveMotionVersion 2^>nul') do set "VERSION=%%v"
if not defined VERSION (
    echo ERROR: could not read HiveMotionVersion from Directory.Build.props.
    goto :fail
)

if /i not "%~1"=="installer" goto :publish_app

set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%LocalAppData%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (
    echo ERROR: Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php or via: winget install JRSoftware.InnoSetup
    goto :fail
)

:publish_app
if exist "%ROOT%publish" rmdir /s /q "%ROOT%publish"

echo Publishing HiveMotion %VERSION% ^(framework-dependent, win-x64^)...
dotnet publish "%ROOT%HiveMotion\HiveMotion.csproj" ^
    --configuration Release ^
    --runtime win-x64 ^
    --self-contained false ^
    --output "%ROOT%publish" ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    goto :fail
)

if /i not "%~1"=="installer" (
    echo.
    echo Done: %ROOT%publish\HiveMotion.exe
    echo Run "build-publish.bat installer" to also build the installer.
    exit /b 0
)

echo Building installer...
"%ISCC%" "/DMyAppVersion=%VERSION%" "%ROOT%Installer\HiveMotion.iss"
if errorlevel 1 (
    echo ERROR: Inno Setup compile failed.
    goto :fail
)

echo.
echo Done. Outputs:
echo   %ROOT%publish\HiveMotion.exe
echo   %ROOT%Installer\HiveMotion-Setup-%VERSION%.exe
exit /b 0

:fail
if "%~1"=="" pause
exit /b 1
