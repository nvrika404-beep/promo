#requires -Version 5.1
<#
.SYNOPSIS
    BatteryTray — показывает в трее Windows заряд наушников и других Bluetooth-устройств в процентах.

.DESCRIPTION
    Для каждого подключённого устройства, которое сообщает Windows уровень заряда,
    в трее (справа снизу) появляется своя иконка с числом процентов. Наведите курсор —
    увидите название устройства. Правый клик — меню (обновить, интервал, предупреждение
    о низком заряде, автозапуск, скрыть устройство, выход).

    Источник данных — тот же, что у «Параметры → Bluetooth и устройства»: свойство
    DEVPKEY_Bluetooth_Battery. Если Windows показывает процент у устройства,
    его покажет и BatteryTray.

.PARAMETER List
    Не запускать трей, а один раз вывести найденные устройства в консоль (для проверки).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\BatteryTray.ps1 -List
#>
param(
    [switch]$List
)

$ErrorActionPreference = 'Stop'
$ScriptPath = $PSCommandPath
$ScriptDir  = $PSScriptRoot

# ---------------------------------------------------------------------------
# Опрос устройств. Это самостоятельный текст скрипта: он выполняется в фоновом
# runspace, чтобы иконки и меню не подвисали, пока Windows отвечает.
# ---------------------------------------------------------------------------
$QueryScript = @'
$BATTERY_KEY   = '{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2'   # DEVPKEY_Bluetooth_Battery
$CONNECTED_KEY = '{83DA6326-97A6-4088-9453-A1923F573B29} 15'  # «устройство подключено»

function Get-Mac([string]$id) {
    $m = [regex]::Matches($id.ToUpperInvariant(), '(?<![0-9A-F])[0-9A-F]{12}(?![0-9A-F])')
    for ($i = $m.Count - 1; $i -ge 0; $i--) {
        # хвост стандартного Bluetooth GUID — не адрес устройства
        if ($m[$i].Value -ne '00805F9B34FB') { return $m[$i].Value }
    }
    return $null
}

function Get-Prop([string]$instanceId, [string]$key) {
    try {
        $p = Get-PnpDeviceProperty -InstanceId $instanceId -KeyName $key -ErrorAction Stop
        if ($p -and $p.Type -ne 'Empty') { return $p.Data }
    } catch {}
    return $null
}

Import-Module PnpDevice -ErrorAction SilentlyContinue
$all = @(Get-PnpDevice -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId -match '^(BTHENUM|BTHLE|BTHLEDEVICE)\\' })
if (-not $all.Count) { return }

# «Корневые» узлы устройств: BTHENUM\DEV_<MAC> (классический BT) и BTHLE\DEV_<MAC> (BLE)
$roots = @{}
foreach ($d in $all) {
    if ($d.InstanceId -match '^(BTHENUM|BTHLE)\\DEV_([0-9A-F]{12})') {
        $roots[$Matches[2].ToUpperInvariant()] = $d
    }
}

$byId = @{}
foreach ($d in $all) { $byId[$d.InstanceId] = $d }

$battery = @(Get-PnpDeviceProperty -InstanceId @($all | ForEach-Object InstanceId) -KeyName $BATTERY_KEY -ErrorAction SilentlyContinue |
    Where-Object { $_.Type -ne 'Empty' -and $null -ne $_.Data })

$found = @{}
foreach ($bp in $battery) {
    $d = $byId[$bp.InstanceId]
    if (-not $d) { continue }
    $val = $bp.Data
    $pct = [int]$val
    if ($pct -lt 0 -or $pct -gt 100) { continue }
    $mac = Get-Mac $d.InstanceId
    if (-not $mac) { continue }

    $prev = $found[$mac]
    # при нескольких значениях для одного устройства предпочитаем активный узел
    if ($prev -and -not ($d.Status -eq 'OK' -and -not $prev.NodeOk)) { continue }

    $root = $roots[$mac]
    $name = $null
    if ($root) { $name = $root.FriendlyName }
    if (-not $name) {
        $name = ($d.FriendlyName -replace '\s+(Hands-Free( AG)?( Audio)?|Avrcp Transport|Stereo|Headset)$', '')
    }
    if (-not $name) { $name = $mac }

    $connected = $null
    if ($root) { $connected = Get-Prop $root.InstanceId $CONNECTED_KEY }
    if ($null -eq $connected) {
        $connected = ($d.Status -eq 'OK') -and (-not $root -or $root.Status -eq 'OK')
    }

    $found[$mac] = [pscustomobject]@{
        Id        = $mac
        Name      = [string]$name
        Percent   = $pct
        Connected = [bool]$connected
        NodeOk    = ($d.Status -eq 'OK')
    }
}

