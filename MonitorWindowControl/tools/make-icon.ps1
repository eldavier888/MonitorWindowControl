# Generates app.ico (a monitor with a small house) at 16/24/32/48/64/256 px.
# Only needed if you want to change the icon; app.ico is already included.
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\app.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 32.0
    # rounded blue tile
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = [single](7 * $k); $d = $r * 2; $w = [single]($s - 1)
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0, 103, 192))), $path)
    # monitor
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.2, 2.2 * $k))
    $g.DrawRectangle($pen, [single](6 * $k), [single](7 * $k), [single](20 * $k), [single](14 * $k))
    $g.DrawLine($pen, [single](12 * $k), [single](25 * $k), [single](20 * $k), [single](25 * $k))
    $g.DrawLine($pen, [single](16 * $k), [single](21 * $k), [single](16 * $k), [single](25 * $k))
    # house
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $roof = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([single](11 * $k)), ([single](14.5 * $k))),
        (New-Object System.Drawing.PointF ([single](16 * $k)), ([single](10 * $k))),
        (New-Object System.Drawing.PointF ([single](21 * $k)), ([single](14.5 * $k))))
    $g.FillPolygon($white, $roof)
    $g.FillRectangle($white, [single](12.5 * $k), [single](14 * $k), [single](7 * $k), [single](4.5 * $k))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Wrote $out"
