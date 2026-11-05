# Generates the MiniStats icons:
#   assets/icon.ico  - application icon (16-64 px DIB + 256 px PNG)
#   assets/tray.ico  - small notification-area icon embedded as a resource (16-40 px DIB)
#   docs/images/icon.png - 256 px PNG for the README
Add-Type -AssemblyName System.Drawing
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 24, 28))), (New-RoundRect 0 0 $s $s ([math]::Max(2, $s * 0.22))))
    $colors = @([System.Drawing.Color]::FromArgb(124, 196, 255), [System.Drawing.Color]::FromArgb(139, 226, 139), [System.Drawing.Color]::FromArgb(255, 176, 102))
    $heights = 0.26, 0.42, 0.56
    $pad = $s * 0.18; $gap = $s * 0.07
    $bw = ($s - 2 * $pad - 2 * $gap) / 3
    $base = $s - $pad
    for ($i = 0; $i -lt 3; $i++) {
        $h = $s * $heights[$i]; $x = $pad + $i * ($bw + $gap)
        $g.FillPath((New-Object System.Drawing.SolidBrush $colors[$i]), (New-RoundRect $x ($base - $h) $bw $h ([math]::Max(0.5, $bw * 0.3))))
    }
    # FPS accent dot above the tallest bar
    $dr = [math]::Max(1.5, $s * 0.075)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 222, 102))), ($pad + 2 * ($bw + $gap) + $bw / 2 - $dr), ($base - $s * 0.56 - $dr * 2.4), 2 * $dr, 2 * $dr)
    $g.Dispose()
    return $bmp
}

# 32-bit DIB entry: BITMAPINFOHEADER + bottom-up BGRA pixels + 1-bit AND mask
function Get-DibBytes($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int]([math]::Ceiling($s / 32.0) * 4)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4 + $maskStride * $s)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) } }
    $w.Write((New-Object byte[] ($maskStride * $s)))
    $w.Flush(); return , $ms.ToArray()
}

function Write-Ico([int[]]$sizes, [string]$out) {
    $entries = @()
    foreach ($s in $sizes) {
        $bmp = New-IconBitmap $s
        if ($s -ge 256) {
            $ms = New-Object System.IO.MemoryStream
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            $entries += , $ms.ToArray()
        } else { $entries += , (Get-DibBytes $bmp) }
        $bmp.Dispose()
    }
    $fs = [System.IO.File]::Create($out); $bw = New-Object System.IO.BinaryWriter $fs
    $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $entries[$i].Length; $d = $(if ($s -ge 256) { 0 } else { $s })
        $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
    }
    foreach ($e in $entries) { $bw.Write($e) }
    $bw.Close()
    "wrote $out ($((Get-Item $out).Length) bytes)"
}

Write-Ico @(16, 24, 32, 48, 64, 256) (Join-Path $root 'assets\icon.ico')
Write-Ico @(16, 20, 24, 32, 40) (Join-Path $root 'assets\tray.ico')
$png = New-IconBitmap 256
$png.Save((Join-Path $root 'docs\images\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png); $png.Dispose()
"wrote docs\images\icon.png"
