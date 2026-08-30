# make-icon.ps1 — 用 System.Drawing 程序化生成应用图标(无需设计工具,可复现可调参)
#
# 设计:深蓝→青对角渐变圆角方底 + 白色双向转发箭头(上行向右/下行向左),
#       寓意端口转发的进出流量;粗线条保证托盘 16px 下清晰可辨。
#
# 输出:
#   app.ico              — exe 图标 + 托盘图标(16/20/24/32/40/48/64/128/256)
#   wwwroot\favicon.ico  — 网页图标(16/32/48,控制体积)
#
# 用法:powershell -ExecutionPolicy Bypass -File make-icon.ps1 [-PreviewDir 目录]
#       指定 -PreviewDir 时额外输出 PNG 预览(256 原尺寸 + 16/24 放大图)。

param(
    [string]$OutDir = $PSScriptRoot,
    [string]$PreviewDir = ""
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# 圆角矩形路径
function New-RoundedRectPath {
    param([float]$x, [float]$y, [float]$w, [float]$h, [float]$r)
    $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

# 画一对转发箭头(上行向右、下行向左,水平镜像对称);$dy 为垂直偏移(画投影用),坐标按 256 基准乘 $s 缩放
function Draw-ArrowPair {
    param([System.Drawing.Graphics]$g, [float]$s, [System.Drawing.Color]$color, [float]$dy, [float]$penW,
          [float]$yTop, [float]$yBottom, [float]$xLeft, [float]$xRight, [float]$capW, [float]$capH)
    $pen = New-Object System.Drawing.Pen($color, $penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cap = New-Object System.Drawing.Drawing2D.AdjustableArrowCap($capW, $capH, $true)
    $pen.CustomEndCap = $cap
    $g.DrawLine($pen, [float]($xLeft * $s), [float](($yTop + $dy) * $s), [float]($xRight * $s), [float](($yTop + $dy) * $s))
    $g.DrawLine($pen, [float]((256 - $xLeft) * $s), [float](($yBottom + $dy) * $s), [float]((256 - $xRight) * $s), [float](($yBottom + $dy) * $s))
    $cap.Dispose()
    $pen.Dispose()
}

# 按指定尺寸绘制一帧图标位图(全部矢量逻辑按 256 基准等比缩放)
function Draw-IconBitmap {
    param([int]$size)
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $s = $size / 256.0

    # 底:对角渐变圆角方块(铺满画布,小尺寸下色块面积最大化)
    $path = New-RoundedRectPath 0 0 $size $size ([float](58 * $s))
    $c1 = [System.Drawing.Color]::FromArgb(255, 45, 108, 246)
    $c2 = [System.Drawing.Color]::FromArgb(255, 7, 147, 201)
    $p1 = New-Object System.Drawing.PointF(0, 0)
    $p2 = New-Object System.Drawing.PointF($size, $size)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush($p1, $p2, $c1, $c2)
    $g.FillPath($grad, $path)
    $grad.Dispose()

    # 顶部高光(裁剪进圆角内,大尺寸才可感知)
    $g.SetClip($path)
    $hl = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(26, 255, 255, 255))
    $g.FillEllipse($hl, [float](-51 * $s), [float](-128 * $s), [float](358 * $s), [float](218 * $s))
    $hl.Dispose()
    $g.ResetClip()

    # 双向箭头:小尺寸(托盘 16/20px)用专档布局——拉开行距、加粗、加大箭头帽,否则两行粘连发糊
    $white = [System.Drawing.Color]::White
    if ($size -le 20)
    {
        $penW = [float]($size * 0.14)
        Draw-ArrowPair $g $s $white 0 $penW -yTop 88 -yBottom 168 -xLeft 56 -xRight 200 -capW 2.3 -capH 2.0
    }
    else
    {
        $penW = [float](31 * $s)
        if ($size -ge 48)
        {
            # 大尺寸加一层深色投影增加立体感;小尺寸会糊,跳过
            $shadow = [System.Drawing.Color]::FromArgb(70, 11, 44, 102)
            Draw-ArrowPair $g $s $shadow 4 $penW -yTop 99 -yBottom 157 -xLeft 76 -xRight 194 -capW 2.2 -capH 2.0
        }
        Draw-ArrowPair $g $s $white 0 $penW -yTop 99 -yBottom 157 -xLeft 76 -xRight 194 -capW 2.2 -capH 2.0
    }

    $path.Dispose()
    $g.Dispose()
    return $bmp
}

# 位图 → PNG 字节(256 尺寸条目用 PNG 压缩,ico 惯例)
function Get-PngBytes {
    param([System.Drawing.Bitmap]$bmp)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

# 位图 → 经典 BMP 条目字节(BITMAPINFOHEADER + 自底向上 BGRA + 全 0 AND 掩码),小尺寸兼容性最好
function Get-BmpEntryBytes {
    param([System.Drawing.Bitmap]$bmp)
    $size = $bmp.Width
    $andStride = [int]([Math]::Floor(($size + 31) / 32) * 4)
    $xorSize = $size * $size * 4
    $andSize = $andStride * $size

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt32]40)                     # biSize
    $bw.Write([Int32]$size)                   # biWidth
    $bw.Write([Int32]($size * 2))             # biHeight(XOR + AND 两段)
    $bw.Write([UInt16]1)                      # biPlanes
    $bw.Write([UInt16]32)                     # biBitCount
    $bw.Write([UInt32]0)                      # biCompression = BI_RGB
    $bw.Write([UInt32]($xorSize + $andSize))  # biSizeImage
    $bw.Write([Int32]0); $bw.Write([Int32]0)
    $bw.Write([UInt32]0); $bw.Write([UInt32]0)

    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $buf = New-Object byte[] ($stride * $size)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($data)
    for ($y = $size - 1; $y -ge 0; $y--)
    {
        $bw.Write($buf, $y * $stride, $size * 4)
    }
    $bw.Write((New-Object byte[] $andSize))   # AND 掩码全 0,透明交给 alpha 通道

    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose(); $ms.Dispose()
    return ,$bytes
}

# 组装多尺寸 ICO 文件(ICONDIR + ICONDIRENTRY[] + 数据块)
function Write-Ico {
    param([int[]]$sizes, [string]$path)
    $entries = @()
    foreach ($sz in $sizes)
    {
        $bmp = Draw-IconBitmap $sz
        if ($sz -ge 256) { $bytes = Get-PngBytes $bmp } else { $bytes = Get-BmpEntryBytes $bmp }
        $bmp.Dispose()
        $entries += @{ Size = $sz; Bytes = $bytes }
    }

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries)
    {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }   # 256 在目录里记 0
        $bw.Write([Byte]$dim); $bw.Write([Byte]$dim)
        $bw.Write([Byte]0); $bw.Write([Byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$e.Bytes.Length)
        $bw.Write([UInt32]$offset)
        $offset += $e.Bytes.Length
    }
    foreach ($e in $entries) { $bw.Write($e.Bytes) }
    $bw.Flush()
    [System.IO.File]::WriteAllBytes($path, $ms.ToArray())
    $bw.Dispose(); $ms.Dispose()
    Write-Host ("已生成 {0}({1} 个尺寸,{2:N0} 字节)" -f $path, $sizes.Count, (Get-Item $path).Length)
}

