# Builds the P2 skin artist kit from a SkinDump output (run the game with ORICOOP_DUMP_SKIN first).
param([string]$Raw = 'C:\OriCoop\skin-dump\raw', [string]$Out = 'C:\OriCoop\skin-dump\OriP2Skin_ArtistKit')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$optional = @('seinEffectsAtlas0', 'seinFeatherAtlas0')   # double-jump effect and glide feather: not Ori's body
Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue
foreach ($d in 'atlases', 'optional', 'guides') { New-Item -ItemType Directory -Force "$Out\$d" | Out-Null }
$j = Get-Content "$Raw\atlases.json" -Raw | ConvertFrom-Json
$normal = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 255, 200, 0)), 2
$rot = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 0, 120, 255)), 3
$list = @()
foreach ($a in $j.atlases) {
    $name = [IO.Path]::GetFileNameWithoutExtension($a.file)
    $dest = if ($optional -contains $name) { 'optional' } else { 'atlases' }
    Copy-Item "$Raw\$($a.file)" "$Out\$dest\$($a.file)"
    $src = [System.Drawing.Image]::FromFile("$Raw\$($a.file)")
    $bmp = New-Object System.Drawing.Bitmap $src.Width, $src.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.DrawImage($src, 0, 0, $src.Width, $src.Height)
    $nr = 0
    foreach ($f in $a.frames) { if ($f.rotated) { $nr++ }; $g.DrawRectangle($(if ($f.rotated) { $rot } else { $normal }), $f.x, $f.y, $f.w - 1, $f.h - 1) }
    $g.Dispose(); $src.Dispose()
    $bmp.Save("$Out\guides\$($name)_guide.png", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $key = if ($a.screenMode -eq 'Red') { '红 red #FF0000' } else { '绿 green #00FF00' }
    $list += "| $dest/$($a.file) | $($a.width)x$($a.height) | $key | $($a.frames.Count) ($nr 旋转/rotated) |"
}
Copy-Item "$Raw\atlases.json" "$Out\atlases.json"

# Preview stage: preview-data.js (frames + animation sequences) next to the HTML page.
$inv = [Globalization.CultureInfo]::InvariantCulture
$idx = @{}; $sb = New-Object Text.StringBuilder
[void]$sb.Append("window.ORI_DATA={atlases:[")
for ($i = 0; $i -lt $j.atlases.Count; $i++) {
    $a = $j.atlases[$i]; $name = [IO.Path]::GetFileNameWithoutExtension($a.file); $idx[$a.texture] = $i
    $dir = if ($optional -contains $name) { 'optional' } else { 'atlases' }
    if ($i) { [void]$sb.Append(',') }
    [void]$sb.Append("{name:`"$name`",file:`"$($a.file)`",dir:`"$dir`",w:$($a.width),h:$($a.height),key:`"$($a.screenMode)`",frames:[")
    $fs = foreach ($f in $a.frames) { '[{0},{1},{2},{3},{4},{5},{6},{7},{8}]' -f $f.x, $f.y, $f.w, $f.h, $(if ($f.rotated) { 1 } else { 0 }), $f.ox.ToString('0.###', $inv), $f.oy.ToString('0.###', $inv), $f.cx.ToString('0.#####', $inv), $f.cy.ToString('0.#####', $inv) }
    [void]$sb.Append(($fs -join ',')).Append(']}')
}
[void]$sb.Append("],anims:[")
$anims = Get-Content "$Raw\animations.json" -Raw | ConvertFrom-Json
$first = $true
foreach ($an in $anims) {
    $fr = foreach ($f in $an.frames) { if ($idx.ContainsKey($f[0])) { "[$($idx[$f[0]]),$($f[1])]" } }
    if (-not $fr) { continue }
    if (-not $first) { [void]$sb.Append(',') }; $first = $false
    [void]$sb.Append("{name:`"$($an.name)`",loop:$($an.loop.ToString().ToLower()),pp:$($an.pingPong.ToString().ToLower()),frames:[$(@($fr) -join ',')]}")
}
[void]$sb.Append("]};")
[IO.File]::WriteAllText("$Out\preview-data.js", $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Copy-Item 'C:\OriCoop\src\skin-preview.html' "$Out\预览舞台_preview.html"
$readme = Get-Content 'C:\OriCoop\src\skin-kit-readme.md' -Raw -Encoding UTF8
$readme = $readme.Replace('{{TABLE}}', ($list -join "`n"))
[IO.File]::WriteAllText("$Out\README_给美术_for_artist.md", $readme, (New-Object System.Text.UTF8Encoding $true))
$zip = "$Out.zip"; Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$Out\*" -DestinationPath $zip
"kit: $Out"; "zip: $zip ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB)
