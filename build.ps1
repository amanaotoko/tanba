# Сборка релиза Tanba.
#
#   .\build.ps1 0.1.1
#
# Делает установщик и пакет обновления в build\releases.
# Прошлые релизы из этой папки не удаляй: по ним считается разностное
# обновление, и без них каждое обновление будет качать все 60 мегабайт.
#
# Ключ доступа не нужен: репозиторий публичный, релизы читаются без него.
# Если его когда-нибудь закроют, положи ключ в переменную окружения
# TANBA_UPDATE_TOKEN, и сборка вошьёт его в exe.

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
# Значок нужен и здесь: в exe он вшит сборкой, но установщику и ярлыкам,
# которые Velopack создаёт сам, взять его больше неоткуда.
vpk pack -u Tanba -v $Version -p $publish -e Tanba.exe -o $releases `
    --packTitle 'Tanba' --packAuthors 'Amanbek Shaimardan' `
    --icon (Join-Path $root 'assets\tanba.ico')
if ($LASTEXITCODE -ne 0) { throw 'Упаковка не удалась' }

# Проверяем, что пакет этой версии действительно появился.
#
# Три релиза подряд уехали с чужими файлами именно так: упаковку обрывали
# на середине, в папке оставались пакеты прошлой версии, и выкладка молча
# отправляла их. Обрыв случался от `Select-Object -First N` на живом
# конвейере: он останавливает выполняемую команду, набрав N строк вывода.
# Никогда не фильтруйте вывод этой сборки конвейером с -First; складывайте
# его целиком через Out-String, а потом смотрите.
$full = Join-Path $releases "Tanba-$Version-full.nupkg"
if (-not (Test-Path $full)) {
    throw "Пакет $Version не собрался: нет $full. Выкладывать нечего."
}

Write-Host ''
Write-Host "Готово. Установщик: $releases\Tanba-win-Setup.exe" -ForegroundColor Green
Get-ChildItem $releases -Filter "*$Version*" |
    Select-Object Name, @{ n = 'МБ'; e = { '{0:N1}' -f ($_.Length / 1MB) } } |
    Format-Table -AutoSize
