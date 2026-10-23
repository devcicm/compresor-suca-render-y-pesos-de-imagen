# Compila SUCA liberando antes el .exe: Windows bloquea el binario mientras la
# app corre y MSBuild falla con MSB3027/MSB3021 tras 10 reintentos.
#
#   .\build.ps1                 # Debug (detiene la app si esta abierta)
#   .\build.ps1 -Configuration Release
#   .\build.ps1 -KeepRunning    # falla en vez de detener una instancia abierta
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$Project = "ConsoleApp suca compress.csproj",

    [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"
$processName = "ConsoleApp suca compress"
$running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)

if ($running.Count -gt 0) {
    if ($KeepRunning) {
        Write-Host "Hay $($running.Count) instancia(s) de '$processName' en ejecucion." -ForegroundColor Red
        Write-Host "Cierrelas o ejecute sin -KeepRunning para que el script las detenga." -ForegroundColor Red
        exit 1
    }

    Write-Host "Deteniendo $($running.Count) instancia(s) de '$processName' para liberar el .exe..." -ForegroundColor Yellow
    Write-Host "Las conversiones en vuelo se pierden; los resultados ya guardados en la galeria no." -ForegroundColor DarkYellow
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

& dotnet build $Project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Compilacion correcta ($Configuration)." -ForegroundColor Green
