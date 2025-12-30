@echo off

REM Copy proto files from controller submodule
if exist proto rmdir /S /Q proto
mkdir proto 2>nul
xcopy /Y /E controller\proto\hdlctrl proto\
xcopy /Y /E controller\headless-container\proto\* proto\

REM Generate POCO classes using ProtoPocoGen
dotnet run --project tools/ProtoPocoGen -- --input proto --output BaruHDLIntegration/Generated
