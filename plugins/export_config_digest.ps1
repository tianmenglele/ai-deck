param(
    [string]$ProjectRoot = "",
    [string]$OutputFile = ""
)
$ErrorActionPreference = 'SilentlyContinue'
if (-not $ProjectRoot -or -not (Test-Path -LiteralPath $ProjectRoot)) { exit 0 }
if (-not $OutputFile) {
    $OutputFile = Join-Path $ProjectRoot "work\collab\SCHEMA_DIGEST.md"
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# 项目数据表与配置结构速查（自动生成）")
$lines.Add("")
$lines.Add("> 生成时间：" + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
$lines.Add("")

$dataDir = Join-Path $ProjectRoot "data"
if (Test-Path -LiteralPath $dataDir) {
    $lines.Add("## data/ 目录文件概览")
    Get-ChildItem -LiteralPath $dataDir -File | Select-Object -First 40 | ForEach-Object {
        $lines.Add(("- `{0}` ({1} bytes)" -f $_.Name, $_.Length))
    }
} else {
    $lines.Add("- 当前项目根目录下未发现 `data/` 子目录，可按需在 plugins/ 中自定义字段提取脚本。")
}

$parent = Split-Path -Parent $OutputFile
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
[System.IO.File]::WriteAllLines($OutputFile, $lines, (New-Object System.Text.UTF8Encoding($false)))
