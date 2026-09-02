# gen-ui-bmps.ps1 - regenerate the branded MSI wizard bitmaps.
#
# 2026-09-01 (owner decree «не используй SQL — дефолтные положения текста,
# картинку в область для лого»): ALL branding lives in these bitmaps; the
# wizard ships stock WixUI geometry, the post-build SQL patch is retired
# (installer\_archive\patch-welcome-page.ps1). The dialog bitmap keeps the
# stock text zone (X >= 180 px) pure white — see the dialog section below.
#
# 2026-08-31 (owner: «не нравится подход [статичной таблички] - полностью
# переехать на большой GUI ... логотип надо перенести»): the Burservis logo
# moves INTO the payload MSI's wizard. Stock WixUI pages are branded via the
# WixUIBannerBmp / WixUIDialogBmp WixVariables (Product.wxs); the two custom
# dialogs (IcemRunningCheckDlg, OptionsDlg) carry a BannerBitmap control
# referencing the same WixUI_Bmp_Banner.
#
# Layout law: the banner keeps its LEFT third white - the custom dialogs
# place their Title text at X=15 Y=6 ON the banner area, and the stock
# dialogs put their title there too. The wordmark goes to the RIGHT side,
# small (height 16 px at 493x58) so it never collides with the title text.
#
# MSI bitmap controls require uncompressed BI_RGB bitmaps; 24bpp RGB is the
# documented-safe format (8bpp banding, 32bpp alpha quirks) - so all output
# is PixelFormat24bppRgb.
#
# PowerShell 5.1 compatible (System.Drawing; no &&, no ternary).

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$AssetsDir = Join-Path $PSScriptRoot 'assets'
$LogoPath = Join-Path $AssetsDir 'Burservis_logo_cyr_red.png'
if (-not (Test-Path -LiteralPath $LogoPath)) { throw "logo source missing: $LogoPath" }

$logo = [System.Drawing.Image]::FromFile($LogoPath)
try {
    $ratio = $logo.Width / $logo.Height   # 1293x101 => ~12.80:1

    # ------------------------------------------------------------- banner ---
    # 493x58 (WixUI_Bmp_Banner stock geometry). Logo right-aligned, h=16.
    $banner = New-Object System.Drawing.Bitmap 493, 58, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($banner)
    $g.Clear([System.Drawing.Color]::White)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $lh = 16
    $lw = [int]($lh * $ratio)
    $g.DrawImage($logo, (493 - $lw - 12), [int]((58 - $lh) / 2), $lw, $lh)
    $g.Dispose()
    $banner.Save((Join-Path $AssetsDir 'ui-banner.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $banner.Dispose()

    # ------------------------------------------------------------- dialog ---
    # 493x312 (WixUI_Bmp_Dialog stock geometry, the big Welcome/Finish
    # picture). 2026-09-01 owner decree («все равно костыли — не используй
    # SQL, дефолтные положения текста, картинку в область для лого»):
    # the bitmap is painted AROUND the STOCK WixUI control geometry —
    # no post-build Control-table patching, no baked-in text:
    #   * stock text zone (Title 135,20,220x60 / Description 135,70,220x60
    #     dialog units = px x 4/3) occupies X 180..473 px, Y 27..173 px on
    #     ALL THREE full-bitmap pages (WelcomeDlg, MaintenanceWelcomeDlg,
    #     ExitDialog) — that area stays PURE WHITE;
    #   * the left art band (X 0..~168 px, like the stock WiX bitmap's
    #     maroon panel) carries the branding: light-gray panel + thin red
    #     divider + the БУРСЕРВИС wordmark with its red rule, centered.
    $dialog = New-Object System.Drawing.Bitmap 493, 312, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($dialog)
    $g.Clear([System.Drawing.Color]::White)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    # Left art panel: #F5F5F5, full height, 168 px wide; 2 px brand-red
    # divider on its right edge (text zone starts at X=180 px — 9 px air).
    $panelBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(245, 245, 245))
    $g.FillRectangle($panelBrush, 0, 0, 168, 312)
    $panelBrush.Dispose()
    $dividerBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 16, 46))
    $g.FillRectangle($dividerBrush, 168, 0, 2, 312)
    $dividerBrush.Dispose()

    # Wordmark centered in the panel (slightly above the vertical middle);
    # thin red rule beneath it, spanning the logo width (brand echo).
    $lw = 150
    $lh = [int]($lw / $ratio)
    $lx = [int]((168 - $lw) / 2)
    $ly = 132
    $g.DrawImage($logo, $lx, $ly, $lw, $lh)
    $rulePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 16, 46)), 2
    $g.DrawLine($rulePen, $lx, ($ly + $lh + 10), ($lx + $lw), ($ly + $lh + 10))
    $rulePen.Dispose()
    $g.Dispose()
    $dialog.Save((Join-Path $AssetsDir 'ui-dialog.bmp'), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $dialog.Dispose()
} finally {
    $logo.Dispose()
}

Write-Host 'UI bitmaps written: assets\ui-banner.bmp (493x58), assets\ui-dialog.bmp (493x312)'
