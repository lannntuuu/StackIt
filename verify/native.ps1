/**
 * 回归验证：原生 C# 版 vs 黄金基准。
 *
 * 为什么留这个：解析器与统计算法处理的是 3719 行、127 张周表、1697 条记录的真实文件，
 * 而这文件里有手误、有粘连、有同月同日出现在两个年份的情况。
 * 一个 off-by-one、少一条边界记录、或者星期索引搞错（0=周日 vs 0=周一），
 * 界面上的数字就会悄悄错，而且很难肉眼发现。
 *
 * 基准（verify/baseline-*.json）是网页版 TypeScript 解析器跑真实 记录.md 的输出，
 * 网页版已删除，基准作为黄金文件保留。它证明的是「C# 移植没有抄错」：
 *   - 1697 条事件，一一对应（不许多、不许少）
 *   - 统计：总数、天数、日期区间、每品类的次数/天数/覆盖率、星期与时段分布
 *   - 分桶：142 周的每周累计、每个品类的可记窗口
 *
 * 注意：修改 记录.md 或解析逻辑后，这个基准不会自动更新。
 * 那说明确实变了——先确认变化是对的，再手工同步基准，别为了让测试变绿而改基准。
 */
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = "$PWD\.dch"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "`n=== 运行 C# 版，与黄金基准逐条对比 ===" -ForegroundColor Cyan
& dotnet run --project native/StackIt.Core -c Release --no-launch-profile -- `
    记录.md `
    verify/baseline-events.json `
    verify/baseline-report.json `
    verify/baseline-stats.json `
    verify/baseline-history.json
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) {
    Write-Host "通过：原生版与基准完全一致" -ForegroundColor Green
} else {
    Write-Host "不通过：有差异，不要相信原生版当前的数据" -ForegroundColor Red
}

# 界面耗时也要守：曾经时间流全量布局要 9.4 秒，界面等于卡死，
# 而数据一致性检查完全看不出来（数字都对，就是点不动）。
$exe = "native/StackIt.App/bin/Release/net8.0-windows/StackIt.App.exe"
if (Test-Path $exe) {
    Write-Host "`n=== 界面耗时（防止长列表卡死）===" -ForegroundColor Cyan

    # 用一个临时库，先灌真实数据。耗时必须在有数据时量才有意义 ——
    # 空库每页都是几十毫秒，测了等于没测。
    #
    # 注意 STACKIT_DATA_DIR 是**原样使用**的目录（不再拼 StackIt 子目录），
    # seed 与计时必须指同一个目录，否则计时那步读到的是空库。
    $timingDir = Join-Path $root ".dch\timing-data"
    Remove-Item $timingDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $timingDir -Force | Out-Null
    $env:STACKIT_DATA_DIR = $timingDir

    # 不要用管道接住 seed 的输出：$ErrorActionPreference='Stop' 下，
    # 原生命令的非零退出会被当成终止性错误，脚本会在这里直接死掉，
    # 后面全部不执行（表现为"计时那段一行都没有"）。
    & dotnet run --project native/StackIt.Core -c Release --no-launch-profile -- `
        seed 记录.md

    $dbFile = Join-Path $timingDir "db.json"
    if (-not (Test-Path $dbFile)) {
        Write-Host "灌数据失败，跳过耗时检查`n" -ForegroundColor Red
        $code = 1
    } else {
        # StackIt.App 是 GUI 子系统程序（WinExe），不能用 & 调用：
        # 它的 stdout 不会接到 PowerShell 的控制台，退出码也不可靠 ——
        # 表现为"这段一行输出都没有"，甚至读到假的空库。
        # 用 cmd /c 重定向到文件是取 GUI 程序输出的可靠办法
        # （Start-Process 在受限环境下会被拒绝，所以不用它）。
        $outLog = Join-Path $timingDir "timing.log"
        $errLog = Join-Path $timingDir "timing.err"
        $exeAbs = (Resolve-Path $exe).Path
        cmd /c "`"$exeAbs`" --timing all > `"$outLog`" 2> `"$errLog`""
        $timingCode = $LASTEXITCODE

        if (Test-Path $outLog) { Get-Content $outLog | ForEach-Object { Write-Host "  $_" } }

        if ($timingCode -ne 0) {
            if (Test-Path $errLog) { Get-Content $errLog | ForEach-Object { Write-Host "  $_" -ForegroundColor Red } }
            Write-Host "界面耗时超出预算，界面可能已经卡住`n" -ForegroundColor Red
            $code = 1
        }

        # 时间范围筛选的逻辑自检。日期前缀匹配（年 vs 月只差几个字符）
        # 是"少一条 / 多一条"那类会静默出错的逻辑，必须有断言守着。
        Write-Host "`n=== 时间范围筛选 ===" -ForegroundColor Cyan
        $fLog = Join-Path $timingDir "filter.log"
        cmd /c "`"$exeAbs`" --filter-test > `"$fLog`" 2>&1"
        $fCode = $LASTEXITCODE
        if (Test-Path $fLog) { Get-Content $fLog | ForEach-Object { Write-Host "  $_" } }
        if ($fCode -ne 0) {
            Write-Host "筛选逻辑有错，时间流会漏记录或多记录`n" -ForegroundColor Red
            $code = 1
        }

        # 托盘行为 + 窗口图标。
        # 这段最容易出的错是"以为藏起来了其实退出了"，而且不留报错，
        # 表现就是用户关掉窗口、第二天发现程序没了。
        Write-Host "`n=== 托盘与图标 ===" -ForegroundColor Cyan
        $tLog = Join-Path $timingDir "tray.log"
        cmd /c "`"$exeAbs`" --tray-test > `"$tLog`" 2>&1"
        $tCode = $LASTEXITCODE
        if (Test-Path $tLog) { Get-Content $tLog | ForEach-Object { Write-Host "  $_" } }
        if ($tCode -ne 0) {
            Write-Host "托盘行为不对：点右上角 X 可能直接退出了`n" -ForegroundColor Red
            $code = 1
        }

        # 图标文件结构。这个错不崩、不报错，只是图标在某些地方显示空白，
        # 只能靠断言守。
        $iLog = Join-Path $timingDir "icon.log"
        $icoAbs = (Resolve-Path (Join-Path $root "native\StackIt.App\app.ico")).Path
        cmd /c "`"$exeAbs`" --icon-test `"$icoAbs`" > `"$iLog`" 2>&1"
        $iCode = $LASTEXITCODE
        if (Test-Path $iLog) { Get-Content $iLog | ForEach-Object { Write-Host "  $_" } }
        if ($iCode -ne 0) {
            Write-Host "图标文件有问题，任务栏或窗口图标可能显示空白`n" -ForegroundColor Red
            $code = 1
        }

        # 每个筛选档位都真渲染一遍。
        #
        # 为什么必须渲染而不能只跑逻辑自检：带 / 格视图里的越界崩溃只在
        # **那个筛选档位**下才发生。曾经全量视图整个崩溃
        # （Index was outside the bounds of the array），而逻辑自检全绿 ——
        # 因为自检只验日期匹配，不碰绘图代码。
        # 只测「全部」那一档也是漏的：出事的正是月 / 周。
        Write-Host "`n=== 各筛选档位渲染 ===" -ForegroundColor Cyan
        $rLog = Join-Path $timingDir "render.log"
        Remove-Item $rLog -ErrorAction SilentlyContinue
        $shots = @(
            @{ n = "全部";    f = "all" },
            @{ n = "年";      f = "y:2026" },
            @{ n = "月";      f = "m:2026-07" },
            @{ n = "周";      f = "w:2026-07-06" },
            @{ n = "早年";    f = "y:2023" }
        )
        $rCode = 0
        foreach ($s in $shots) {
            $png = Join-Path $timingDir ("render_" + $s.f.Replace(":", "-") + ".png")
            $err = Join-Path $timingDir ("render_" + $s.f.Replace(":", "-") + ".err")
            cmd /c "`"$exeAbs`" --shot `"$png`" stats 460 900 `"$($s.f)`" > `"$err`" 2>&1"
            $pngCode = $LASTEXITCODE

            # 崩溃页会渲染成一张写着"统计页打不开"的图，尺寸正常、退出码可能仍是 0，
            # 所以除了退出码还得看有没有启动错误日志。
            $crash = $false
            $selog = Join-Path (Split-Path $exeAbs) "startup-error.log"
            if (Test-Path $selog) { $crash = $true }

            $ok = ($pngCode -eq 0) -and (-not $crash) -and (Test-Path $png)
            if (-not $ok) {
                Write-Host "  FAIL  $($s.n)  渲染失败（退出码 $pngCode）" -ForegroundColor Red
                $rCode = 1
                if (Test-Path $selog) { Get-Content $selog -Tail 4 | ForEach-Object { Write-Host "        $_" -ForegroundColor Red } }
            } else {
                Write-Host "  PASS  $($s.n)  $($s.f)"
            }
            Remove-Item $selog -ErrorAction SilentlyContinue
        }
        if ($rCode -ne 0) {
            Write-Host "有筛选档位渲染不出来，用户点进去会看到错误页`n" -ForegroundColor Red
            $code = 1
        }
    }

    Remove-Item Env:\STACKIT_DATA_DIR -ErrorAction SilentlyContinue
    Remove-Item $timingDir -Recurse -Force -ErrorAction SilentlyContinue
}

exit $code