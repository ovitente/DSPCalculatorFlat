@echo off
rem Builds bin\DspFlat.Engine.dll (engine core), bin\MakeBlock.exe (CLI host) and bin\SelfTest.exe (self tests).
rem Windows only (.NET Framework 4.x csc, C# 5). The game assembly is NOT referenced at compile time; the CLI host
rem loads it read-only by reflection at run time (engine.host\GameBp.cs).
rem The core (engine\) is compiled alone against System + System.Core only, and is checked for I/O, console,
rem environment and culture side effects.
setlocal
set HERE=%~dp0
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set BIN=%HERE%bin
set OPT=/nologo /langversion:5 /optimize+ /warn:4
if not exist "%BIN%" mkdir "%BIN%"

findstr /n /r /c:"^using System\.IO" /c:"Console\." /c:"Environment\." /c:"CurrentCulture" /c:"GameBp\." "%HERE%engine\*.cs"
if not errorlevel 1 (echo engine core has a forbidden side effect, see the lines above & exit /b 1)

"%CSC%" %OPT% /target:library /out:"%BIN%\DspFlat.Engine.dll" /r:System.Core.dll "%HERE%engine\*.cs" || exit /b 1
"%CSC%" %OPT% /out:"%BIN%\MakeBlock.exe" /r:"%BIN%\DspFlat.Engine.dll" /r:System.Web.Extensions.dll /r:System.Core.dll "%HERE%engine.host\*.cs" "%HERE%cli\MakeBlock.cs" "%HERE%tests\SelfTest.cs" || exit /b 1
"%CSC%" %OPT% /out:"%BIN%\SelfTest.exe" /main:SelfTestMain /r:"%BIN%\DspFlat.Engine.dll" /r:System.Web.Extensions.dll /r:System.Core.dll "%HERE%engine.host\*.cs" "%HERE%cli\MakeBlock.cs" "%HERE%tests\*.cs" || exit /b 1
echo built %BIN%\DspFlat.Engine.dll, MakeBlock.exe, SelfTest.exe
