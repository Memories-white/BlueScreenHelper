[CmdletBinding()]
param(
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'BlueScreenHelper\BlueScreenHelper.csproj'
$manifestPath = Join-Path $repositoryRoot 'BlueScreenHelper\app.manifest'
$publishDirectory = Join-Path $repositoryRoot 'publish\win-x64'
$installerScript = Join-Path $repositoryRoot 'installer\installer.iss'

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$appVersion = [string]$project.Project.PropertyGroup.Version
if ($appVersion -notmatch '^(?<base>\d+\.\d+\.\d+)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "项目版本 '$appVersion' 不是有效的 SemVer 版本。"
}

$numericVersion = "$($Matches.base).0"
$manifest = Get-Content -LiteralPath $manifestPath -Raw
if ($manifest -notmatch '<assemblyIdentity\s+version="(?<version>[^"]+)"') {
    throw '无法从 app.manifest 读取 assemblyIdentity 版本。'
}
if ($Matches.version -ne $numericVersion) {
    throw "版本不一致：项目为 $appVersion，app.manifest 为 $($Matches.version)，预期 $numericVersion。"
}

if (-not $SkipRestore) {
    dotnet restore $projectPath -r win-x64 -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore 失败，退出代码：$LASTEXITCODE。"
    }
}

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -o $publishDirectory `
    -p:Platform=x64 `
    -p:Version=$appVersion
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出代码：$LASTEXITCODE。"
}

$application = Join-Path $publishDirectory 'BlueScreenHelper.exe'
if (-not (Test-Path -LiteralPath $application -PathType Leaf)) {
    throw "发布目录中缺少主程序：$application"
}

foreach ($runtimeFile in @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll')) {
    $source = Join-Path $env:WINDIR "System32\$runtimeFile"
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "系统中缺少安装包所需的 VC++ 运行库：$source"
    }
    Copy-Item -LiteralPath $source -Destination $publishDirectory -Force
}

$resolvedPublishDirectory = (Resolve-Path -LiteralPath $publishDirectory).Path
$allowedCultures = @('zh-CN', 'en-US')
foreach ($directory in Get-ChildItem -LiteralPath $resolvedPublishDirectory -Directory) {
    try {
        $culture = [System.Globalization.CultureInfo]::GetCultureInfo($directory.Name)
    }
    catch [System.Globalization.CultureNotFoundException] {
        continue
    }

    if ($allowedCultures -contains $culture.Name) {
        continue
    }

    $expectedPrefix = $resolvedPublishDirectory + [System.IO.Path]::DirectorySeparatorChar
    if (-not $directory.FullName.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝删除发布目录以外的语言资源：$($directory.FullName)"
    }
    Remove-Item -LiteralPath $directory.FullName -Recurse -Force
}

$logDirectory = Join-Path $resolvedPublishDirectory 'log'
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logHelp = @(
    '此文件夹用于保存程序运行日志。',
    '遇到问题时，请将本文件夹（log）内的 .log 文件打包发给开发者，以便快速定位问题。',
    '若日志未出现在此处，请查看 %LOCALAPPDATA%\BlueScreenHelper\logs。'
) -join [Environment]::NewLine
[System.IO.File]::WriteAllText(
    (Join-Path $logDirectory '日志说明.txt'),
    $logHelp,
    [System.Text.UTF8Encoding]::new($false))

$iscc = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
if (-not $iscc) {
    $searchRoots = @(
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86'),
        (Join-Path $env:LOCALAPPDATA 'Programs')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }

    $iscc = foreach ($root in $searchRoots) {
        Get-ChildItem -LiteralPath $root -Filter 'ISCC.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'Inno Setup' } |
            Select-Object -First 1 -ExpandProperty FullName
    }
    $iscc = $iscc | Select-Object -First 1
}
if (-not $iscc) {
    throw '未找到 ISCC.exe。请先安装 Inno Setup 6，或将其目录加入 PATH。'
}

& $iscc "/DMyAppVersion=$appVersion" "/DMyAppNumericVersion=$numericVersion" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup 编译失败，退出代码：$LASTEXITCODE。"
}

$installer = Join-Path $repositoryRoot "dist\BlueScreenHelper_Setup_${appVersion}_win-x64.exe"
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "未生成预期的安装包：$installer"
}

$file = Get-Item -LiteralPath $installer
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
Write-Host ''
Write-Host '安装包构建完成：' -ForegroundColor Green
Write-Host "  路径：$($file.FullName)"
Write-Host "  大小：$([Math]::Round($file.Length / 1MB, 2)) MB"
Write-Host "  SHA256：$($hash.Hash)"
