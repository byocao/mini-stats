# Renders the README images in docs/images using MiniStats' own renderer (MiniStats.exe --render).
# Usage:  build.cmd  then  powershell -ExecutionPolicy Bypass -File tools\make-images.ps1
param([int]$Scale = 2)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$exe = Join-Path $root 'bin\MiniStats.exe'
$outDir = Join-Path $root 'docs\images'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('ministats-img-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $outDir, $tmp | Out-Null
if (-not (Test-Path $exe)) { throw "Build first: $exe not found" }

# ---------------------------------------------------------------- helpers
function Render([string]$name, [string]$ini, [string]$sample = '', [int]$scale = $Scale) {
    $cfg = Join-Path $tmp "$name.ini"; $png = Join-Path $tmp "$name.png"
    Set-Content -Path $cfg -Value $ini -Encoding ASCII
    $a = @('--render', $png, '--config', $cfg, '--scale', $scale)
    if ($sample) { $a += @('--sample', $sample) }
    Start-Process -FilePath $exe -ArgumentList $a -Wait -NoNewWindow
    return [System.Drawing.Image]::FromFile($png)
}

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function C([string]$hex, [int]$a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); return [System.Drawing.Color]::FromArgb($a, $c) }

function NewCanvas([int]$w, [int]$h) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    return @($bmp, $g)
}

function Backdrop($g, [int]$w, [int]$h) {
    $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C '#0F1226'), (C '#2A1240'), 35
    $g.FillRectangle($lg, $rect)
    foreach ($b in @(@(0.18, 0.30, 0.55, '#3B5BDB', 70), @(0.80, 0.25, 0.45, '#C2255C', 55), @(0.62, 0.95, 0.60, '#0CA678', 40))) {
        $cx = $w * $b[0]; $cy = $h * $b[1]; $r = $w * $b[2] / 2
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)
        $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $path
        $pg.CenterColor = C $b[3] $b[4]; $pg.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        $g.FillPath($pg, $path)
    }
}

function Shadow($g, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    for ($i = 12; $i -ge 1; $i--) {
        $p = RoundRect ($x - $i) ($y - $i + 6) ($w + 2 * $i) ($h + 2 * $i) ($r + $i)
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(6, 0, 0, 0))), $p)
    }
}

function Text($g, [string]$s, [string]$font, [float]$size, [string]$style, [string]$color, [float]$x, [float]$y) {
    $f = New-Object System.Drawing.Font $font, $size, ([System.Drawing.FontStyle]$style), ([System.Drawing.GraphicsUnit]::Pixel)
    $g.DrawString($s, $f, (New-Object System.Drawing.SolidBrush (C $color)), $x, $y)
    $sz = $g.MeasureString($s, $f); $f.Dispose(); return $sz
}

$base = @"
font=Segoe UI
font_size=10
opacity=0.92
corner_radius=8
"@

