# Сборка релиза Tanba.
#
#   .\build.ps1 0.1.1
#
# Делает установщик и пакет обновления в build\releases.
# Прошлые релизы из этой папки не удаляй: по ним считается разностное
# обновление, и без них каждое обновление будет качать все 60 мегабайт.
#
# Ключ доступа к приватному репозиторию берётся из переменной окружения
# TANBA_UPDATE_TOKEN и вшивается в exe. В репозиторий он не попадает никогда.
# Поставить его один раз для своей учётной записи:
#
#   [Environment]::SetEnvironmentVariable('TANBA_UPDATE_TOKEN', 'ghp_...', 'User')
#
# После этого перезапусти терминал.

param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$publish = Join-Path $root 'build\publish'
$releases = Join-Path $root 'build\releases'

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Версия должна быть вида 1.2.3, а не '$Version'"
}

if (-not $env:TANBA_UPDATE_TOKEN) {
    Write-Warning 'TANBA_UPDATE_TOKEN не задан. Программа соберётся, но обновляться из приватного репозитория не сможет.'
}

# Запущенная копия держит свои файлы и ломает сборку.
Get-Process Tanba -ErrorAction SilentlyContinue | Stop-Process -Force

# Публикуем со всем .NET внутри: на чужой машине ничего доустанавливать не надо.
Write-Host "Собираю $Version" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $root 'src\Tanba\Tanba.csproj') `
    -c Release -r win-x64 --self-contained true `
    -o $publish -v q --nologo /p:Version=$Version
if ($LASTEXITCODE -ne 0) { throw 'Сборка не удалась' }

Write-Host 'Упаковываю' -ForegroundColor Cyan
vpk pack -u Tanba -v $Version -p $publish -e Tanba.exe -o $releases `
    --packTitle 'Tanba' --packAuthors 'Amanbek Shaimardan'
if ($LASTEXITCODE -ne 0) { throw 'Упаковка не удалась' }

Write-Host ''
Write-Host "Готово. Установщик: $releases\Tanba-win-Setup.exe" -ForegroundColor Green
Get-ChildItem $releases -Filter "*$Version*" |
    Select-Object Name, @{ n = 'МБ'; e = { '{0:N1}' -f ($_.Length / 1MB) } } |
    Format-Table -AutoSize