$found.Values | Sort-Object Name
'@

if ($List) {
    $devices = @(& ([scriptblock]::Create($QueryScript)))
    if (-not $devices.Count) {
        Write-Host 'Устройств с данными о заряде не найдено.'
    } else {
        $devices | Format-Table Name, Percent, Connected, Id -AutoSize
    }
    return
}

# ---------------------------------------------------------------------------
# Трей
# ---------------------------------------------------------------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

# Только один экземпляр
$createdNew = $false
$script:Mutex = New-Object System.Threading.Mutex($true, 'Local\BatteryTray_6c1f0e2a', [ref]$createdNew)
if (-not $createdNew) { return }

Add-Type -Namespace BatteryTray -Name Native -MemberDefinition @'
[DllImport("user32.dll")]   public static extern bool DestroyIcon(IntPtr hIcon);
[DllImport("user32.dll")]   public static extern bool SetProcessDPIAware();
[DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
[DllImport("user32.dll")]   public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
[DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] list, uint count);
'@

# Прячем консоль, если она создана только для этого скрипта (двойной клик по .ps1 и т. п.).
# Если скрипт запущен из уже открытого терминала, его окно не трогаем.
$consoleHwnd = [BatteryTray.Native]::GetConsoleWindow()
$consoleProcs = [BatteryTray.Native]::GetConsoleProcessList((New-Object 'uint32[]' 4), 4)
if ($consoleHwnd -ne [IntPtr]::Zero -and $consoleProcs -le 1) { [void][BatteryTray.Native]::ShowWindow($consoleHwnd, 0) }
[void][BatteryTray.Native]::SetProcessDPIAware()
[System.Windows.Forms.Application]::EnableVisualStyles()

# ---------------------------------------------------------------------------
# Настройки (%APPDATA%\BatteryTray\settings.json)
# ---------------------------------------------------------------------------
$script:SettingsDir  = Join-Path $env:APPDATA 'BatteryTray'
$script:SettingsFile = Join-Path $script:SettingsDir 'settings.json'
$script:Settings = [pscustomobject]@{
    IntervalSec  = 30
    LowThreshold = 20
    Hidden       = @()
}

function Load-Settings {
    if (-not (Test-Path $script:SettingsFile)) { return }
    try {
        $s = Get-Content -Raw -Encoding UTF8 $script:SettingsFile | ConvertFrom-Json
        if ($s.IntervalSec -ge 5)            { $script:Settings.IntervalSec  = [int]$s.IntervalSec }
        if ($null -ne $s.LowThreshold)       { $script:Settings.LowThreshold = [int]$s.LowThreshold }
        if ($s.Hidden)                       { $script:Settings.Hidden       = @($s.Hidden) }
    } catch {}
}

function Save-Settings {
    try {
        if (-not (Test-Path $script:SettingsDir)) { New-Item -ItemType Directory -Path $script:SettingsDir | Out-Null }
        $script:Settings | ConvertTo-Json | Set-Content -Encoding UTF8 $script:SettingsFile
    } catch {}
}

Load-Settings

# ---------------------------------------------------------------------------
# Автозапуск — ярлык в папке «Автозагрузка»
# ---------------------------------------------------------------------------
$script:StartupLnk = Join-Path ([Environment]::GetFolderPath('Startup')) 'BatteryTray.lnk'

function Test-Autostart { Test-Path $script:StartupLnk }

function Set-Autostart([bool]$on) {
    if (-not $on) {
        Remove-Item $script:StartupLnk -ErrorAction SilentlyContinue
        return
    }
    $psExe   = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $conhost = Join-Path $env:WINDIR 'System32\conhost.exe'
    $psArgs  = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$ScriptPath`""
    $sh  = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($script:StartupLnk)
    # conhost --headless запускает PowerShell вовсе без окна (в т.ч. когда терминалом
    # по умолчанию выбран Windows Terminal). Есть начиная с Windows 10 1903.
    if ([Environment]::OSVersion.Version.Build -ge 18362 -and (Test-Path $conhost)) {
        $lnk.TargetPath = $conhost
        $lnk.Arguments  = "--headless `"$psExe`" $psArgs"
    } else {
        $lnk.TargetPath = $psExe
        $lnk.Arguments  = $psArgs
    }
    $lnk.WorkingDirectory = $ScriptDir
    $lnk.Description      = 'BatteryTray — заряд Bluetooth-устройств в трее'
    $lnk.Save()
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($sh)
}

