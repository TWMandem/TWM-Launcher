[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$Repository,
    [Parameter(Mandatory=$true)][string]$PackagePath,
    [Parameter(Mandatory=$true)][string]$ReleaseNotesPath
)
$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw 'GitHub CLI (gh) не найден. Установите его и выполните gh auth login.'
}
if (-not (Test-Path $PackagePath -PathType Leaf)) { throw "Пакет не найден: $PackagePath" }
if (-not (Test-Path $ReleaseNotesPath -PathType Leaf)) { throw "Файл описания релиза не найден: $ReleaseNotesPath" }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') { throw 'Версия должна иметь формат semver, например 1.2.3.' }

$hash = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$tag = "v$Version"
Write-Host "Репозиторий: $Repository"
Write-Host "Тег: $tag"
Write-Host "SHA-256: $hash"
Write-Host 'Проверяю авторизацию GitHub CLI...'
& gh auth status
if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI не авторизован. Выполните gh auth login.' }

Write-Host 'Создаю GitHub Release и загружаю пакет...'
& gh release create $tag $PackagePath --repo $Repository --title "TWM Launcher $Version" --notes-file $ReleaseNotesPath
if ($LASTEXITCODE -ne 0) { throw 'Не удалось создать релиз. Проверьте репозиторий, права доступа и существование тега.' }
Write-Host 'Релиз опубликован. Сохраните SHA-256 в update-manifest.json:'
Write-Host $hash
