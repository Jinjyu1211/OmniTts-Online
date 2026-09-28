#requires -Version 5.1
<#
    在线模块仓库的清单维护脚本。

    作用：
      1. 对 TreeHouseModules.json 中每个模块的 File 重新计算 SHA256 并写回清单；
      2. 可选 -BumpVersion，自动递增版本号的修订号，让已安装的模块触发更新；
      3. 输出校验结果。

    用法：
      pwsh -File Tools/Update-Sha256.ps1
      pwsh -File Tools/Update-Sha256.ps1 -BumpVersion

    注意：清单是 UTF-8 无 BOM，Windows PowerShell 5.1 默认按系统 ANSI 读取会把中文吃掉引号、
    导致 JSON 解析失败，因此这里一律显式指定 -Encoding UTF8，并用文本替换写回以保留原有格式。
#>
param(
    [switch]$BumpVersion
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'TreeHouseModules.json'

function Read-Utf8($path) {
    # -Encoding UTF8 是关键：5.1 默认按 ANSI 解码会损坏非 ASCII 字符。
    return [System.IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false)))
}

$manifest = Read-Utf8 $manifestPath | ConvertFrom-Json
$text = Read-Utf8 $manifestPath

foreach ($module in $manifest.Modules) {
    if ($module.File -match '^https?://') {
        Write-Warning "模块 $($module.InternalName) 使用完整下载地址，需手动下载该文件后计算摘要，已跳过。"
        continue
    }

    $file = Join-Path $root $module.File
    if (-not (Test-Path -LiteralPath $file)) {
        throw "找不到模块文件：$file"
    }

    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $oldSha = $module.Sha256
    $oldVersion = $module.Version
    $newVersion = $oldVersion

    if ($BumpVersion) {
        $parts = $oldVersion.Split('.')
        $parts[$parts.Length - 1] = [string]([int]$parts[$parts.Length - 1] + 1)
        $newVersion = $parts -join '.'
    }

    # 文本替换：只改这两个值，保留清单原有缩进与编码。
    $text = [regex]::Replace(
        $text,
        [regex]::Escape($oldSha),
        $actual,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    if ($newVersion -ne $oldVersion) {
        $text = $text.Replace(
            ('"Version": "{0}"' -f $oldVersion),
            ('"Version": "{0}"' -f $newVersion))
    }

    Write-Host ("{0,-24} {1}  {2}" -f $module.InternalName, $newVersion, $actual)
}

[System.IO.File]::WriteAllText($manifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已更新 $manifestPath"
