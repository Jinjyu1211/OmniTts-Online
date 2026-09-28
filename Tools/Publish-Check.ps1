#requires -Version 5.1
<#
    在线模块发布前自检：把仓库规范里会导致「下载失败 / 加载失败 / 不触发更新」的硬性约束逐条核对一遍。

    检查项：
      1. 清单字段完整性：SchemaVersion、InternalName、Version、MinimumOmniVersion、File、Sha256
      2. InternalName 命名规则（ASCII 字母数字下划线，首字符非数字）与「类型名 == InternalName」
      3. File 扩展名小写 .cs/.dll、文件存在、单文件 < 32 MiB
      4. Sha256 与文件实际字节一致（大小写不敏感）
      5. Version 为 2–4 段非负整数，无 v 前缀、无预发布后缀
      6. 模块类：继承 ModuleBase、公共无参构造、每个文件仅一个可实例化的 ModuleBase
      7. Info 位于类型首个成员位置
      8. 文件编码：无 UTF-8 BOM；换行符统一（LF 优先）
      9. 危险信号：用到 System.Linq（Omni 的 Roslyn 环境没有隐式 using 与 Linq）

    用法：
      pwsh -File Tools/Publish-Check.ps1
    退出码 0 表示全部通过，1 表示存在必须修复的问题。
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'TreeHouseModules.json'

$errors = @()
$warnings = @()

function Add-Error($msg) { $script:errors += $msg }
function Add-Warning($msg) { $script:warnings += $msg }

if (-not (Test-Path -LiteralPath $manifestPath)) {
    Write-Host "找不到清单：$manifestPath" -ForegroundColor Red
    exit 1
}

# 显式按 UTF-8 读取：Windows PowerShell 5.1 默认按系统 ANSI 解码，会损坏清单里的中文。
$manifest = [System.IO.File]::ReadAllText($manifestPath, (New-Object System.Text.UTF8Encoding($false))) | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1) {
    Add-Error "SchemaVersion 应为 1，当前为 $($manifest.SchemaVersion)"
}

if (-not $manifest.Modules -or $manifest.Modules.Count -eq 0) {
    Add-Error "清单中没有模块"
}

