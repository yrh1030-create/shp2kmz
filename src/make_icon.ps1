# Generates app.ico (multi-size, PNG-compressed entries)
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'app.ico'
$sizes = 256, 48, 32, 16
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $r = [single]($s * 0.06)
    $rect = New-Object System.Drawing.RectangleF $r, $r, ($s - 2 * $r), ($s - 2 * $r)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 120, 200))), $rect)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(150, 255, 255, 255)), ([single]([Math]::Max(1, $s / 40)))
    $g.DrawEllipse($pen, ($s * 0.32), $r, ($s * 0.36), ($s - 2 * $r))
    $g.DrawLine($pen, $r, ($s / 2), ($s - $r), ($s / 2))
    # yellow polygon
    $pts = @(
        (New-Object System.Drawing.PointF ($s * 0.30), ($s * 0.28)),
        (New-Object System.Drawing.PointF ($s * 0.72), ($s * 0.36)),
        (New-Object System.Drawing.PointF ($s * 0.66), ($s * 0.70)),
        (New-Object System.Drawing.PointF ($s * 0.40), ($s * 0.76)),
        (New-Object System.Drawing.PointF ($s * 0.24), ($s * 0.52))
    )
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 220, 0))), [System.Drawing.PointF[]]$pts)
    $g.DrawPolygon((New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 220, 0)), ([single]([Math]::Max(1, $s / 22)))), [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Output "icon -> $out"
