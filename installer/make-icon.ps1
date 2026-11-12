# Рисует иконку приложения (снежинка на синем круге) и сохраняет её как assets\HNY.ico.
Add-Type -AssemblyName System.Drawing

$sizes = 256, 64, 48, 32, 24, 16
$pngs = @()
foreach ($n in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $rect = New-Object System.Drawing.RectangleF ($n * 0.03), ($n * 0.03), ($n * 0.94), ($n * 0.94)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 70, 140, 235)), ([System.Drawing.Color]::FromArgb(255, 24, 62, 150)), 60
    $g.FillEllipse($bg, $rect)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.4, $n * 0.07))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $c = $n / 2; $len = $n * 0.33; $twig = $n * 0.115
    for ($i = 0; $i -lt 6; $i++) {
        $a = [Math]::PI / 3 * $i + [Math]::PI / 6
        $dx = [Math]::Cos($a); $dy = [Math]::Sin($a)
        $g.DrawLine($pen, [single]$c, [single]$c, [single]($c + $dx * $len), [single]($c + $dy * $len))
        if ($n -ge 24) {
            $mx = $c + $dx * $len * 0.62; $my = $c + $dy * $len * 0.62
            foreach ($o in 0.9, -0.9) {
                $g.DrawLine($pen, [single]$mx, [single]$my, [single]($mx + [Math]::Cos($a + $o) * $twig), [single]($my + [Math]::Sin($a + $o) * $twig))
            }
        }
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

$out = Join-Path $PSScriptRoot '..\assets\HNY.ico'
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]
    $dim = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"icon written: $out"
