@echo off
setlocal

REM Ensure headless-container is at v0.8.0 (3317121) which has the newest RPC types.
REM controller's own submodule pin may lag behind, so we override before copy.
set HEADLESS_PIN=33171212bc86793992a64e2e629389a339e99089
pushd controller\headless-container
git rev-parse HEAD > "%TEMP%\_hdc_head.txt"
set /p CUR_HEAD=<"%TEMP%\_hdc_head.txt"
del "%TEMP%\_hdc_head.txt"
if not "%CUR_HEAD%"=="%HEADLESS_PIN%" (
    echo Switching headless-container from %CUR_HEAD% to %HEADLESS_PIN%
    git fetch origin
    git checkout %HEADLESS_PIN%
)
popd

REM Copy proto files from controller submodule
if exist proto rmdir /S /Q proto
mkdir proto 2>nul
xcopy /Y /E controller\proto\hdlctrl proto\
xcopy /Y /E controller\headless-container\proto\* proto\

REM Generate POCO classes using ProtoPocoGen
dotnet run --project tools/ProtoPocoGen -- --input proto --output BaruHDLIntegration/Generated

endlocal
