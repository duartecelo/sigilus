@echo off
:: ============================================================================
::  Sigilus 2.0 — bootstrap para Windows x64
::  Uso: dê duplo clique neste arquivo após clonar/baixar o repositório.
::  Resultado: pasta `publish\Sigilus\` com Sigilus.exe pronto para usar.
:: ============================================================================
setlocal EnableDelayedExpansion

cd /d "%~dp0"
title Sigilus - Preparando o aplicativo

echo.
echo ================================================================
echo                Sigilus 2.0 - Preparando o aplicativo
echo ================================================================
echo.
echo Este processo vai:
echo   1) Verificar se o .NET 8 SDK esta instalado (instala se faltar)
echo   2) Restaurar pacotes e compilar o Sigilus
echo   3) Baixar o componente de OCR (Tesseract portugues, ~3 MB)
echo.
echo Isso leva 5-15 minutos na primeira vez. Nas proximas e quase instantaneo.
echo NAO feche esta janela.
echo.
echo ----------------------------------------------------------------

:: ----- 1) Verifica .NET 8 SDK -----
echo.
echo [1/4] Verificando o .NET 8 SDK...
where dotnet >nul 2>&1
if errorlevel 1 goto :install_dotnet

for /f "tokens=1 delims=." %%a in ('dotnet --version 2^>nul') do set DOTNET_MAJOR=%%a
if not defined DOTNET_MAJOR goto :install_dotnet
if %DOTNET_MAJOR% LSS 8 goto :install_dotnet
echo      .NET %DOTNET_MAJOR%.x ja instalado, OK.
goto :build

:install_dotnet
echo      .NET 8 SDK nao encontrado. Baixando instalador...
set "DOTNET_INSTALLER=%TEMP%\dotnet-sdk-8-x64.exe"
:: Link oficial estavel (sempre aponta para o ultimo .NET 8 LTS)
set "DOTNET_URL=https://aka.ms/dotnet/8.0/dotnet-sdk-win-x64.exe"
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ProgressPreference='SilentlyContinue'; try { Invoke-WebRequest -Uri '%DOTNET_URL%' -OutFile '%DOTNET_INSTALLER%' -UseBasicParsing } catch { exit 1 }"
if errorlevel 1 (
    echo      ERRO: falha ao baixar o .NET 8 SDK. Verifique sua conexao.
    pause
    exit /b 1
)
echo      Instalando o .NET 8 SDK (pode aparecer um pop-up de Controle de Conta)...
"%DOTNET_INSTALLER%" /install /quiet /norestart
if errorlevel 1 (
    echo      ERRO: instalacao do .NET 8 SDK falhou.
    pause
    exit /b 1
)
:: Recarrega PATH (instalacao MSI adiciona dotnet.exe ao Program Files)
set "PATH=%PATH%;%ProgramFiles%\dotnet"
where dotnet >nul 2>&1
if errorlevel 1 (
    echo      AVISO: o .NET foi instalado, mas o sistema nao detectou.
    echo      Por favor reinicie o computador e execute novamente este arquivo.
    pause
    exit /b 1
)
echo      .NET 8 SDK instalado com sucesso.

:build
:: ----- 2) Restore + Publish -----
echo.
echo [2/4] Restaurando pacotes NuGet (pode demorar alguns minutos)...
dotnet restore Sigilus.sln --nologo --verbosity minimal
if errorlevel 1 (
    echo      ERRO: falha no dotnet restore.
    pause
    exit /b 1
)

echo.
echo [3/4] Compilando o Sigilus em modo Release...
if exist publish\Sigilus rmdir /s /q publish\Sigilus
dotnet publish src\Sigilus.Ui.Wpf\Sigilus.Ui.Wpf.csproj ^
    -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -o publish\Sigilus --nologo --verbosity minimal
if errorlevel 1 (
    echo      ERRO: falha no dotnet publish.
    pause
    exit /b 1
)

:: Remove runtimes extras de outras plataformas e .pdb
del /q publish\Sigilus\*.pdb >nul 2>&1
for %%D in (linux-arm64 linux-musl-x64 linux-x64 osx-arm64 osx-x64 win-arm64) do (
    if exist publish\Sigilus\runtimes\%%D rmdir /s /q publish\Sigilus\runtimes\%%D
)

:: ----- 3) Baixa tessdata se nao existir -----
echo.
echo [4/4] Verificando componente de OCR (tessdata)...
if not exist publish\Sigilus\tessdata mkdir publish\Sigilus\tessdata
if exist publish\Sigilus\tessdata\por.traineddata (
    echo      tessdata\por.traineddata ja existe, OK.
) else (
    echo      Baixando por.traineddata do GitHub (tesseract-ocr/tessdata_fast)...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
      "$ProgressPreference='SilentlyContinue'; try { Invoke-WebRequest -Uri 'https://github.com/tesseract-ocr/tessdata_fast/raw/main/por.traineddata' -OutFile 'publish\Sigilus\tessdata\por.traineddata' -UseBasicParsing } catch { exit 1 }"
    if errorlevel 1 (
        echo      AVISO: nao foi possivel baixar tessdata agora.
        echo      Voce pode baixar pelo proprio Sigilus depois ^(menu Configuracoes^).
    ) else (
        echo      tessdata baixado com sucesso.
    )
)

:: Copia models versionados (labels, vocab, tokenizer_config) se existirem.
if not exist publish\Sigilus\models mkdir publish\Sigilus\models
if exist models\labels.json copy /y models\labels.json publish\Sigilus\models\ >nul
if exist models\vocab.txt copy /y models\vocab.txt publish\Sigilus\models\ >nul
if exist models\tokenizer_config.json copy /y models\tokenizer_config.json publish\Sigilus\models\ >nul

:: Copia LEIA-ME para a pasta publicada
if exist publish\Sigilus\LEIA-ME.txt del /q publish\Sigilus\LEIA-ME.txt >nul 2>&1
if exist LEIA-ME.txt copy /y LEIA-ME.txt publish\Sigilus\ >nul

echo.
echo ================================================================
echo                       Tudo pronto!
echo ================================================================
echo.
echo O Sigilus esta em: %CD%\publish\Sigilus\Sigilus.exe
echo.
echo Para usar:
echo   1) Abra a pasta publish\Sigilus
echo   2) De duplo clique em Sigilus.exe
echo   3) Em Configuracoes, clique "Baixar / atualizar" para obter:
echo        - IA rapida (NER, ~104 MB)
echo        - IA inteligente (LLM, 2-5 GB)
echo.
echo Pressione qualquer tecla para abrir a pasta...
pause >nul
start "" "%CD%\publish\Sigilus"
endlocal
exit /b 0