Write-Ico -sizes 16, 20, 24, 32, 40, 48, 64, 128, 256 -path (Join-Path $OutDir "app.ico")
Write-Ico -sizes 16, 32, 48 -path (Join-Path $OutDir "wwwroot\favicon.ico")

# 小尺寸帧邻近放大存 PNG,检查像素级效果
function Save-ZoomPreview {
    param([int]$size, [int]$zoomFactor, [string]$dir)
    $small = Draw-IconBitmap $size
    $zoomed = New-Object System.Drawing.Bitmap([int]($size * $zoomFactor), [int]($size * $zoomFactor))
    $zg = [System.Drawing.Graphics]::FromImage($zoomed)
    $zg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
    $zg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
    $zg.DrawImage($small, 0, 0, $size * $zoomFactor, $size * $zoomFactor)
    $zg.Dispose()
    $zoomed.Save((Join-Path $dir ("icon-{0}-zoom.png" -f $size)), [System.Drawing.Imaging.ImageFormat]::Png)
    $zoomed.Dispose(); $small.Dispose()
}

# PNG 预览:256 原图 + 16/24 邻近放大(检查小尺寸像素级效果)
if ($PreviewDir -ne "")
{
    if (-not (Test-Path $PreviewDir)) { New-Item -ItemType Directory -Force $PreviewDir | Out-Null }
    $b256 = Draw-IconBitmap 256
    $b256.Save((Join-Path $PreviewDir "icon-256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $b256.Dispose()
    Save-ZoomPreview -size 16 -zoomFactor 8 -dir $PreviewDir
    Save-ZoomPreview -size 24 -zoomFactor 6 -dir $PreviewDir
    Write-Host ("预览已输出到 {0}" -f $PreviewDir)
}
