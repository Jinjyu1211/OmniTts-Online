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
    $oldVersion = $module.Version
    $newVersion = $oldVersion

    # 完整下载地址（jsDelivr 直链）拆成「CDN 前缀 + 仓库内相对路径」，
    # 形如 https://cdn.jsdelivr.net/gh/<owner>/<repo>@<ref>/Modules/<ver>/X.cs
    $relative = $module.File
    $prefix = ''
    if ($relative -match '^(?<prefix>https?://.*?@[^/]+/)(?<rest>.+)$') {
        $prefix = $Matches['prefix']
        $relative = $Matches['rest']
    }

    $currentFile = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $currentFile)) {
        throw "找不到模块文件：$currentFile"
    }

    # 开发源文件优先：清单里的 File 指向上一版快照，直接用它会导致升版本时
    # 复制旧内容、摘要永远不变。只要 Modules/<name>.cs 存在，就以它为准。
    $devFile = Join-Path $root ("Modules\{0}" -f [System.IO.Path]::GetFileName($relative))
    if (Test-Path -LiteralPath $devFile) {
        $currentFile = $devFile
    }

    if ($BumpVersion) {
        $parts = $oldVersion.Split('.')
        $parts[$parts.Length - 1] = [string]([int]$parts[$parts.Length - 1] + 1)
        $newVersion = $parts -join '.'
    }

    $targetRelative = $relative
    if ($newVersion -ne $oldVersion) {
        # 版本号目录化：Modules/<version>/<file>.cs。
        # 每个版本的下载地址都不同，CDN / 代理不可能返回上一版的缓存内容，
        # 从根上避免「清单已更新但下载到旧文件导致摘要校验失败」。
        $leaf = [System.IO.Path]::GetFileName($relative)
        $targetRelative = ('Modules/{0}/{1}' -f $newVersion, $leaf)
        $targetFile = Join-Path $root $targetRelative
        $targetDir = Split-Path -Parent $targetFile
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir | Out-Null
        }
        Copy-Item -LiteralPath $currentFile -Destination $targetFile -Force
        Write-Host ("已生成版本快照：{0}" -f $targetRelative)

        # 文本替换：只改这两处，保留清单原有缩进与编码。
        $text = $text.Replace(('"File": "{0}"' -f $module.File), ('"File": "{0}"' -f ($prefix + $targetRelative)))
        $text = $text.Replace(('"Version": "{0}"' -f $oldVersion), ('"Version": "{0}"' -f $newVersion))
    }

    $actual = (Get-FileHash -LiteralPath (Join-Path $root $targetRelative) -Algorithm SHA256).Hash
    $text = [regex]::Replace(
        $text,
        [regex]::Escape($module.Sha256),
        $actual,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)

    Write-Host ("{0,-24} {1}  {2}" -f $module.InternalName, $newVersion, $actual)
}

[System.IO.File]::WriteAllText($manifestPath, $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已更新 $manifestPath"
