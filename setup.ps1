#Requires -RunAsAdministrator
<#
.SYNOPSIS
  SRM の初期セットアップ（管理者権限が必要）
.DESCRIPTION
  - SRMデータディレクトリの作成
  - ポリシーファイルへの署名（SHA256サイドカー生成）
  - WFPフィルタープロバイダーの永続登録（将来拡張用）
#>

$ErrorActionPreference = 'Stop'

$SrmData = "$env:ProgramData\SRM"

Write-Host "SRM セットアップを開始します..." -ForegroundColor Cyan

# データディレクトリ作成
$dirs = @("$SrmData\logs", "$SrmData\run")
foreach ($d in $dirs) {
    if (-not (Test-Path $d)) {
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        Write-Host "  作成: $d"
    }
}

# ポリシーファイルへの署名
$policiesDir = "$PSScriptRoot\policies"
if (Test-Path $policiesDir) {
    Get-ChildItem "$policiesDir\*.yaml" | ForEach-Object {
        $sidecar = "$($_.FullName).sha256"
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        Set-Content -Path $sidecar -Value $hash -NoNewline
        Write-Host "  署名: $($_.Name) -> $($_.BaseName).yaml.sha256"
    }
}

Write-Host ""
Write-Host "セットアップ完了。" -ForegroundColor Green
Write-Host "使用方法（srm.exe は bin\ 配下）:"
Write-Host "  bin\srm run claude-code    — Claude Codeを隔離実行"
Write-Host "  bin\srm list               — 実行中アプリ一覧"
Write-Host "  bin\srm stop claude-code   — 停止"
Write-Host "  bin\srm logs claude-code   — ログ表示"
Write-Host "  bin\srm validate claude-code -- ポリシー検証"
