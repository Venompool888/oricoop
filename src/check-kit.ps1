# Checks a kit zip (src\make-online-kit.ps1): the OriCoop.dll inside must be the build of the commit the zip name
# says. T-091: a kit named 077a18d once carried a test build of be474be (an older menu), because the script copied
# whatever DLL sat in a test copy's plugins folder. Usage: src\check-kit.ps1 <zip>   (exit 0 = PASS)
param([Parameter(Mandatory = $true)][string]$Zip)
$ErrorActionPreference = 'Stop'
$name = [IO.Path]::GetFileName($Zip)
$m = [regex]::Match($name, '-([0-9a-f]{7,40})\.zip$')
if (-not $m.Success) { "KIT CHECK FAIL: no commit in the zip name '$name'"; exit 1 }
$named = $m.Groups[1].Value
Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($Zip)
try {
    $e = $z.Entries | Where-Object { $_.FullName -match '(^|[\\/])OriCoop\.dll$' } | Select-Object -First 1
    if (-not $e) { "KIT CHECK FAIL: no OriCoop.dll in $name"; exit 1 }
    $ms = New-Object IO.MemoryStream; $es = $e.Open(); $es.CopyTo($ms); $es.Dispose()
    $asm = [Reflection.Assembly]::Load($ms.ToArray())
    $res = $asm.GetManifestResourceStream('OriCoop.build.txt')
    if (-not $res) { "KIT CHECK FAIL: the DLL carries no build info (built without build.ps1 / build-commit.ps1)"; exit 1 }
    $text = (New-Object IO.StreamReader($res)).ReadToEnd()
} finally { $z.Dispose() }
$cm = [regex]::Match($text, 'commit=([0-9a-f]+)(\+local)?')
if (-not $cm.Success) { "KIT CHECK FAIL: unreadable build info '$($text.Trim())'"; exit 1 }
$dllCommit = $cm.Groups[1].Value
if ($cm.Groups[2].Success) { "KIT CHECK FAIL: the DLL is a working-tree build ($dllCommit+local), not a commit"; exit 1 }
if (-not ($dllCommit.StartsWith($named) -or $named.StartsWith($dllCommit))) { "KIT CHECK FAIL: the zip is named $named but its DLL is a build of $dllCommit"; exit 1 }
"KIT CHECK PASS: $name carries the build of $dllCommit"
exit 0
