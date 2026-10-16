[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskConcepts = Get-Content (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
$taskBoard = [System.Drawing.Bitmap]::new(1920, 1030)
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBoard)
$taskTitleFont = [System.Drawing.Font]::new('Segoe UI', 25, [System.Drawing.FontStyle]::Bold)
$taskLabelFont = [System.Drawing.Font]::new('Segoe UI', 15, [System.Drawing.FontStyle]::Bold)
$taskSmallFont = [System.Drawing.Font]::new('Segoe UI', 11)
$taskInk = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#172332'))
$taskMuted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#536275'))
$taskDark = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#20242c'))
try {
    $taskGraphics.Clear([System.Drawing.ColorTranslator]::FromHtml('#eef1f5'))
    $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $taskGraphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $taskGraphics.DrawString('Preset Forwarder - 10 logo concepts', $taskTitleFont, $taskInk, 30, 20)
    $taskGraphics.DrawString('Each concept shown on light and dark backgrounds. Bottom row: actual 16 / 32 / 48 pixel previews.', $taskSmallFont, $taskMuted, 32, 68)
    for ($taskIndex = 0; $taskIndex -lt $taskConcepts.Count; $taskIndex++) {
        $taskConcept = $taskConcepts[$taskIndex]
        $taskX = 24 + ($taskIndex % 5) * 376
        $taskY = 110 + [math]::Floor($taskIndex / 5) * 424
        $taskGraphics.FillRectangle([System.Drawing.Brushes]::White, $taskX, $taskY, 360, 405)
        $taskGraphics.DrawString(('{0}. {1}' -f $taskConcept.n, $taskConcept.name), $taskLabelFont, $taskInk, ($taskX+16), ($taskY+14))
        $taskGraphics.FillRectangle($taskDark, ($taskX+184), ($taskY+59), 160, 230)
        $taskImage = [System.Drawing.Bitmap]::new((Join-Path $PSScriptRoot $taskConcept.filename))
        try {
            $taskGraphics.DrawImage($taskImage, [System.Drawing.Rectangle]::new(($taskX+12), ($taskY+84), 160, 160))
            $taskGraphics.DrawImage($taskImage, [System.Drawing.Rectangle]::new(($taskX+184), ($taskY+84), 160, 160))
            $taskGraphics.DrawString('LIGHT', $taskSmallFont, $taskMuted, ($taskX+24), ($taskY+258))
            $taskGraphics.DrawString('DARK', $taskSmallFont, [System.Drawing.Brushes]::White, ($taskX+198), ($taskY+258))
            $taskGraphics.FillRectangle($taskDark, ($taskX+184), ($taskY+303), 160, 83)
            $taskOffset = 0
            foreach ($taskSize in @(16, 32, 48)) {
                $taskGraphics.DrawImage($taskImage, [System.Drawing.Rectangle]::new(($taskX+24+$taskOffset), ($taskY+318+(48-$taskSize)), $taskSize, $taskSize))
                $taskGraphics.DrawImage($taskImage, [System.Drawing.Rectangle]::new(($taskX+196+$taskOffset), ($taskY+318+(48-$taskSize)), $taskSize, $taskSize))
                $taskOffset += $taskSize + 16
            }
        } finally { $taskImage.Dispose() }
    }
    $taskGraphics.DrawString('Original concept artwork; final small-size and theme adaptation follows selection.', $taskSmallFont, $taskMuted, 32, 982)
    $taskBoard.Save((Join-Path $PSScriptRoot 'comparison.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $taskGraphics.Dispose()
    $taskBoard.Dispose()
    $taskTitleFont.Dispose()
    $taskLabelFont.Dispose()
    $taskSmallFont.Dispose()
    $taskInk.Dispose()
    $taskMuted.Dispose()
    $taskDark.Dispose()
}