# ---------------------------------------------------------------------------
# Рисование иконки с числом
# ---------------------------------------------------------------------------
function Test-LightTaskbar {
    try {
        $v = Get-ItemPropertyValue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' 'SystemUsesLightTheme' -ErrorAction Stop
        return ($v -eq 1)
    } catch { return $false }
}

function New-TextIcon([string]$text, [System.Drawing.Color]$color) {
    $sz  = [System.Windows.Forms.SystemInformation]::SmallIconSize.Width
    if ($sz -lt 16) { $sz = 16 }
    $bmp = New-Object System.Drawing.Bitmap($sz, $sz, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($bmp)
    $path  = New-Object System.Drawing.Drawing2D.GraphicsPath
    $brush = New-Object System.Drawing.SolidBrush($color)
    try {
        $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        $family = New-Object System.Drawing.FontFamily('Segoe UI')
        $path.AddString($text, $family, [int][System.Drawing.FontStyle]::Bold, [single]100,
            (New-Object System.Drawing.PointF(0, 0)), [System.Drawing.StringFormat]::GenericTypographic)
        $b = $path.GetBounds()
        if ($b.Width -gt 0 -and $b.Height -gt 0) {
            # цифры занимают ~70% высоты и не шире иконки
            $s = [Math]::Min(($sz - 0.5) / $b.Width, ($sz * 0.72) / $b.Height)
            $m = New-Object System.Drawing.Drawing2D.Matrix
            $m.Translate([single](($sz - $b.Width * $s) / 2), [single](($sz - $b.Height * $s) / 2))
            $m.Scale([single]$s, [single]$s)
            $m.Translate([single](-$b.X), [single](-$b.Y))
            $path.Transform($m)
            $m.Dispose()
        }
        $g.FillPath($brush, $path)
        $family.Dispose()
        return $bmp.GetHicon()
    } finally {
        $brush.Dispose(); $path.Dispose(); $g.Dispose(); $bmp.Dispose()
    }
}

function Get-LevelColor([int]$pct) {
    $light = Test-LightTaskbar
    if ($script:Settings.LowThreshold -gt 0 -and $pct -le $script:Settings.LowThreshold) {
        if ($light) { return [System.Drawing.Color]::FromArgb(196, 43, 28) }
        return [System.Drawing.Color]::FromArgb(255, 99, 88)
    }
    if ($light) { return [System.Drawing.Color]::FromArgb(25, 25, 25) }
    return [System.Drawing.Color]::White
}

function Get-IdleColor {
    if (Test-LightTaskbar) { return [System.Drawing.Color]::FromArgb(120, 120, 120) }
    return [System.Drawing.Color]::FromArgb(160, 160, 160)
}

# ---------------------------------------------------------------------------
# Слоты иконок. Создаются заранее в фиксированном порядке, чтобы у каждой был
# постоянный ID: тогда Windows запоминает, что иконку закрепили на панели задач.
# ---------------------------------------------------------------------------
$MaxSlots = 6
$script:Slots = @()
$script:Menu  = New-Object System.Windows.Forms.ContextMenuStrip

for ($i = 0; $i -lt $MaxSlots; $i++) {
    $ni = New-Object System.Windows.Forms.NotifyIcon
    $ni.ContextMenuStrip = $script:Menu
    $ni.Visible = $false
    $ni.add_MouseClick({
        param($sender, $e)
        if ($e.Button -eq [System.Windows.Forms.MouseButtons]::Left) { Request-Refresh }
    })
    $script:Slots += [pscustomobject]@{ Icon = $ni; HIcon = [IntPtr]::Zero; Key = ''; DeviceId = $null }
}

function Set-SlotIcon($slot, [string]$text, [System.Drawing.Color]$color, [string]$tip) {
    $key = "$text|$($color.ToArgb())"
    if ($slot.Key -ne $key) {
        $h = New-TextIcon $text $color
        $slot.Icon.Icon = [System.Drawing.Icon]::FromHandle($h)
        if ($slot.HIcon -ne [IntPtr]::Zero) { [void][BatteryTray.Native]::DestroyIcon($slot.HIcon) }
        $slot.HIcon = $h
        $slot.Key   = $key
    }
    if ($tip.Length -gt 63) { $tip = $tip.Substring(0, 62) + '…' }
    $slot.Icon.Text = $tip
    $slot.Icon.Visible = $true
}

# ---------------------------------------------------------------------------
# Состояние и обновление
# ---------------------------------------------------------------------------
$script:Devices     = @()           # последний результат опроса
$script:LowNotified = @{}           # Id -> уже предупредили о низком заряде
$script:Pending     = $null         # активный фоновый опрос
$script:Clock       = [System.Diagnostics.Stopwatch]::new()
$script:FirstRun    = $true

$script:LastTick    = [DateTime]::UtcNow
$script:ResumeAt    = $null         # когда обновиться после выхода из сна

function New-Worker {
    $script:Runspace = [runspacefactory]::CreateRunspace()
    $script:Runspace.Open()
    $script:PS = [PowerShell]::Create()
    $script:PS.Runspace = $script:Runspace
}
New-Worker

function Request-Refresh {
    if ($script:Pending) { return }
    try {
        $script:PS.Commands.Clear()
        $script:PS.Streams.Error.Clear()
        [void]$script:PS.AddScript($QueryScript)
        $script:Pending = $script:PS.BeginInvoke()
    } catch {
        $script:Pending = $null
    }
    $script:Clock.Restart()
}

function Complete-Refresh {
    $result = @()
    try {
        $result = @($script:PS.EndInvoke($script:Pending) | Where-Object { $_ -ne $null })
    } catch {}
    $script:Pending = $null
    $script:Devices = $result
    Update-Tray
    $script:FirstRun = $false
}

function Update-Tray {
    $visible = @($script:Devices | Where-Object { $_.Connected -and ($script:Settings.Hidden -notcontains $_.Id) })

    # предупреждение о низком заряде — один раз при переходе через порог
    $th = $script:Settings.LowThreshold
    foreach ($d in $script:Devices) {
        $low = ($th -gt 0 -and $d.Connected -and $d.Percent -le $th)
        if ($low -and -not $script:LowNotified[$d.Id] -and -not $script:FirstRun) {
            $script:Slots[0].Icon.Visible = $true
            $script:Slots[0].Icon.ShowBalloonTip(8000, 'Низкий заряд', "$($d.Name): $($d.Percent)%", [System.Windows.Forms.ToolTipIcon]::Warning)
        }
        if ($low -or $script:FirstRun) { $script:LowNotified[$d.Id] = $low }
        elseif ($d.Percent -gt $th + 2) { $script:LowNotified[$d.Id] = $false }   # небольшой гистерезис
    }

    for ($i = 0; $i -lt $script:Slots.Count; $i++) {
        $slot = $script:Slots[$i]
        if ($i -lt $visible.Count) {
            $d = $visible[$i]
            $slot.DeviceId = $d.Id
            Set-SlotIcon $slot ([string]$d.Percent) (Get-LevelColor $d.Percent) "$($d.Name): $($d.Percent)%"
        } elseif ($i -eq 0) {
            # нечего показывать — оставляем одну серую иконку, чтобы было меню
            $slot.DeviceId = $null
            $tip = if ($script:FirstRun -and -not $script:Devices) { 'BatteryTray: поиск устройств…' } else { 'BatteryTray: нет подключённых устройств' }
            Set-SlotIcon $slot '–' (Get-IdleColor) $tip
        } else {
            $slot.DeviceId = $null
            $slot.Icon.Visible = $false
        }
    }
}

# ---------------------------------------------------------------------------
# Меню
# ---------------------------------------------------------------------------
function Add-MenuItem($items, [string]$text, [scriptblock]$onClick, [bool]$checked = $false, [object]$tag = $null) {
    $it = New-Object System.Windows.Forms.ToolStripMenuItem($text)
    $it.Checked = $checked
    $it.Tag = $tag
    if ($onClick) { $it.add_Click($onClick) }
    [void]$items.Add($it)
    return $it
}

function Build-Menu {
    $m = $script:Menu
    $m.Items.Clear()

    $title = New-Object System.Windows.Forms.ToolStripMenuItem('BatteryTray')
    $title.Enabled = $false
    [void]$m.Items.Add($title)

    if (-not $script:Devices.Count) {
        $none = New-Object System.Windows.Forms.ToolStripMenuItem('Устройства с данными о заряде не найдены')
        $none.Enabled = $false
        [void]$m.Items.Add($none)
    } else {
        foreach ($d in ($script:Devices | Sort-Object @{ e = { -not $_.Connected } }, Name)) {
            $state = if ($d.Connected) { "$($d.Percent)%" } else { 'не подключено' }
            $shown = $script:Settings.Hidden -notcontains $d.Id
            $it = Add-MenuItem $m.Items "$($d.Name) — $state" {
                $id = $this.Tag
                if ($script:Settings.Hidden -contains $id) {
                    $script:Settings.Hidden = @($script:Settings.Hidden | Where-Object { $_ -ne $id })
                } else {
                    $script:Settings.Hidden = @($script:Settings.Hidden) + $id
                }
                Save-Settings
                Update-Tray
            } $shown $d.Id
            $it.ToolTipText = 'Галочка — показывать иконку этого устройства в трее'
            if (-not $d.Connected) { $it.ForeColor = [System.Drawing.SystemColors]::GrayText }
        }
    }

    [void]$m.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))

    [void](Add-MenuItem $m.Items 'Обновить сейчас' { Request-Refresh })

    $intervalMenu = New-Object System.Windows.Forms.ToolStripMenuItem('Обновлять каждые')
    foreach ($opt in @(@(15, '15 секунд'), @(30, '30 секунд'), @(60, '1 минуту'), @(300, '5 минут'))) {
        [void](Add-MenuItem $intervalMenu.DropDownItems $opt[1] {
            $script:Settings.IntervalSec = [int]$this.Tag
            Save-Settings
        } ($script:Settings.IntervalSec -eq $opt[0]) $opt[0])
    }
    [void]$m.Items.Add($intervalMenu)

    $lowMenu = New-Object System.Windows.Forms.ToolStripMenuItem('Предупреждать о низком заряде')
    foreach ($opt in @(@(0, 'Не предупреждать'), @(10, 'При 10% и ниже'), @(20, 'При 20% и ниже'), @(30, 'При 30% и ниже'))) {
        [void](Add-MenuItem $lowMenu.DropDownItems $opt[1] {
            $script:Settings.LowThreshold = [int]$this.Tag
            Save-Settings
            $script:LowNotified = @{}
            foreach ($d in $script:Devices) {
                $script:LowNotified[$d.Id] = ($script:Settings.LowThreshold -gt 0 -and $d.Percent -le $script:Settings.LowThreshold)
            }
            Update-Tray
        } ($script:Settings.LowThreshold -eq $opt[0]) $opt[0])
    }
    [void]$m.Items.Add($lowMenu)

    [void](Add-MenuItem $m.Items 'Запускать вместе с Windows' {
        try { Set-Autostart (-not (Test-Autostart)) }
        catch {
            [System.Windows.Forms.MessageBox]::Show("Не удалось изменить автозапуск:`n$($_.Exception.Message)", 'BatteryTray') | Out-Null
        }
    } (Test-Autostart))

    [void]$m.Items.Add((New-Object System.Windows.Forms.ToolStripSeparator))
    [void](Add-MenuItem $m.Items 'Выход' { Exit-App })
}

