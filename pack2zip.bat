@echo off

if exist pack_tmp rmdir /S /Q pack_tmp
if exist BaruHDLIntegration.zip del BaruHDLIntegration.zip

mkdir pack_tmp 2>nul
mkdir pack_tmp\rml_mods 2>nul

copy BaruHDLIntegration\bin\Release\net9.0\BaruHDLIntegration.dll pack_tmp\rml_mods\
copy BaruHDLIntegration\bin\Release\net9.0\Google.Protobuf.dll pack_tmp\

powershell -command "Compress-Archive -Path 'pack_tmp\*' -DestinationPath 'BaruHDLIntegration.zip' -Force"

rmdir /S /Q pack_tmp
