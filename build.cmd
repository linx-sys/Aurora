@echo off
rem ============================================================
rem  Aurora Player build script (uses the built-in .NET csc.exe)
rem ============================================================
setlocal
cd /d "%~dp0"

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set WPF=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF
set DEFS=/nologo /target:winexe /platform:anycpu /optimize+ /win32icon:assets\app.ico

if not exist build mkdir build

echo === [1/3] AuroraPlayer.exe ===
%CSC% %DEFS% /out:build\AuroraPlayer.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Xaml.dll /r:Microsoft.CSharp.dll ^
  /r:"%WPF%\PresentationFramework.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\WindowsBase.dll" ^
  /res:src\ui.xaml,Aurora.ui.xaml ^
  src\App.cs src\Model.cs src\Id3.cs src\Lrc.cs src\MainWindow.cs src\Spectro.cs src\Assoc.cs src\Tags.cs src\CoverArt.cs
if errorlevel 1 goto :fail

echo === [2/3] unins.exe ===
%CSC% %DEFS% /out:build\unins.exe ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\Uninstaller.cs
if errorlevel 1 goto :fail

echo === [3/3] AuroraPlayer-Setup.exe ===
%CSC% %DEFS% /out:AuroraPlayer-Setup.exe ^
  /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:Microsoft.CSharp.dll ^
  /res:build\AuroraPlayer.exe,Aurora.AuroraPlayer.exe ^
  /res:build\unins.exe,Aurora.unins.exe ^
  /res:assets\icon256.png,Aurora.icon.png ^
  src\Installer.cs
if errorlevel 1 goto :fail

echo.
echo BUILD OK
dir /b build AuroraPlayer-Setup.exe
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