# ---------------------------------------------------------------- 1. hero
$W = 1280; $H = 640
$bmp, $g = NewCanvas $W $H
Backdrop $g $W $H
$null = Text $g 'MiniStats' 'Segoe UI Semibold' 92 'Regular' '#FFFFFF' 72 120
$null = Text $g 'A tiny FPS / CPU / RAM / GPU overlay for Windows' 'Segoe UI' 34 'Regular' '#C9CCE6' 78 238
$chips = @('~180 KB single .exe', 'No install, no runtime', '~0.5% CPU', 'Click-through')
$cx = 80
foreach ($c in $chips) {
    $f = New-Object System.Drawing.Font 'Segoe UI Semibold', 20, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sz = $g.MeasureString($c, $f); $cw = $sz.Width + 28
    $g.FillPath((New-Object System.Drawing.SolidBrush (C '#FFFFFF' 26)), (RoundRect $cx 312 $cw 40 20))
    $g.DrawPath((New-Object System.Drawing.Pen (C '#FFFFFF' 60), 1.2), (RoundRect $cx 312 $cw 40 20))
    $g.DrawString($c, $f, (New-Object System.Drawing.SolidBrush (C '#E9EAF6')), ($cx + 14), 319)
    $cx += $cw + 12
}
# faux taskbar + overlay docked bottom-right
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#0B0C16' 170)), 0, ($H - 64), $W, 64)
$g.FillRectangle((New-Object System.Drawing.SolidBrush (C '#FFFFFF' 30)), 0, ($H - 64), $W, 1)
$clockF = New-Object System.Drawing.Font 'Segoe UI', 17, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$g.DrawString('10:18 PM', $clockF, (New-Object System.Drawing.SolidBrush (C '#E9EAF6')), ($W - 120), ($H - 54))
$g.DrawString('11/5/2026', $clockF, (New-Object System.Drawing.SolidBrush (C '#B9BBD0')), ($W - 124), ($H - 32))
for ($i = 0; $i -lt 5; $i++) { $g.FillPath((New-Object System.Drawing.SolidBrush (C '#FFFFFF' (35 + 10 * ($i -eq 2)))), (RoundRect (($W / 2) - 140 + $i * 58) ($H - 52) 40 40 9)) }
$ov = Render 'hero' ($base + "`nitems=fps,cpu,ram,gpu") 'fps=144,cpu=23,ram=61,gpu=87'
$ox = $W - $ov.Width - 20; $oy = $H - 64 - $ov.Height - 20
Shadow $g $ox $oy $ov.Width $ov.Height 16
$g.DrawImage($ov, $ox, $oy, $ov.Width, $ov.Height)
$g.Dispose(); $bmp.Save((Join-Path $outDir 'hero.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $ov.Dispose()

# ---------------------------------------------------------------- 2. layouts
$layouts = @(
    @('Default', 'items=fps,cpu,ram,gpu', 'fps=144,cpu=23,ram=61,gpu=87'),
    @('Everything', 'items=fps,cpu,ram,gpu,vram,gputemp', 'fps=165,cpu=31,ram=58,gpu=94,vram=7.2,temp=68'),
    @('Memory in GB', 'items=fps,cpu,ramgb,gpu', 'fps=60,cpu=12,ram=61,ramused=19.6,gpu=41'),
    @('Just FPS', 'items=fps', 'fps=240')
)
$imgs = @(); foreach ($l in $layouts) { $imgs += , (Render ('layout-' + $l[0].Replace(' ', '')) ($base + "`n" + $l[1]) $l[2]) }
function Showcase([string]$title, [string]$file, $rows, $images) {
    $colX = 460; $rowH = 112
    $maxW = ($images | Measure-Object -Property Width -Maximum).Maximum
    $W = [math]::Max(1280, $colX + $maxW + 60); $H = 84 + $rowH * $rows.Count
    $bmp, $g = NewCanvas $W $H
    Backdrop $g $W $H
    $null = Text $g $title 'Segoe UI Semibold' 30 'Regular' '#FFFFFF' 56 22
    for ($i = 0; $i -lt $rows.Count; $i++) {
        $y = 80 + $i * $rowH; $img = $images[$i]
        $null = Text $g $rows[$i][0] 'Segoe UI Semibold' 22 'Regular' '#E9EAF6' 60 ($y + 8)
        if ($rows[$i][1]) { $null = Text $g $rows[$i][1] 'Consolas' 17 'Regular' '#9EA3C8' 60 ($y + 42) }
        $iy = $y + 10
        Shadow $g $colX $iy $img.Width $img.Height 16
        $g.DrawImage($img, $colX, $iy, $img.Width, $img.Height)
    }
    $g.Dispose(); $bmp.Save((Join-Path $outDir $file), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
Showcase 'Layouts' 'layouts.png' $layouts $imgs
$imgs | % { $_.Dispose() }

# ---------------------------------------------------------------- 3. themes
$themes = @(
    @('Midnight (default)', @"
background=#18181C
color_label=#9696A2
color_fps=#FFDE66
color_cpu=#7CC4FF
color_ram=#8BE28B
color_gpu=#FFB066
"@),
    @('Daylight', @"
background=#F6F7F9
color_label=#6E7781
color_fps=#B7791F
color_cpu=#1F6FEB
color_ram=#1A7F37
color_gpu=#D9480F
"@),
    @('Nord', @"
background=#2E3440
color_label=#81A1C1
color_fps=#EBCB8B
color_cpu=#88C0D0
color_ram=#A3BE8C
color_gpu=#D08770
"@),
    @('Mono', @"
background=#000000
opacity=0.75
color_label=#8A8A8A
color_fps=#FFFFFF
color_cpu=#FFFFFF
color_ram=#FFFFFF
color_gpu=#FFFFFF
"@)
)
$imgs = @(); $i = 0
foreach ($t in $themes) { $imgs += , (Render ('theme' + $i) ($base + "`nitems=fps,cpu,ram,gpu`n" + $t[1]) 'fps=144,cpu=23,ram=61,gpu=87'); $i++ }
$rows = @(); foreach ($t in $themes) { $rows += , @($t[0], '') }
Showcase 'Themes' 'themes.png' $rows $imgs
$imgs | % { $_.Dispose() }

# ---------------------------------------------------------------- 4. settings window
# MiniStats.exe --shot-settings renders each page of the real settings window (with sample data)
$shots = Join-Path $tmp 'shots'
Start-Process -FilePath $exe -ArgumentList @('--shot-settings', $shots) -Wait -NoNewWindow

function Framed($g, $img, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    Shadow $g $x $y $w $h $r
    $brush = New-Object System.Drawing.TextureBrush $img
    $brush.WrapMode = 'Clamp'
    $brush.ScaleTransform($w / $img.Width, $h / $img.Height, 'Append')
    $brush.TranslateTransform($x, $y, 'Append')
    $g.FillPath($brush, (RoundRect $x $y $w $h $r))
    $g.DrawPath((New-Object System.Drawing.Pen (C '#FFFFFF' 28), 1), (RoundRect $x $y $w $h $r))
}

# 4a. one page, large
$page = [System.Drawing.Image]::FromFile((Join-Path $shots 'settings-1.png'))
$pw = 1100.0; $ph = $page.Height * $pw / $page.Width
$W = 1280; $H = [int]($ph + 120)
$bmp, $g = NewCanvas $W $H
Backdrop $g $W $H
Framed $g $page (($W - $pw) / 2) 60 $pw $ph 14
$g.Dispose(); $bmp.Save((Join-Path $outDir 'settings.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $page.Dispose()

# 4b. all four pages as a 2 x 2 tour
$pages = @(); for ($i = 0; $i -lt 4; $i++) { $pages += , [System.Drawing.Image]::FromFile((Join-Path $shots "settings-$i.png")) }
$cw = 600.0; $gap = 40; $W = [int](2 * $cw + 3 * $gap)
$rowsH = @(); for ($r = 0; $r -lt 2; $r++) { $rowsH += [math]::Max($pages[2 * $r].Height, $pages[2 * $r + 1].Height) * $cw / $pages[0].Width }
$H = [int]($rowsH[0] + $rowsH[1] + 3 * $gap)
$bmp, $g = NewCanvas $W $H
Backdrop $g $W $H
for ($i = 0; $i -lt 4; $i++)
{
    $col = $i % 2; $row = [math]::Floor($i / 2)
    $p = $pages[$i]; $h = $p.Height * $cw / $p.Width
    $x = $gap + $col * ($cw + $gap); $y = $gap + ($(if ($row -eq 0) { 0 } else { $rowsH[0] + $gap }))
    Framed $g $p $x $y $cw $h 10
}
$g.Dispose(); $bmp.Save((Join-Path $outDir 'settings-tour.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose(); $pages | % { $_.Dispose() }

Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
Get-ChildItem $outDir | Select-Object Name, Length