foreach ($m in $manifest.Modules) {
    $name = $m.InternalName
    Write-Host "=== $name ===" -ForegroundColor Cyan

    foreach ($field in @('InternalName', 'Name', 'Author', 'Description', 'Version', 'MinimumOmniVersion', 'File', 'Sha256')) {
        $value = $m.PSObject.Properties[$field].Value
        if ([string]::IsNullOrWhiteSpace([string]$value)) {
            Add-Error "$name：缺少字段 $field"
        }
    }

    # InternalName 命名规则
    if ($name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        Add-Error "$name：InternalName 只能使用 ASCII 字母、数字和下划线，且首字符不能为数字"
    }

    # 版本格式：2–4 段非负整数
    if ($m.Version -notmatch '^\d+(\.\d+){1,3}$') {
        Add-Error "$name：Version「$($m.Version)」不是 2–4 段非负整数"
    }

    # File：可以是相对路径，也可以是完整下载地址（jsDelivr 等 CDN 直链）。
    # 完整地址形如 https://cdn.jsdelivr.net/gh/<owner>/<repo>@<ref>/Modules/<ver>/X.cs，
    # 取 "@<ref>/" 之后的部分即可映射回仓库内的相对路径，从而照常做本地校验。
    $relativeFile = $m.File
    if ($relativeFile -match '^https?://') {
        Add-Warning "$name：File 使用完整下载地址（校验的是其映射回仓库的本地副本）"
        if ($relativeFile -match '@[^/]+/(.+)$') {
            $relativeFile = $Matches[1]
            Write-Host ("  映射本地文件  : {0}" -f $relativeFile)
        }
        else {
            Add-Error "$name：无法从下载地址推断仓库内相对路径，跳过本地校验"
            continue
        }
    }

    $ext = [System.IO.Path]::GetExtension($relativeFile)
    if ($ext -notin @('.cs', '.dll')) {
        Add-Error "$name：File 扩展名必须是小写 .cs 或 .dll，当前为 $ext"
    }

    $file = Join-Path $root $relativeFile
    if (-not (Test-Path -LiteralPath $file)) {
        Add-Error "$name：找不到文件 $file"
        continue
    }

    $item = Get-Item -LiteralPath $file
    if ($item.Length -gt 32MB) {
        Add-Error "$name：文件超过 32 MiB（当前 $([math]::Round($item.Length / 1MB, 2)) MiB）"
    }

    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $shaOk = [string]::Equals($actual, $m.Sha256, [System.StringComparison]::OrdinalIgnoreCase)
    Write-Host ("  Sha256        : {0} ({1})" -f $actual, $(if ($shaOk) { '一致' } else { '不一致' }))
    if (-not $shaOk) {
        Add-Error "$name：Sha256 与文件不一致（清单 $($m.Sha256) / 实际 $actual），改完代码请重算"
    }

    if ($ext -ne '.cs') {
        continue
    }

    # 源码层面的规范检查
    $bytes = [System.IO.File]::ReadAllBytes($file)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Add-Warning "$name：文件带 UTF-8 BOM，远端字节与本地不一致会导致校验失败"
    }

    $crlf = ([regex]::Matches($text, "`r`n")).Count
    $lf = ([regex]::Matches($text, "(?<!`r)`n")).Count
    if ($crlf -gt 0 -and $lf -gt 0) {
        Add-Warning "$name：换行符混用（CRLF $crlf / LF $lf），提交时应统一（本仓库 .gitattributes 固定 LF）"
    }
    elseif ($crlf -gt 0) {
        Add-Warning "$name：文件为 CRLF，Git 转换可能导致远端字节变化"
    }

    # 类型名 == InternalName，且继承 ModuleBase
    $classMatch = [regex]::Match($text, "class\s+$name\s*:\s*ModuleBase\b")
    if (-not $classMatch.Success) {
        Add-Error "$name：找不到 `class $name : ModuleBase`，类型名必须与 InternalName 完全一致"
    }
    else {
        $bodyStart = $text.IndexOf('{', $classMatch.Index)

        # 公共无参构造：没有声明构造，或存在 public 类名() 且无参
        $ctor = [regex]::Match($text.Substring($bodyStart), "(public|private|protected|internal)\s+$name\s*\(\s*\)")
        if ($ctor.Success -and $ctor.Groups[1].Value -ne 'public') {
            Add-Error "$name：构造函数不是公共无参构造（当前 $($ctor.Groups[1].Value)）"
        }

        # Info 必须是首个成员：类体中 Info 声明之前只能有注释与空白。
        # 匹配整个声明（含 public override 前缀），否则会把修饰符误判成前导成员。
        $infoMatch = [regex]::Match($text.Substring($bodyStart), 'public\s+override\s+ModuleInfo\s+Info')
        if (-not $infoMatch.Success) {
            Add-Error "$name：找不到 ModuleInfo Info 成员"
        }
        else {
            $infoIndex = $bodyStart + $infoMatch.Index
            $before = $text.Substring($bodyStart + 1, $infoIndex - $bodyStart - 1)
            $before = [regex]::Replace($before, '//[^\r\n]*', '')
            $before = [regex]::Replace($before, '(?s)/\*.*?\*/', '')
            if ($before.Trim().Length -gt 0) {
                $preview = $before.Trim()
                if ($preview.Length -gt 60) { $preview = $preview.Substring(0, 60) }
                Add-Error "$name：Info 不是首个成员，其前有：$preview"
            }
        }
    }

    # 单个文件仅一个可实例化的 ModuleBase
    $moduleCount = ([regex]::Matches($text, 'class\s+\w+\s*:\s*ModuleBase\b')).Count
    if ($moduleCount -gt 1) {
        Add-Error "$name：单个文件包含 $moduleCount 个 ModuleBase 实现"
    }

    # Omni 的 Roslyn 环境没有 System.Linq
    if ($text -match 'using\s+System\.Linq' -or $text -match '\.(Select|Where|FirstOrDefault|Any|Cast|OfType)\s*[\(<]') {
        Add-Warning "$name：疑似使用 LINQ（Omni 编译环境无隐式 using 与 System.Linq，会报 CS1061）"
    }

    Write-Host ("  Version       : {0}   最低 Omni：{1}" -f $m.Version, $m.MinimumOmniVersion)
}

Write-Host ''
if ($warnings.Count -gt 0) {
    Write-Host '警告：' -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}

if ($errors.Count -gt 0) {
    Write-Host '必须修复：' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host '自检通过：清单与模块文件符合在线模块规范。' -ForegroundColor Green
Write-Host '提醒：本地一致不等于远端一致，推送后请下载 File 指向的 raw 文件再算一次 SHA256。' -ForegroundColor Green
exit 0
