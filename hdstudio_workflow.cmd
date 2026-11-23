@echo off
rem HD Studio complete workflow (validated for any NES ROM).
rem
rem Usage:
rem   hdstudio_workflow.cmd "game.nes" [--disasm=FOLDER] [--movie=FILE] [--frames=N] [--playerforms]
rem
rem Examples:
rem   hdstudio_workflow.cmd "D:\ROMs\game.nes" --frames=1800
rem   hdstudio_workflow.cmd "D:\ROMs\Super Mario Bros. 3 (USA).nes" --disasm="D:\MesenHighWork\disasm\smb3-master" --playerforms
rem   hdstudio_workflow.cmd "D:\ROMs\game.nes" --movie="D:\TAS\run.fm2" --frames=600
rem
rem Steps:
rem   1. capture with the automatic explorer (or the movie) and --generate
rem   2. if a disassembly is given: --maps (exact level maps/backgrounds + sprite sweep)
rem   2b. if --playerforms is given: capture all SMB3 player forms and poses
rem   3. merge the exact tiles, player forms and wildcard defaults
rem   4. report
rem   5. verify (HD pack active?) and side-by-side screenshots
rem
rem Every step runs in its own Mesen process, so a stuck game cannot poison the next step.

setlocal
set "HERE=%~dp0"
set "ROM="
set "DISASM="
set "MOVIE="
set "FRAMES=1800"
set "PLAYERFORMS=0"

:parse
if "%~1"=="" goto :parsed
set "ARG=%~1"
if /i "%ARG%"=="--disasm" goto :arg_disasm
if /i "%ARG%"=="--movie" goto :arg_movie
if /i "%ARG%"=="--frames" goto :arg_frames
if /i "%ARG%"=="--playerforms" set "PLAYERFORMS=1"
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
if "%PLAYERFORMS%"=="1" echo [workflow] player forms: yes

echo [workflow] step 1/5: capture + all CHR tiles + wildcard fallback
if not "%MOVIE%"=="" (
  call :run "%ROM%" "%MOVIE%" --generate --timeout=1800 --log="%LOGDIR%\capture.log"
) else (
  call :run "%ROM%" --generate --frames=%FRAMES% --timeout=1800 --log="%LOGDIR%\capture.log"
)
if errorlevel 1 goto :error

if not "%DISASM%"=="" (
  echo [workflow] step 2/5: exact level maps, backgrounds and sprite palette sweep
  call :run "%ROM%" --maps --disasm="%DISASM%" --timeout=600 --log="%LOGDIR%\maps.log"
  if errorlevel 1 goto :error
) else (
  echo [workflow] step 2/5: no disassembly given; skipping exact level maps
)

if "%PLAYERFORMS%"=="1" (
  echo [workflow] step 2b/5: player forms and named poses
  call :run "%ROM%" --playerforms --timeout=600 --log="%LOGDIR%\playerforms.log"
  if errorlevel 1 goto :error
)

if not "%DISASM%"=="" goto :do_merge
if "%PLAYERFORMS%"=="1" goto :do_merge
echo [workflow] step 3/5: no exact data to merge
goto :after_merge

:do_merge
echo [workflow] step 3/5: merge exact tiles, player forms and wildcard defaults
call :run "%ROM%" --generate --frames=600 --timeout=900 --log="%LOGDIR%\merge.log"
if errorlevel 1 goto :error

:after_merge
echo [workflow] step 4/5: report
if not "%DISASM%"=="" (
  call :run "%ROM%" --reportonly --disasm="%DISASM%" --log="%LOGDIR%\report.log"
) else (
  call :run "%ROM%" --reportonly --log="%LOGDIR%\report.log"
)
if errorlevel 1 goto :error

echo [workflow] step 5/5: verify (HD pack active, side-by-side screenshots)
call :run "%ROM%" --verify --frames=600 --shots=1 --timeout=300 --log="%LOGDIR%\verify.log"
if errorlevel 1 goto :error

echo [workflow] DONE
echo [workflow] logs: %LOGDIR%
exit /b 0

:run
start /wait "" "%MESEN%" --hdstudio %*
exit /b %ERRORLEVEL%

:usage
echo Usage: hdstudio_workflow.cmd "game.nes" [--disasm=FOLDER] [--movie=FILE] [--frames=N] [--playerforms]
exit /b 1

:error
echo [workflow] FAILED at the last step. See the workflow_logs folder.
exit /b 1
