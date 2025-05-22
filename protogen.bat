@echo off

if exist proto rmdir /S /Q proto
mkdir proto 2>nul
xcopy /Y /E controller\proto\hdlctrl proto\
xcopy /Y /E controller\headless-container\proto\* proto\

go run github.com/bufbuild/buf/cmd/buf@v1.54.0 generate
