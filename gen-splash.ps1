Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
function New-Splash($path, $bg, $fg, $muted) {
    $w = 1600; $h = 1000
    $dv = New-Object System.Windows.Media.DrawingVisual
    $ctx = $dv.RenderOpen()
    $brush = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($bg))
    $ctx.DrawRectangle($brush, $null, (New-Object System.Windows.Rect 0, 0, $w, $h))
    $tf = New-Object System.Windows.Media.Typeface('Segoe UI')
    $f1 = New-Object System.Windows.Media.FormattedText 'zapret', ([System.Globalization.CultureInfo]::CurrentCulture), ([System.Windows.FlowDirection]::LeftToRight), $tf, 44, (New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($fg))), 1.0
    $f2 = New-Object System.Windows.Media.FormattedText 'loading...', ([System.Globalization.CultureInfo]::CurrentCulture), ([System.Windows.FlowDirection]::LeftToRight), $tf, 15, (New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.ColorConverter]::ConvertFromString($muted))), 1.0
    $ctx.DrawText($f1, (New-Object System.Windows.Point (($w - $f1.Width) / 2), (($h - $f1.Height) / 2 - 12)))
    $ctx.DrawText($f2, (New-Object System.Windows.Point (($w - $f2.Width) / 2), (($h + $f1.Height) / 2 + 12)))
    $ctx.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $w, $h, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $fs = [System.IO.File]::Create($path)
    $enc.Save($fs); $fs.Close()
    Write-Host "$path ok"
}
New-Splash (Join-Path $PSScriptRoot 'splash-dark.png') '#202020' '#F3F3F3' '#ABABAB'
New-Splash (Join-Path $PSScriptRoot 'splash-light.png') '#F3F3F3' '#1A1A1A' '#616161'
