# Gera o instalador do MyPIN:  dist\MyPIN-Setup-<versão>.exe
# Uso (na pasta do projeto):  .\tools\gerar_instalador.ps1
# Precisa do Inno Setup 6 instalado (winget install JRSoftware.InnoSetup).
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# versão: a mesma do projeto (<Version> no MyPIN.csproj)
$version = ([xml](Get-Content "src\MyPIN\MyPIN.csproj")).Project.PropertyGroup.Version | ? { $_ } | Select -First 1
Write-Host "MyPIN $version"

# 1) app autônomo (leva o .NET junto; não precisa instalar nada no computador do colega).
#    As DLLs nativas ficam ao lado do .exe (nada é extraído para a pasta temporária, que
#    muitas empresas bloqueiam).
if (Test-Path "dist\app") { Remove-Item "dist\app" -Recurse -Force }
dotnet publish "src\MyPIN\MyPIN.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "dist\app"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou" }

# 2) instalador
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | ? { Test-Path $_ } | Select -First 1
if (-not $iscc) { throw "Inno Setup 6 não encontrado. Instale com:  winget install JRSoftware.InnoSetup" }
& $iscc "/DAppVersion=$version" "installer\MyPIN.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup falhou" }

Write-Host ""
Write-Host "Pronto: $root\dist\MyPIN-Setup-$version.exe" -ForegroundColor Green