$script:Menu.add_Opening({ Build-Menu })
Build-Menu

# ---------------------------------------------------------------------------
# Цикл
# ---------------------------------------------------------------------------
$script:App = New-Object System.Windows.Forms.ApplicationContext

function Exit-App {
    $script:Timer.Stop()
    foreach ($s in $script:Slots) {
        $s.Icon.Visible = $false
        $s.Icon.Dispose()
        if ($s.HIcon -ne [IntPtr]::Zero) { [void][BatteryTray.Native]::DestroyIcon($s.HIcon) }
    }
    try { $script:PS.Stop() } catch {}
    $script:PS.Dispose()
    $script:Runspace.Dispose()
    $script:App.ExitThread()
}

$script:Timer = New-Object System.Windows.Forms.Timer
$script:Timer.Interval = 500
$script:Timer.add_Tick({
    try {
        # Большой разрыв между тиками = компьютер спал. Устройствам нужно время,
        # чтобы переподключиться, поэтому обновляемся через 10 секунд.
        $now = [DateTime]::UtcNow
        if (($now - $script:LastTick).TotalSeconds -gt 20) { $script:ResumeAt = $now.AddSeconds(10) }
        $script:LastTick = $now

        if ($script:Pending) {
            if ($script:Pending.IsCompleted) { Complete-Refresh }
            elseif ($script:Clock.Elapsed.TotalSeconds -gt 120) {
                # опрос завис (бывает при сбоях драйвера Bluetooth) — бросаем его и создаём новый
                $old = $script:PS
                try { [void]$old.BeginStop($null, $null) } catch {}
                $script:Pending = $null
                New-Worker
                Request-Refresh
            }
        } elseif ($script:ResumeAt -and $now -ge $script:ResumeAt) {
            $script:ResumeAt = $null
            Request-Refresh
        } elseif ($script:Clock.Elapsed.TotalSeconds -ge $script:Settings.IntervalSec) {
            Request-Refresh
        }
    } catch {}
})

Update-Tray          # серая иконка, пока идёт первый опрос
Request-Refresh
$script:Timer.Start()

[System.Windows.Forms.Application]::Run($script:App)
$script:Mutex.ReleaseMutex()
