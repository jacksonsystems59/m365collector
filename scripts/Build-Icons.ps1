$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../assets/icons'))
New-Item -ItemType Directory -Path $root -Force | Out-Null
# Original geometry, shared between editable SVG and antialiased PNG exports.
$icons = [ordered]@{
    dashboard = @('R 3 3 7 7','R 14 3 7 7','R 3 14 7 7','R 14 14 7 7')
    customers = @('E 4 3 6 6','E 14 5 5 5','L 2 21 2 15 5 12 9 12 12 15 12 21','L 15 13 19 13 22 16 22 21')
    add = @('E 3 3 18 18','L 12 7 12 17','L 7 12 17 12')
    modules = @('R 3 3 7 7','R 14 3 7 7','R 3 14 7 7','L 14 17 21 17','L 17.5 14 17.5 21')
    search = @('E 3 3 12 12','L 14 14 21 21')
    warning = @('L 12 3 22 21 2 21 12 3','L 12 9 12 14','L 12 17 12 18')
    location = @('E 9 7 6 6','L 5 13 4 9 6 4 12 2 18 4 20 9 19 13 12 22 5 13')
    reports = @('L 5 2 15 2 20 7 20 22 5 22 5 2','L 15 2 15 7 20 7','L 9 17 9 13','L 13 17 13 10','L 17 17 17 12')
    shield = @('L 12 2 21 6 20 15 16 20 12 23 8 20 4 15 3 6 12 2','L 7 12 10 15 17 8')
    update = @('L 12 3 12 15','L 7 10 12 15 17 10','L 4 16 4 21 20 21 20 16')
    logs = @('R 3 2 18 20','L 7 7 17 7','L 7 12 17 12','L 7 17 14 17')
    lock = @('R 5 10 14 12','L 8 10 8 5 10 2 14 2 16 5 16 10','L 12 14 12 18')
    key = @('E 2 3 10 10','L 10 12 20 22','L 15 17 18 14','L 18 20 21 17')
    play = @('L 7 3 21 12 7 21 7 3')
    export = @('L 3 9 3 21 17 21 17 16','L 10 14 21 3','L 14 3 21 3 21 10')
    healthy = @('E 2 2 20 20','L 6 12 10 16 18 8')
}
foreach ($name in $icons.Keys) {
    $svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" stroke="#006791" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">'
    $bitmap = [Drawing.Bitmap]::new(96,96); $g = [Drawing.Graphics]::FromImage($bitmap); $g.SmoothingMode = 'AntiAlias'; $g.ScaleTransform(4,4)
    $pen = [Drawing.Pen]::new([Drawing.Color]::White,1.8); $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round'
    foreach ($shape in $icons[$name]) {
        $parts=$shape.Split(' '); $v=@($parts[1..($parts.Length-1)] | ForEach-Object { [single]::Parse($_,[Globalization.CultureInfo]::InvariantCulture) })
        switch ($parts[0]) {
            'R' { $g.DrawRectangle($pen,$v[0],$v[1],$v[2],$v[3]); $svg += "<rect x='$($v[0])' y='$($v[1])' width='$($v[2])' height='$($v[3])'/>" }
            'E' { $g.DrawEllipse($pen,$v[0],$v[1],$v[2],$v[3]); $svg += "<ellipse cx='$($v[0]+$v[2]/2)' cy='$($v[1]+$v[3]/2)' rx='$($v[2]/2)' ry='$($v[3]/2)'/>" }
            'L' { $points=[Collections.Generic.List[Drawing.PointF]]::new(); for($i=0;$i -lt $v.Length;$i+=2){$points.Add([Drawing.PointF]::new($v[$i],$v[$i+1]))}; $g.DrawLines($pen,$points.ToArray()); $svg += "<polyline points='$($v -join ' ') '/>" }
        }
    }
    [IO.File]::WriteAllText((Join-Path $root "$name.svg"),$svg+'</svg>')
    $small=[Drawing.Bitmap]::new(24,24); $sg=[Drawing.Graphics]::FromImage($small);$sg.InterpolationMode='HighQualityBicubic';$sg.DrawImage($bitmap,0,0,24,24);$small.Save((Join-Path $root "$name.png"),[Drawing.Imaging.ImageFormat]::Png)
    $sg.Dispose();$small.Dispose();$pen.Dispose();$g.Dispose();$bitmap.Dispose()
}
$appSvg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><rect width="64" height="64" rx="12" fill="#1b2a3f"/><path d="M12 44V20h7l13 15 13-15h7v24h-8V32L32 46 20 32v12z" fill="white"/><path d="M25 12h14v6H25zM27 51h10v5H27z" fill="#24c6c8"/></svg>'
[IO.File]::WriteAllText((Join-Path $root 'application.svg'),$appSvg)
$images=[Collections.Generic.List[byte[]]]::new();$sizes=@(16,24,32,48,64,128,256)
foreach($size in $sizes){
    $bitmap=[Drawing.Bitmap]::new($size,$size);$g=[Drawing.Graphics]::FromImage($bitmap);$g.SmoothingMode='AntiAlias';$g.ScaleTransform($size/64.0,$size/64.0);$g.Clear([Drawing.Color]::FromArgb(27,42,63))
    $points=[Drawing.PointF[]]@([Drawing.PointF]::new(12,44),[Drawing.PointF]::new(12,20),[Drawing.PointF]::new(19,20),[Drawing.PointF]::new(32,35),[Drawing.PointF]::new(45,20),[Drawing.PointF]::new(52,20),[Drawing.PointF]::new(52,44),[Drawing.PointF]::new(44,44),[Drawing.PointF]::new(44,32),[Drawing.PointF]::new(32,46),[Drawing.PointF]::new(20,32),[Drawing.PointF]::new(20,44))
    $g.FillPolygon([Drawing.Brushes]::White,$points);$brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(36,198,200));$g.FillRectangle($brush,25,12,14,6);$g.FillRectangle($brush,27,51,10,5)
    $stream=[IO.MemoryStream]::new();$bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png);$images.Add($stream.ToArray());[IO.File]::WriteAllBytes((Join-Path $root "application-$size.png"),$stream.ToArray());$stream.Dispose();$brush.Dispose();$g.Dispose();$bitmap.Dispose()
}
$output=[IO.File]::Create((Join-Path $root 'application.ico'));$writer=[IO.BinaryWriter]::new($output);$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count);$offset=6+16*$sizes.Count
for($i=0;$i -lt $sizes.Count;$i++){ $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length }
foreach($bytes in $images){$writer.Write($bytes)};$writer.Dispose();$output.Dispose()
Write-Output "Created original SVG, PNG and multi-size ICO assets in $root"
