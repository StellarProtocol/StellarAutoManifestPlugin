# Placeholder header / launcher glyphs for Auto Manifest: a white rounded diamond on transparent, one bitmap per pixel
# size (house icon rule: pre-drawn per size, never one master scaled down). The real art replaces these files at the
# same names (Resources/icons/automanifest-<px>.png).
#   pwsh -File tools/icons/gen-placeholder-icons.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\..\Resources\icons'
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($px in 12, 16, 18, 24, 32, 40, 64, 128) {
    $bmp = New-Object System.Drawing.Bitmap $px, $px, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    # Diamond inset ~1/12 of the side, corners rounded with a round line join.
    $c = $px / 2.0; $r = $px / 2.0 - [Math]::Max(1.0, [Math]::Floor($px / 12.0 + 0.5))
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $c, ($c - $r)),
        (New-Object System.Drawing.PointF ($c + $r), $c),
        (New-Object System.Drawing.PointF $c, ($c + $r)),
        (New-Object System.Drawing.PointF ($c - $r), $c))
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddPolygon($pts)
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.0, $px / 10.0))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.FillPath($brush, $path)
    $g.DrawPath($pen, $path)
    $g.Dispose()
    $bmp.Save((Join-Path $out "automanifest-$px.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
