#Requires -Version 5.1
<#
.SYNOPSIS
    打包 Windows 独立发行版：产出单个自包含的 Idle-Sword.exe。

.DESCRIPTION
    刻意不引入任何第三方打包工具。Godot 官方的 `--export-release` 已经覆盖了导出本身，
    外面的封装（Docker 镜像、各种 CI action、安装器）都只是它的薄壳，接进来只会多一层
    引擎版本对齐的负担。本脚本负责的是官方命令没有的那部分：

      1. 前置检查（引擎、版本、导出模板、C# 解决方案、dotnet、预设）；
      2. 导出模板安装（-InstallTemplates，约 1.1 GB，一次性）；
      3. 导出前的质量闸（编译 + 纯逻辑自检）；
      4. 产物断言（证明 embed_pck 真的生效，而不是"看着导出成功"）；
      5. 独立运行验证——把产出的 exe 拷到仓库外单独跑，断言它自己启动了、断言全过、
         中文真的渲染出来了。

    第 5 步是整件事的重点：Godot 的 `--export-release` 在导出失败时**也会返回 0**
    （godot#85062），只看退出码会把一堆坏包放过去。

.PARAMETER GodotExe
    Godot 编辑器可执行文件（必须是 .NET/mono 版）。默认取环境变量 GODOT_EDITOR。

.PARAMETER InstallTemplates
    下载并安装导出模板。模板缺失时脚本会提示加这个开关。

.PARAMETER WithDebugTemplate
    连调试模板一起装（仅 -InstallTemplates 生效）。发布包不需要。

.PARAMETER SkipBuild / -SkipChecks / -SkipImport / -SkipCapture
    跳过对应阶段，用于快速迭代。

.PARAMETER KeepTemp / -KeepTpz
    保留临时运行目录 / 保留下载的模板包，便于排查。

.EXAMPLE
    powershell -File tools\PackWindows.ps1 -InstallTemplates
.EXAMPLE
    powershell -File tools\PackWindows.ps1 -SkipBuild -SkipChecks
