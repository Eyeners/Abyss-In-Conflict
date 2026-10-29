# Rebuild the transparent 256px sprites from the SVG polylines, with 4x antialiasing.
Add-Type -AssemblyName System.Drawing
[xml]$svg = Get-Content (Join-Path $PSScriptRoot '海啸准星.svg') -Raw -Encoding UTF8
$ns = New-Object System.Xml.XmlNamespaceManager($svg.NameTable)
$ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
$lines = $svg.SelectNodes('//s:g[@id="reticle"]/s:polyline', $ns)
$passes = $svg.SelectNodes('/s:svg/s:use', $ns)
foreach ($pointer in @($false, $true)) {
    $canvas = New-Object System.Drawing.Bitmap(1024, 1024)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(4, 4)
    foreach ($pass in $passes) {
        $rgb = [System.Drawing.ColorTranslator]::FromHtml($pass.GetAttribute('stroke'))
        $alpha = [int](255 * [double]::Parse($pass.GetAttribute('opacity'), [Globalization.CultureInfo]::InvariantCulture))
        $width = [single]::Parse($pass.GetAttribute('stroke-width'), [Globalization.CultureInfo]::InvariantCulture)
        $offset = if ($pass.HasAttribute('x')) { [single]$pass.GetAttribute('x') } else { 0 }
        $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($alpha, $rgb), $width)
        foreach ($line in $lines) {
            if ($pointer -and $line.GetAttribute('class') -ne 'centre') { continue }
            [System.Drawing.PointF[]]$points = @($line.GetAttribute('points').Split(' ') | ForEach-Object {
                $xy = $_.Split(',')
                New-Object System.Drawing.PointF(([single]$xy[0] + $offset), [single]$xy[1])
            })
            $graphics.DrawLines($pen, $points)
        }
        $pen.Dispose()
    }
    $graphics.Dispose()
    $output = New-Object System.Drawing.Bitmap(256, 256)
    $g = [System.Drawing.Graphics]::FromImage($output)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($canvas, 0, 0, 256, 256)
    $name = if ($pointer) { '海啸准星指针.png' } else { '海啸准星.png' }
    $output.Save((Join-Path $PSScriptRoot $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $output.Dispose(); $canvas.Dispose()
}
