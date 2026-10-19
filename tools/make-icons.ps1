# Generates every bitmap asset: the app icon and the Stream Deck plugin images.
# Windows PowerShell 5.1 (uses System.Drawing). Re-run after changing a design here.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function New-Canvas([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)
    return @{ Bmp = $bmp; G = $g; S = [single]$size }
}

function Save-Canvas($c, [string]$path) {
    New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
    $c.Bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $c.G.Dispose(); $c.Bmp.Dispose()
}

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    return $p
}

# Three overlapping RGB circles: the MyRGB mark. $bg draws the dark rounded tile behind it.
function Draw-Mark($c, [bool]$bg) {
    $g = $c.G; $s = $c.S
    if ($bg) {
        $tile = RoundRect ($s * 0.02) ($s * 0.02) ($s * 0.96) ($s * 0.96) ($s * 0.22)
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 28, 30, 44)), ([System.Drawing.Color]::FromArgb(255, 12, 12, 20))
        $g.FillPath($brush, $tile)
    }
    $r = $s * 0.24
    $cx = $s / 2; $cy = $s * 0.53
    $centers = @(
        @(($cx), ($cy - $r * 0.62), [System.Drawing.Color]::FromArgb(200, 255, 40, 80)),
        @(($cx - $r * 0.56), ($cy + $r * 0.36), [System.Drawing.Color]::FromArgb(200, 40, 230, 120)),
        @(($cx + $r * 0.56), ($cy + $r * 0.36), [System.Drawing.Color]::FromArgb(200, 40, 140, 255))
    )
    foreach ($ctr in $centers) {
        $b = New-Object System.Drawing.SolidBrush $ctr[2]
        $g.FillEllipse($b, [single]($ctr[0] - $r), [single]($ctr[1] - $r), [single](2 * $r), [single](2 * $r))
    }
}

function White([int]$alpha = 255) { [System.Drawing.Color]::FromArgb($alpha, 255, 255, 255) }

# Monochrome glyphs for the Stream Deck action list and category (white on transparent, per Elgato guidelines).
function Draw-Glyph($c, [string]$kind, [System.Drawing.Color]$color) {
    $g = $c.G; $s = $c.S
    $pen = New-Object System.Drawing.Pen $color, ([single]($s * 0.09))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $brush = New-Object System.Drawing.SolidBrush $color
    switch ($kind) {
        'preset' {
            $r = $s * 0.2
            foreach ($p in @(@(0.5, 0.33), @(0.33, 0.62), @(0.67, 0.62))) {
                $g.DrawEllipse($pen, [single]($s * $p[0] - $r), [single]($s * $p[1] - $r), [single](2 * $r), [single](2 * $r))
            }
        }
        'power' {
            $g.DrawArc($pen, [single]($s * 0.2), [single]($s * 0.22), [single]($s * 0.6), [single]($s * 0.6), -60, 300)
            $g.DrawLine($pen, [single]($s * 0.5), [single]($s * 0.12), [single]($s * 0.5), [single]($s * 0.5))
        }
        'brightness' {
            $r = $s * 0.16
            $g.FillEllipse($brush, [single]($s / 2 - $r), [single]($s / 2 - $r), [single](2 * $r), [single](2 * $r))
            for ($i = 0; $i -lt 8; $i++) {
                $a = $i * [Math]::PI / 4
                $g.DrawLine($pen, [single]($s / 2 + [Math]::Cos($a) * $s * 0.3), [single]($s / 2 + [Math]::Sin($a) * $s * 0.3),
                                  [single]($s / 2 + [Math]::Cos($a) * $s * 0.42), [single]($s / 2 + [Math]::Sin($a) * $s * 0.42))
            }
        }
        'next' {
            $tri = [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($s * 0.22, $s * 0.2), [System.Drawing.PointF]::new($s * 0.65, $s * 0.5), [System.Drawing.PointF]::new($s * 0.22, $s * 0.8))
            $g.FillPolygon($brush, $tri)
            $g.DrawLine($pen, [single]($s * 0.78), [single]($s * 0.22), [single]($s * 0.78), [single]($s * 0.78))
        }
    }
}

# Key image: dark tile, colored glyph.
function Draw-Key($c, [string]$kind, [System.Drawing.Color]$color) {
    $g = $c.G; $s = $c.S
    $g.Clear([System.Drawing.Color]::FromArgb(255, 16, 16, 24))
    $inner = New-Canvas ([int]($s * 0.5))
    Draw-Glyph $inner $kind $color
    $g.DrawImage($inner.Bmp, [single]($s * 0.25), [single]($s * 0.18), [single]($s * 0.5), [single]($s * 0.5))
    $inner.G.Dispose(); $inner.Bmp.Dispose()
}

# --- App icon --------------------------------------------------------------
$assets = Join-Path $Root 'src\MyRGB.App\Assets'
$c = New-Canvas 256; Draw-Mark $c $true; Save-Canvas $c (Join-Path $assets 'icon.png')

# Multi-size .ico (PNG-compressed entries, supported since Vista).
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $c = New-Canvas $sz; Draw-Mark $c $true
    $ms = New-Object System.IO.MemoryStream
    $c.Bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $c.G.Dispose(); $c.Bmp.Dispose()
    , $ms.ToArray()
}
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $ico.ToArray())

# --- Stream Deck plugin ----------------------------------------------------
$sd = Join-Path $Root 'streamdeck\com.myrgb.streamdeck.sdPlugin\imgs'
foreach ($scale in @(@(1, ''), @(2, '@2x'))) {
    $k = $scale[0]; $suffix = $scale[1]

    $c = New-Canvas (256 * $k); Draw-Mark $c $true; Save-Canvas $c (Join-Path $sd "plugin$suffix.png")
    $c = New-Canvas (28 * $k); Draw-Glyph $c 'preset' (White); Save-Canvas $c (Join-Path $sd "category$suffix.png")

    foreach ($kind in 'preset', 'power', 'brightness', 'next') {
        $c = New-Canvas (20 * $k); Draw-Glyph $c $kind (White); Save-Canvas $c (Join-Path $sd "actions\$kind$suffix.png")
    }

    $keys = @{
        'preset-key'     = @('preset', [System.Drawing.Color]::FromArgb(255, 120, 160, 255))
        'power-on'       = @('power', [System.Drawing.Color]::FromArgb(255, 60, 230, 140))
        'power-off'      = @('power', [System.Drawing.Color]::FromArgb(255, 110, 110, 120))
        'brightness-key' = @('brightness', [System.Drawing.Color]::FromArgb(255, 255, 210, 80))
        'next-key'       = @('next', [System.Drawing.Color]::FromArgb(255, 200, 120, 255))
    }
    foreach ($name in $keys.Keys) {
        $c = New-Canvas (72 * $k); Draw-Key $c $keys[$name][0] $keys[$name][1]; Save-Canvas $c (Join-Path $sd "keys\$name$suffix.png")
    }
}
Write-Output "Icons written under $Root"