#>
[CmdletBinding()]
param(
    [string]$GodotExe = $env:GODOT_EDITOR,
    [switch]$InstallTemplates,
    [switch]$WithDebugTemplate,
    [switch]$SkipBuild,
    [switch]$SkipChecks,
    [switch]$SkipImport,
    [switch]$SkipCapture,
    [switch]$KeepTemp,
    [switch]$KeepTpz
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 常量

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$ProjectDir  = Join-Path $RepoRoot 'idle-sword'
$PresetFile  = Join-Path $ProjectDir 'export_presets.cfg'
$Csproj      = Join-Path $ProjectDir 'Idle-Sword.csproj'
$Solution    = Join-Path $ProjectDir 'Idle-Sword.sln'
$OutDir      = Join-Path $RepoRoot 'artifacts\windows'
$PresetName  = 'Windows Desktop'

# 引擎不是装在 PATH 里的，这两条是本机与常见安装位置的候选；跨机器请用 GODOT_EDITOR。
$GodotCandidates = @(
    'D:\Downloads\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe'
)

$TemplateUrl  = 'https://github.com/godotengine/godot/releases/download/4.7-stable/Godot_v4.7-stable_mono_export_templates.tpz'
$TemplateTpz  = Join-Path $env:TEMP 'Godot_v4.7-stable_mono_export_templates.tpz'
$TemplateSize = 1200753503L   # 官方 release 的 Content-Length，用来挡住截断的下载
# 官方 release 的 SHA256（已与本机实下结果、GitHub releases API 三处对齐），挡住被代理改写的下载。
$TemplateSha256 = '4C02A0B99AD9C5BC243C2E79468628DB3DF89350D9DB8FC995988F69D126E069'

# 自检里断言的标记串，见 idle-sword/UI/Main.cs。
$MarkerReady   = 'Idle-Sword READY'
$MarkerSmoke   = 'UI SMOKE PASS'

# 导出输出里出现这些就判失败。Godot 的退出码不可信，只能靠输出 + 产物双保险。
$FatalMarkers = @(
    'Export failed',
    'No export template found',
    'Failed to build project',
    'no solution file was found',
    'Unable to find the .NET assemblies',
    'Cannot save file'
)

$AppUserData = Join-Path $env:APPDATA 'Godot\app_userdata\Idle-Sword'
$SaveFile    = Join-Path $AppUserData 'save_v1.json'
# 截图落在 artifacts/ 下（已被 gitignore）：既在交付目录之外，又能留存下来给人看。
$ShotPath    = Join-Path $RepoRoot 'artifacts\pack-preview.png'

# ---------------------------------------------------------------- 断言收集

$script:Checks   = New-Object System.Collections.Generic.List[string]
$script:Failures = New-Object System.Collections.Generic.List[string]

function Add-Check {
    param([string]$Name, [bool]$Ok, [string]$Detail = '')
    if ($Ok) {
        $script:Checks.Add("PASS  $Name")
        Write-Host "  [ok]   $Name" -ForegroundColor DarkGreen
    } else {
        $script:Checks.Add("FAIL  $Name  -- $Detail")
        $script:Failures.Add("$Name -- $Detail")
        Write-Host "  [FAIL] $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "         $Detail" -ForegroundColor Red }
    }
}

function Show-Summary {
    Write-Host ''
    Write-Host '================ 打包结果 ================' -ForegroundColor Cyan
    foreach ($line in $script:Checks) {
        if ($line.StartsWith('FAIL')) { Write-Host $line -ForegroundColor Red }
        else { Write-Host $line -ForegroundColor DarkGray }
    }
    Write-Host '==========================================' -ForegroundColor Cyan
    if ($script:Failures.Count -eq 0) {
        Write-Host ("全部 {0} 项通过。" -f $script:Checks.Count) -ForegroundColor Green
    } else {
        Write-Host ("{0} / {1} 项失败。" -f $script:Failures.Count, $script:Checks.Count) -ForegroundColor Red
    }
}

# 前置检查失败：立刻中止，不继续浪费一次长导出。
function Stop-Preflight {
    param([string]$Message)
    Write-Host ''
    Write-Host "前置检查未通过：$Message" -ForegroundColor Red
    Show-Summary
    exit 1
}

function Write-Step {
    param([string]$Text)
    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

# ---------------------------------------------------------------- 进程调用

# Windows PowerShell 5.1 跑在 .NET Framework 上，没有 ProcessStartInfo.ArgumentList，
# 只能自己拼参数串。含空格或引号的参数用双引号包起来。
function Format-ArgumentLine {
    param([string[]]$Arguments)
    $parts = foreach ($a in $Arguments) {
        if ($a -match '[\s"]') { '"' + ($a -replace '"', '\"') + '"' } else { $a }
    }
    return ($parts -join ' ')
}

# 统一的进程调用。返回 @{ ExitCode; Stdout; Stderr; TimedOut }。
# 用 ReadToEndAsync 而不是同步 ReadToEnd，避免子进程写满管道缓冲后双方互等。
function Invoke-Process {
    param(
        [Parameter(Mandatory)][string]$Exe,
        [string[]]$Arguments = @(),
        [int]$TimeoutSec = 900,
        [string]$WorkDir = $RepoRoot
    )
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = $Exe
    $psi.Arguments              = Format-ArgumentLine $Arguments
    $psi.UseShellExecute        = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.CreateNoWindow         = $true
    $psi.WorkingDirectory       = $WorkDir

    $proc = [System.Diagnostics.Process]::Start($psi)
    $outTask = $proc.StandardOutput.ReadToEndAsync()
    $errTask = $proc.StandardError.ReadToEndAsync()
    $timedOut = -not $proc.WaitForExit($TimeoutSec * 1000)
    if ($timedOut) {
        try { $proc.Kill() } catch { }
        try { $proc.WaitForExit(5000) | Out-Null } catch { }
    }
    # hashtable 的值位置不接受 if 语句（PS 5.1 会解析失败），先算好再放进去。
    $exitCode = if ($timedOut) { -1 } else { $proc.ExitCode }
    return @{
        ExitCode = $exitCode
        Stdout   = $outTask.Result
        Stderr   = $errTask.Result
        TimedOut = $timedOut
    }
}

function Invoke-Godot {
    param([string[]]$Arguments, [int]$TimeoutSec = 900, [string]$WorkDir = $RepoRoot)
    return Invoke-Process -Exe $GodotExe -Arguments $Arguments -TimeoutSec $TimeoutSec -WorkDir $WorkDir
}

function Get-Lines {
    param([string]$Text)
    return @($Text -split "`r?`n" | Where-Object { $_.Trim() -ne '' })
}

function Write-Output-Head {
    param([string]$Text, [int]$Count = 40, [string]$Label = '输出')
    $lines = Get-Lines $Text
    Write-Host ("--- {0}（共 {1} 行，显示前 {2} 行）---" -f $Label, $lines.Count, $Count) -ForegroundColor DarkGray
    $lines | Select-Object -First $Count | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
}

# ---------------------------------------------------------------- 预设解析

function Get-PresetText {
    if (-not (Test-Path -LiteralPath $PresetFile)) { Stop-Preflight "找不到预设文件 $PresetFile" }
    return (Get-Content -LiteralPath $PresetFile -Raw -Encoding UTF8)
}

function Test-PresetOption {
    param([string]$Text, [string]$Key, [string]$Value)
    return [regex]::IsMatch($Text, '(?m)^' + [regex]::Escape($Key) + '\s*=\s*' + [regex]::Escape($Value) + '\s*$')
}

# ================================================================ 1. 前置检查

Write-Host 'Idle-Sword Windows 打包' -ForegroundColor Cyan
Write-Host "仓库根目录：$RepoRoot"

Write-Step '1/8 前置检查'

# --- 引擎可执行文件
$resolvedGodot = $null
if ($GodotExe -and (Test-Path -LiteralPath $GodotExe)) {
    $resolvedGodot = $GodotExe
} else {
    if ($GodotExe) { Write-Host "  GODOT_EDITOR 指向的路径不存在：$GodotExe" -ForegroundColor Yellow }
    foreach ($cand in $GodotCandidates) { if (Test-Path -LiteralPath $cand) { $resolvedGodot = $cand; break } }
    if (-not $resolvedGodot) {
        $cmd = Get-Command godot -ErrorAction SilentlyContinue
        if ($cmd) { $resolvedGodot = $cmd.Source }
    }
}
if (-not $resolvedGodot) {
    Stop-Preflight @"
找不到 Godot 编辑器。请设置环境变量 GODOT_EDITOR 指向 .NET(mono) 版编辑器，例如：
    `$env:GODOT_EDITOR = 'D:\Downloads\Godot_v4.7-stable_mono_win64\Godot_v4.7-stable_mono_win64.exe'
或把 -GodotExe 传进来。注意必须是带 .NET 的 mono 版，普通版导不出 C# 工程。
"@
}
$GodotExe = $resolvedGodot
Add-Check '找到 Godot 编辑器' $true
Write-Host "          $GodotExe" -ForegroundColor DarkGray

# --- 版本：必须是 .NET 版，且与工程声明的 4.7 一致
$verRes = Invoke-Process -Exe $GodotExe -Arguments @('--version') -TimeoutSec 60
$versionString = ($verRes.Stdout -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1)
if (-not $versionString) { $versionString = ($verRes.Stderr -split "`r?`n" | Where-Object { $_.Trim() -ne '' } | Select-Object -First 1) }
Add-Check '读取引擎版本' ([bool]$versionString) "拿到空版本号，退出码 $($verRes.ExitCode)"
Write-Host "          $versionString" -ForegroundColor DarkGray

$isDotNetBuild = $versionString -match '\.mono\.'
Add-Check '引擎是 .NET(mono) 版' $isDotNetBuild "版本串里没有 .mono.：$versionString —— 普通版引擎无法导出 C# 工程"

# 版本目录名 = 引擎版本串去掉最后两段（.official 与构建哈希）。
# 见 core/version.h：GODOT_VERSION_FULL_CONFIG = 号.状态.模块配置。
$versionParts = @($versionString -split '\.')
if ($versionParts.Count -lt 4) { Stop-Preflight "无法从版本串 $versionString 解析出模板版本目录名" }
$versionDirName = ($versionParts[0..($versionParts.Count - 3)] -join '.')

$engineMajorMinor = ($versionParts[0..1] -join '.')
$projectFeatures = Get-Content -LiteralPath (Join-Path $ProjectDir 'project.godot') -Raw -Encoding UTF8
$featureMatch = [regex]::Match($projectFeatures, 'config/features=PackedStringArray\("([^"]+)"')
$projectMajorMinor = if ($featureMatch.Success) { $featureMatch.Groups[1].Value } else { '' }
Add-Check '引擎版本与 project.godot 一致' ($projectMajorMinor -ne '' -and $projectMajorMinor -eq $engineMajorMinor) `
    "project.godot 声明 $projectMajorMinor，引擎是 $engineMajorMinor"
Write-Host "          模板版本目录：$versionDirName" -ForegroundColor DarkGray

# --- 导出模板
$TemplateDir      = Join-Path $env:APPDATA "Godot\export_templates\$versionDirName"
$ReleaseTemplate  = Join-Path $TemplateDir 'windows_release_x86_64.exe'
$TemplatePresent  = Test-Path -LiteralPath $ReleaseTemplate

if (-not $TemplatePresent) {
    if ($InstallTemplates) {
        Write-Step "1b/8 安装导出模板（首次约 1.1 GB）"
        if (-not (Test-Path -LiteralPath $TemplateTpz) -or (Get-Item -LiteralPath $TemplateTpz).Length -ne $TemplateSize) {
            Write-Host "  下载 $TemplateUrl"
            Add-Type -AssemblyName System.Net.Http
            $client = New-Object System.Net.Http.HttpClient
            $client.Timeout = [TimeSpan]::FromHours(2)
            $resp = $client.GetAsync($TemplateUrl, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).Result
            $resp.EnsureSuccessStatusCode() | Out-Null
            $total = $resp.Content.Headers.ContentLength
            $inStream  = $resp.Content.ReadAsStreamAsync().Result
            $outStream = [System.IO.File]::Create($TemplateTpz)
            $buffer = [byte[]]::new(4194304)
            $done = 0L; $nextMark = 0L
            while (($read = $inStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $outStream.Write($buffer, 0, $read); $done += $read
                if ($done -ge $nextMark) {
                    $nextMark += 268435456L
                    Write-Host ("    {0:N0} / {1:N0} 字节" -f $done, $total) -ForegroundColor DarkGray
                }
            }
            $outStream.Close(); $inStream.Close(); $client.Dispose()
        } else {
            Write-Host "  复用已下载的模板包：$TemplateTpz" -ForegroundColor DarkGray
        }

        $tpzInfo = Get-Item -LiteralPath $TemplateTpz
        if ($tpzInfo.Length -ne $TemplateSize) {
            Stop-Preflight "模板包大小不符：$($tpzInfo.Length) 字节，预期 $TemplateSize —— 下载被截断或被代理改写"
        }
        Write-Host ("  校验 SHA256 …") -ForegroundColor DarkGray
        $sha = (Get-FileHash -LiteralPath $TemplateTpz -Algorithm SHA256).Hash
        Write-Host "  SHA256 $sha" -ForegroundColor DarkGray
        if ($sha -ne $TemplateSha256) {
            Stop-Preflight "模板包 SHA256 不符：$sha，预期 $TemplateSha256 —— 不是官方包或被代理改写"
        }
        # 下载来的可执行模板带 Mark-of-the-Web 时 Windows 会拦，先解封。
        Unblock-File -LiteralPath $TemplateTpz -ErrorAction SilentlyContinue

        # .tpz 是个 zip，里面是 templates/<文件>；而引擎找的是 <版本目录>/<文件>，
        # 即安装时要**剥掉 templates/ 前缀**（见 EditorExportPlatform::find_export_template）。
        # 只取 Windows 那两个，剩下约 1 GB 的 Linux/macOS/Android/Web 模板不落盘。
        $wanted = @('version.txt', 'windows_release_x86_64.exe')
        if ($WithDebugTemplate) { $wanted += 'windows_debug_x86_64.exe' }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        if (-not (Test-Path -LiteralPath $TemplateDir)) { New-Item -ItemType Directory -Path $TemplateDir -Force | Out-Null }
        $zip = [System.IO.Compression.ZipFile]::OpenRead($TemplateTpz)
        try {
            foreach ($name in $wanted) {
                $entry = $zip.Entries | Where-Object { $_.FullName -eq "templates/$name" } | Select-Object -First 1
                if (-not $entry) { Stop-Preflight "模板包里没有 templates/$name —— 模板包内容与预期不符" }
                $dest = Join-Path $TemplateDir $name
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
                Unblock-File -LiteralPath $dest -ErrorAction SilentlyContinue
                Write-Host ("  写入 {0} ({1:N0} 字节)" -f $dest, (Get-Item -LiteralPath $dest).Length) -ForegroundColor DarkGray
            }
        } finally { $zip.Dispose() }

        if (-not $KeepTpz) { Remove-Item -LiteralPath $TemplateTpz -Force -ErrorAction SilentlyContinue }
        $TemplatePresent = Test-Path -LiteralPath $ReleaseTemplate
    } else {
        Stop-Preflight @"
导出模板未安装。预期路径：
    $ReleaseTemplate
用 -InstallTemplates 让本脚本自动下载安装：
    powershell -File tools\PackWindows.ps1 -InstallTemplates
或在该版本的 Godot 编辑器里 Editor > Manage Export Templates > Download and Install（走同一份官方包）。
"@
    }
}
Add-Check '导出模板已安装' $TemplatePresent "预期位置 $ReleaseTemplate"
if ($TemplatePresent) { Write-Host "          $ReleaseTemplate" -ForegroundColor DarkGray }

# --- C# 解决方案：Godot 的 .NET 导出靠它判断"这个工程有没有 C#"。
# 缺了它，dotnet publish 会被静默跳过、导出照样返回 0，打出来的 exe 里没有程序集。
Add-Check '存在 C# 解决方案 Idle-Sword.sln' (Test-Path -LiteralPath $Solution) `
    "缺少 $Solution —— Godot 会静默跳过 dotnet publish，产出没有 C# 的坏包"

# --- dotnet SDK
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
Add-Check 'dotnet SDK 可用' ([bool]$dotnetCmd) 'PATH 里找不到 dotnet'

# --- 预设
$presetText = Get-PresetText
Add-Check "预设 `"$PresetName`" 存在" ($presetText -match [regex]::Escape('name="' + $PresetName + '"')) '预设文件里没有这个预设名'
Add-Check '预设启用单文件 PCK 内嵌' (Test-PresetOption $presetText 'binary_format/embed_pck' 'true') `
    'binary_format/embed_pck 不是 true —— 会产出 exe+pck 两个文件'
Add-Check '预设内嵌 .NET 构建产物' (Test-PresetOption $presetText 'dotnet/embed_build_outputs' 'true') `
    'dotnet/embed_build_outputs 不是 true —— 程序集会变成外置目录'

$pathMatch = [regex]::Match($presetText, '(?m)^export_path\s*=\s*"([^"]*)"')
Add-Check '预设声明了导出路径' $pathMatch.Success 'export_presets.cfg 里没有 export_path'
if (-not $pathMatch.Success) { Show-Summary; exit 1 }
# 预设是唯一事实来源：脚本按它算绝对路径，再显式传给命令行。
$ExePath = [System.IO.Path]::GetFullPath((Join-Path $ProjectDir $pathMatch.Groups[1].Value))
Add-Check '导出路径可解析' ([System.IO.Path]::IsPathRooted($ExePath)) "解析结果：$ExePath"
Write-Host "          产物：$ExePath" -ForegroundColor DarkGray

if ($script:Failures.Count -gt 0) { Show-Summary; exit 1 }

# ================================================================ 2. 质量闸

Write-Step '2/8 质量闸'

if ($SkipBuild) {
    Write-Host '  已跳过编译' -ForegroundColor Yellow
} else {
    $buildRes = Invoke-Process -Exe $dotnetCmd.Source -Arguments @('build', $Csproj, '-warnaserror', '--nologo') -TimeoutSec 600
    if ($buildRes.ExitCode -ne 0) { Write-Output-Head $buildRes.Stdout 40 'dotnet build' }
    Add-Check 'dotnet build 通过（警告即错误）' ($buildRes.ExitCode -eq 0) "退出码 $($buildRes.ExitCode)"
}

if ($SkipChecks) {
    Write-Host '  已跳过纯逻辑自检' -ForegroundColor Yellow
} else {
    $checkRes = Invoke-Process -Exe $dotnetCmd.Source `
        -Arguments @('run', '--project', 'tests/IdleSword.Checks.csproj', '--', 'idle-sword/Config/Tables') -TimeoutSec 600
    if ($checkRes.ExitCode -ne 0) { Write-Output-Head $checkRes.Stdout 40 '逻辑自检' }
    Add-Check '纯逻辑自检通过' ($checkRes.ExitCode -eq 0) "退出码 $($checkRes.ExitCode)"
}

# ================================================================ 3. 导入

Write-Step '3/8 导入资源'
if ($SkipImport) {
    Write-Host '  已跳过导入' -ForegroundColor Yellow
} else {
    # --import 的退出码不可靠（godot#83449），这里不据此判定成败，真正的证据在后面。
    $importRes = Invoke-Godot -Arguments @('--headless', '--path', $ProjectDir, '--editor', '--import') -TimeoutSec 900
    Write-Host "  导入完成（退出码 $($importRes.ExitCode)，该退出码不可信，不作为判据）" -ForegroundColor DarkGray
}

# ================================================================ 4. 导出

Write-Step '4/8 导出'

# 先清空产物目录：残留的 .pck / data_* 会让后面的断言变成假通过。
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
Write-Host "  清空并重建 $OutDir" -ForegroundColor DarkGray

$exportRes = Invoke-Godot -Arguments @('--headless', '--path', $ProjectDir, '--export-release', $PresetName, $ExePath) -TimeoutSec 1800

if ($exportRes.TimedOut) { Add-Check '导出未超时' $false '导出超过 30 分钟，已强杀' }

# 退出码不可信（godot#85062），所以两路并查：输出里的致命标记 + 产物本身。
$allExportText = ($exportRes.Stdout + "`n" + $exportRes.Stderr)
$errorLines = @(Get-Lines $allExportText | Where-Object { $_ -match '(?i)^\s*(ERROR|SCRIPT ERROR|FATAL)\b' })
$hitMarkers = @($FatalMarkers | Where-Object { $allExportText -match [regex]::Escape($_) })

Add-Check '导出输出无致命错误标记' ($hitMarkers.Count -eq 0) `
    ("命中：" + ($hitMarkers -join ' / '))
if ($errorLines.Count -gt 0) {
    Write-Host "  导出输出里有 $($errorLines.Count) 行 ERROR（供参考）：" -ForegroundColor Yellow
    $errorLines | Select-Object -First 15 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
}
Write-Host "  导出退出码 $($exportRes.ExitCode)（不可信，仅记录）" -ForegroundColor DarkGray

# ================================================================ 5. 产物断言

Write-Step '5/8 产物断言'

$exeInfo = Get-Item -LiteralPath $ExePath -ErrorAction SilentlyContinue
Add-Check 'exe 已产出' ($null -ne $exeInfo -and $exeInfo.Length -gt 0) "没找到 $ExePath"

if ($null -eq $exeInfo) { Write-Output-Head $allExportText 60 '导出输出'; Show-Summary; exit 1 }

$templateSize = (Get-Item -LiteralPath $ReleaseTemplate).Length
# 自校准：内嵌 PCK 后 exe 必然比纯模板大。这条能挡住"模板没换、Pck 没嵌"的桩包。
Add-Check 'exe 大于引擎模板（说明 PCK 真的嵌进去了）' ($exeInfo.Length -gt $templateSize) `
    ("exe {0:N0} 字节 ≤ 模板 {1:N0} 字节" -f $exeInfo.Length, $templateSize)

$outFiles = @(Get-ChildItem -LiteralPath $OutDir -File -Force)
$outDirs  = @(Get-ChildItem -LiteralPath $OutDir -Directory -Force)
Add-Check '产物目录只有单个文件' ($outFiles.Count -eq 1) `
    ("目录里有 {0} 个文件：{1}" -f $outFiles.Count, (($outFiles | ForEach-Object { $_.Name }) -join ', '))
Add-Check '没有外置 .pck' (@($outFiles | Where-Object { $_.Extension -eq '.pck' }).Count -eq 0) `
    '存在 .pck，说明 binary_format/embed_pck 没生效'
Add-Check '没有外置程序集目录 data_*' (@($outDirs | Where-Object { $_.Name -like 'data_*' }).Count -eq 0) `
    '存在 data_* 目录，说明 dotnet/embed_build_outputs 没生效'

Write-Host ("  产物 {0:N0} 字节（{1:N1} MB）" -f $exeInfo.Length, ($exeInfo.Length / 1MB)) -ForegroundColor DarkGray
Write-Host ("  SHA256 {0}" -f (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash) -ForegroundColor DarkGray

# ================================================================ 6. 独立运行

Write-Step '6/8 独立运行验证'

# 拷到仓库外再跑：证明这个 exe 不依赖源码目录、不依赖同目录的兄弟文件。
$runDir = Join-Path $env:TEMP ('IdleSword-Ship-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
Copy-Item -Path (Join-Path $OutDir '*') -Destination $runDir -Recurse -Force
$runExe = Join-Path $runDir (Split-Path -Leaf $ExePath)
Add-Check '产物已拷贝到仓库外' (Test-Path -LiteralPath $runExe) "拷贝失败：$runExe"
Write-Host "  运行目录 $runDir" -ForegroundColor DarkGray

# QA 模式只写 user://qa/*，玩家存档不该被动过。
$saveBefore = if (Test-Path -LiteralPath $SaveFile) { (Get-Item -LiteralPath $SaveFile).LastWriteTimeUtc } else { $null }

# 6a. 无窗口自检：证明它能启动、且断言全过。
$smokeRes = Invoke-Process -Exe $runExe -Arguments @('--headless', '--', '--smoke-test') -TimeoutSec 600 -WorkDir $runDir
$smokeText = ($smokeRes.Stdout + "`n" + $smokeRes.Stderr)
Add-Check '打包版无窗口自检退出码为 0' ($smokeRes.ExitCode -eq 0) "退出码 $($smokeRes.ExitCode)"
Add-Check '打包版自检跑到通过标记' ($smokeText -match [regex]::Escape($MarkerSmoke)) `
    "输出里没有 `"$MarkerSmoke`""
if (-not $smokeText.Contains($MarkerSmoke)) { Write-Output-Head $smokeText 60 '自检输出' }

# 启动行里的资源计数证明 PCK 里真的带了 CSV 与 JSON，而不只是带了个引擎。
$readyMatch = [regex]::Match($smokeText, 'levels=(\d+)\s+skills=(\d+)\s+audio=(\d+)\s+sfx_voices=(\d+)')
Add-Check '启动行显示配置与音频资源已载入' $readyMatch.Success `
    "输出里没有 $MarkerReady 的资源计数行"
if ($readyMatch.Success) {
    $lv = [int]$readyMatch.Groups[1].Value; $sk = [int]$readyMatch.Groups[2].Value
    $au = [int]$readyMatch.Groups[3].Value; $sv = [int]$readyMatch.Groups[4].Value
    Add-Check '关卡/剑诀/音频数量均大于 0（CSV 与 JSON 已进包）' (($lv -gt 0) -and ($sk -gt 0) -and ($au -gt 0) -and ($sv -gt 0)) `
        "levels=$lv skills=$sk audio=$au sfx_voices=$sv"
    Write-Host "          levels=$lv skills=$sk audio=$au sfx_voices=$sv" -ForegroundColor DarkGray
}

# 6b. 有窗口截图：证明渲染管线与中文字体在成品里真的可用。
#     --capture 隐含自检模式，所以这一跑同时把断言跑了一遍。
if ($SkipCapture) {
    Write-Host '  已跳过截图验证' -ForegroundColor Yellow
} else {
    # 用绝对路径截图（README 记录的就是这个用法）。别用 user://qa/xxx.png：
    # SavePng 不会自动建目录，而 user://qa/ 在干净机器上首次截图时尚未创建，会直接失败。
    $shotAbs = $ShotPath
    $shotDir = Split-Path -Parent $shotAbs
    if (-not (Test-Path -LiteralPath $shotDir)) { New-Item -ItemType Directory -Path $shotDir -Force | Out-Null }
    if (Test-Path -LiteralPath $shotAbs) { Remove-Item -LiteralPath $shotAbs -Force }
    # 注意：--capture 走 SavePng，无窗口（--headless）下会挂住，必须带窗口跑。
    $capRes  = Invoke-Process -Exe $runExe -Arguments @('--', '--capture', $shotAbs) -TimeoutSec 600 -WorkDir $runDir
    $capText = ($capRes.Stdout + "`n" + $capRes.Stderr)
    Add-Check '打包版截图运行退出码为 0' ($capRes.ExitCode -eq 0) "退出码 $($capRes.ExitCode)"
    $shotOk = Test-Path -LiteralPath $shotAbs
    Add-Check '截图像素已产出' $shotOk "没找到 $shotAbs"
    if (-not $shotOk) { Write-Output-Head $capText 40 '截图运行输出' }

    if ($shotOk) {
        Add-Type -AssemblyName System.Drawing
        $bmp = [System.Drawing.Bitmap]::FromFile($shotAbs)
        try {
            $w = $bmp.Width; $h = $bmp.Height
            $colors = New-Object 'System.Collections.Generic.HashSet[int]'
            $stepX = [Math]::Max(1, [int]($w / 48)); $stepY = [Math]::Max(1, [int]($h / 27))
            for ($x = 0; $x -lt $w; $x += $stepX) {
                for ($y = 0; $y -lt $h; $y += $stepY) { [void]$colors.Add($bmp.GetPixel($x, $y).ToArgb()) }
            }
            $colorCount = $colors.Count
        } finally { $bmp.Dispose() }

        Add-Check '截图尺寸正常（≥1024×576）' (($w -ge 1024) -and ($h -ge 576)) "实际 ${w}x${h}"
        # 纯色/黑屏的截图颜色数会很少；这一步挡住"启动了但什么也没画"。
        Add-Check '截图不是空白（采样颜色数 ≥ 8）' ($colorCount -ge 8) "采样到 $colorCount 种颜色"
        Write-Host ("  截图 {0}  ({1}x{2}, 采样 {3} 色)" -f $shotAbs, $w, $h, $colorCount) -ForegroundColor DarkGray
        Write-Host '  >>> 请人工看一眼这张图，确认中文不是豆腐块（系统字体缺字只有肉眼查得出）' -ForegroundColor Yellow
    }
}

# 6c. QA 不应碰真实存档
$saveAfter = if (Test-Path -LiteralPath $SaveFile) { (Get-Item -LiteralPath $SaveFile).LastWriteTimeUtc } else { $null }
Add-Check '真实存档未被 QA 运行改写' ($saveBefore -eq $saveAfter) "save_v1.json 的修改时间变了"

if ($KeepTemp) { Write-Host "  保留临时目录 $runDir" -ForegroundColor DarkGray }
else { Remove-Item -LiteralPath $runDir -Recurse -Force -ErrorAction SilentlyContinue }

# ================================================================ 7. 汇总

Write-Step '7/8 汇总'
Write-Host ("  产物      : {0}" -f $ExePath)
Write-Host ("  大小      : {0:N0} 字节 ({1:N1} MB)" -f $exeInfo.Length, ($exeInfo.Length / 1MB))
Write-Host ("  引擎      : {0}" -f $versionString)
Write-Host ("  模板目录  : {0}" -f $TemplateDir)

Write-Step '8/8 结果'
Show-Summary

if ($script:Failures.Count -gt 0) { exit 1 }
Write-Host "已产出单文件独立包体：$ExePath" -ForegroundColor Green
exit 0
