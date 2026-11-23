@echo off
rem HD Studio complete workflow (validated for any NES ROM).
rem
rem Usage:
rem   hdstudio_workflow.cmd "game.nes" [--disasm=FOLDER] [--movie=FILE] [--frames=N]
rem
rem Examples:
rem   hdstudio_workflow.cmd "D:\ROMs\game.nes" --frames=1800
rem   hdstudio_workflow.cmd "D:\ROMs\Super Mario Bros. 3 (USA).nes" --disasm="D:\MesenHighWork\disasm\smb3-master"
rem   hdstudio_workflow.cmd "D:\ROMs\game.nes" --movie="D:\TAS\run.fm2" --frames=600
rem
rem Steps:
rem   1. capture with the automatic explorer (or the movie) and --generate
rem   2. if a disassembly is given: --maps (exact level maps/backgrounds + sprite sweep)
rem      and a short --generate to merge the exact tiles and the wildcard defaults
rem   3. report
rem   4. verify (HD pack active?) and side-by-side screenshots
rem
rem Every step runs in its own Mesen process, so a stuck game cannot poison the next step.

setlocal
set "HERE=%~dp0"
set "ROM="
set "DISASM="
set "MOVIE="
set "FRAMES=1800"

:parse
if "%~1"=="" goto :parsed
set "ARG=%~1"
if /i "%ARG%"=="--disasm" goto :arg_disasm
if /i "%ARG%"=="--movie" goto :arg_movie
if /i "%ARG%"=="--frames" goto :arg_frames
if /i "%ARG:~0,9%"=="--disasm=" set "DISASM=%ARG:~9%"
if /i "%ARG:~0,8%"=="--movie=" set "MOVIE=%ARG:~8%"
if /i "%ARG:~0,9%"=="--frames=" set "FRAMES=%ARG:~9%"
if not "%ARG:~0,2%"=="--" set "ROM=%ARG%"
shift
goto :parse

:arg_disasm
shift
set "DISASM=%~1"
shift
goto :parse

:arg_movie
shift
set "MOVIE=%~1"
shift
goto :parse

:arg_frames
shift
set "FRAMES=%~1"
shift
goto :parse

:parsed
if "%ROM%"=="" goto :usage

set "LOGDIR=%CD%\workflow_logs"
if not exist "%LOGDIR%" mkdir "%LOGDIR%"
set "MESEN=%HERE%bin\win-x64\Release\Mesen.exe"
if exist "%HERE%..\MesenHighWork\env.cmd" call "%HERE%..\MesenHighWork\env.cmd"

echo [workflow] ROM: %ROM%
echo [workflow] frames: %FRAMES%
echo [workflow] logs: %LOGDIR%
if not "%DISASM%"=="" echo [workflow] disassembly: %DISASM%
if not "%MOVIE%"=="" echo [workflow] movie: %MOVIE%

echo [workflow] step 1/4: capture + all CHR tiles + wildcard fallback
if not "%MOVIE%"=="" (
  call :run "%ROM%" "%MOVIE%" --generate --timeout=1800 --log="%LOGDIR%\capture.log"
) else (
  call :run "%ROM%" --generate --frames=%FRAMES% --timeout=1800 --log="%LOGDIR%\capture.log"
)
if errorlevel 1 goto :error

if not "%DISASM%"=="" (
  echo [workflow] step 2/4: exact level maps, backgrounds and sprite palette sweep
  call :run "%ROM%" --maps --disasm="%DISASM%" --timeout=600 --log="%LOGDIR%\maps.log"
  if errorlevel 1 goto :error

  echo [workflow] step 2b/4: merge exact tiles and wildcard defaults into the pack
  call :run "%ROM%" --generate --frames=600 --timeout=900 --log="%LOGDIR%\merge.log"
  if errorlevel 1 goto :error
) else (
  echo [workflow] step 2/4: no disassembly given; skipping exact level maps
)

echo [workflow] step 3/4: report
if not "%DISASM%"=="" (
  call :run "%ROM%" --reportonly --disasm="%DISASM%" --log="%LOGDIR%\report.log"
) else (
  call :run "%ROM%" --reportonly --log="%LOGDIR%\report.log"
)
if errorlevel 1 goto :error

echo [workflow] step 4/4: verify (HD pack active, side-by-side screenshots)
call :run "%ROM%" --verify --frames=600 --shots=1 --timeout=300 --log="%LOGDIR%\verify.log"
if errorlevel 1 goto :error

echo [workflow] DONE
echo [workflow] logs: %LOGDIR%\capture.log, maps.log, merge.log, report.log, verify.log
exit /b 0

:run
start /wait "" "%MESEN%" --hdstudio %*
exit /b %ERRORLEVEL%

:usage
echo Usage: hdstudio_workflow.cmd "game.nes" [--disasm=FOLDER] [--movie=FILE] [--frames=N]
exit /b 1

:error
echo [workflow] FAILED at the last step. See the workflow_logs folder.
exit /b 1
