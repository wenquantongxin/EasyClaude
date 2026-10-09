$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskAssetDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\EasyClaude\Assets'
New-Item -ItemType Directory -Force -Path $taskAssetDir | Out-Null
$taskSvg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 128 128">
  <rect x="8" y="8" width="112" height="112" rx="28" fill="#6366f1"/>
  <path d="M85 40H54C43 40 37 47 37 58V73C37 84 43 90 54 90H78L91 100V76M51 58H78M51 74H70" fill="none" stroke="#fff" stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
'@
[IO.File]::WriteAllText((Join-Path $taskAssetDir 'EasyClaude.svg'), $taskSvg)
function New-BrandPng([int]$size, [string]$state) {
    $bitmap = [Drawing.Bitmap]::new($size,$size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([Drawing.Color]::Transparent)
        $g.ScaleTransform($size/128.0,$size/128.0)
        $path = [Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $path.AddArc(8,8,56,56,180,90); $path.AddArc(64,8,56,56,270,90)
            $path.AddArc(64,64,56,56,0,90); $path.AddArc(8,64,56,56,90,90); $path.CloseFigure()
            $brush = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#6366F1'))
            try {$g.FillPath($brush,$path)} finally {$brush.Dispose()}
        } finally {$path.Dispose()}
        $pen = [Drawing.Pen]::new([Drawing.Color]::White,8)
        $pen.StartCap = $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        try {
            $brand = [Drawing.Drawing2D.GraphicsPath]::new()
            try {
                $brand.AddLine(85,40,54,40); $brand.AddBezier(54,40,43,40,37,47,37,58)
                $brand.AddLine(37,58,37,73); $brand.AddBezier(37,73,37,84,43,90,54,90)
                $brand.AddLine(54,90,78,90); $brand.AddLine(78,90,91,100); $brand.AddLine(91,100,91,76)
                $g.DrawPath($pen,$brand); $g.DrawLine($pen,51,58,78,58); $g.DrawLine($pen,51,74,70,74)
            } finally {$brand.Dispose()}
        } finally {$pen.Dispose()}
        if ($state) {
            $colors = @{Ready='#10B981';Warning='#F59E0B';Error='#EF4444';Starting='#38BDF8';Stopped='#94A3B8'}
            $border = [Drawing.SolidBrush]::new([Drawing.Color]::White)
            $badge = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml($colors[$state]))
            try {$g.FillRectangle($border,84,84,44,44); $g.FillRectangle($badge,88,88,36,36)}
            finally {$border.Dispose();$badge.Dispose()}
        }
        $stream=[IO.MemoryStream]::new()
        try {$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); return ,$stream.ToArray()} finally {$stream.Dispose()}
    } finally {$g.Dispose();$bitmap.Dispose()}
}
foreach ($state in @('', 'Ready','Warning','Error','Starting','Stopped')) {
    $sizes= @(16,20,24,32,40,48,64,128,256)
    $pngs = @($sizes | ForEach-Object {New-BrandPng $_ $state})
    $name=if($state){"Tray$state.ico"}else{'EasyClaude.ico'}
    $file=[IO.File]::Create((Join-Path $taskAssetDir $name))
    $writer=[IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
        $offset=6+16*$sizes.Count
        for($i=0;$i -lt $sizes.Count;$i++) {
            $dim=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
            $writer.Write([byte]$dim);$writer.Write([byte]$dim);$writer.Write([byte]0);$writer.Write([byte]0)
            $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$pngs[$i].Length);$writer.Write([uint32]$offset)
            $offset += $pngs[$i].Length
        }
        foreach($png in $pngs){$writer.Write([byte[]]$png)}
    } finally {$writer.Dispose();$file.Dispose()}
}
'Generated SVG and nine-size application/tray icons.'
