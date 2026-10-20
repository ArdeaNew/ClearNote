#Requires -Version 5.1
[CmdletBinding()]
param([string]$PreviewPath = '')

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$iconFolder = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Folio.App\Assets'))
New-Item -ItemType Directory -Path $iconFolder -Force | Out-Null

# Keep these editable paths in step with AppIcons.CreateBrand. No bitmap or font dependency.
$shapes = @(
    @{ Fill='#141414'; Data='M96 151L138 163L140 184C162 194 177 212 183 238H54C60 210 75 191 101 184L105 169Z' },
    @{ Fill='#141414'; Data='M130 18C83 11 42 42 43 82C44 114 63 137 88 153C100 161 111 174 127 176C146 177 160 159 168 145L180 130C185 124 190 121 188 116L178 105C188 71 181 32 147 21C141 19 135 18 130 18Z' },
    @{ Fill='#FFFFFF'; Data='M73 93C89 91 109 99 122 118C103 122 85 112 73 93ZM144 114C151 103 163 98 174 99C169 113 158 121 144 124Z' },
    @{ Fill='#FFFFFF'; Data='M143 168C137 182 138 194 145 208L158 238H171L155 202C150 190 150 182 159 166Z' },
    @{ Fill='#141414'; Data='M174 145C180 146 183 152 181 158L169 183L193 230C195 234 192 238 187 238H166L148 190C146 184 147 178 151 170L158 157C150 159 145 157 147 152L161 142C165 139 169 139 172 142Z' }
)
$pathElements = @($shapes | ForEach-Object { '  <path d="' + $_.Data + '" fill="' + $_.Fill + '"/>' }) -join "`n"
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img" aria-labelledby="title desc">
  <title id="title">ClearNote</title>
  <desc id="desc">A quiet alien thinker, with an elongated head and a hand beneath the chin.</desc>
$pathElements
</svg>
"@
[IO.File]::WriteAllText((Join-Path $iconFolder 'Folio.svg'), $svg + "`n", [Text.UTF8Encoding]::new($false))

function Render-Png([int]$Size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform([Windows.Media.ScaleTransform]::new($Size / 256.0, $Size / 256.0))
        foreach ($shape in $shapes) {
            $brush = [Windows.Media.BrushConverter]::new().ConvertFromString($shape.Fill)
            $geometry = [Windows.Media.Geometry]::Parse($shape.Data)
            $context.DrawGeometry($brush, $null, $geometry)
        }
        $context.Pop()
    }
    finally { $context.Close() }
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [IO.MemoryStream]::new()
    try {
        $encoder.Save($memory)
        return ,$memory.ToArray()
    }
    finally { $memory.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = @($sizes | ForEach-Object { ,(Render-Png $_) })
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $iconFolder 'Folio.ico')))
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
}
finally { $writer.Dispose() }

if ($PreviewPath) {
    $resolvedPreview = [IO.Path]::GetFullPath($PreviewPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($resolvedPreview)) -Force | Out-Null
    [IO.File]::WriteAllBytes($resolvedPreview, (Render-Png 1024))
}
Get-Item -LiteralPath (Join-Path $iconFolder 'Folio.ico')
