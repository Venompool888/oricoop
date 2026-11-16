# Makes a test P2 skin: Ori tinted blue, saved as TRANSPARENT PNGs (exercises the loader's key-fill path), zipped.
param([string]$Raw = 'C:\OriCoop\skin-dump\raw', [string]$Out = 'C:\OriCoop\skin-dump\test-skin-blue',
      [string]$Install = 'C:\OriCoop\game\BepInEx\plugins\OriCoop_P2Skin', [string]$Only = '')
$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class SkinTint {
  // Un-keys a chroma-key atlas (key = 0 red, 1 green) and tints Ori: returns a 32-bit ARGB bitmap with alpha.
  public static Bitmap Run(string file, int key, float tr, float tg, float tb) {
    Bitmap src = new Bitmap(file);
    Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
    Rectangle r = new Rectangle(0, 0, src.Width, src.Height);
    BitmapData s = src.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    BitmapData d = dst.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    int n = src.Width * src.Height; int[] px = new int[n];
    Marshal.Copy(s.Scan0, px, 0, n);
    for (int i = 0; i < n; i++) {
      int c = px[i]; float R = (c >> 16) & 255, G = (c >> 8) & 255, B = c & 255;
      float k = key == 0 ? R - Math.Max(G, B) : G - Math.Max(R, B);   // how much key color is mixed in
      float a = 1f - Math.Max(0f, Math.Min(255f, k)) / 255f;
      if (a < 0.02f) { px[i] = 0; continue; }
      // remove the key's share, then un-premultiply
      if (key == 0) R -= (1f - a) * 255f; else G -= (1f - a) * 255f;
      R /= a; G /= a; B /= a;
      float L = (R * 0.3f + G * 0.59f + B * 0.11f) / 255f;
      int nr = (int)Math.Min(255f, L * tr), ng = (int)Math.Min(255f, L * tg), nb = (int)Math.Min(255f, L * tb);
      px[i] = ((int)(a * 255f) << 24) | (nr << 16) | (ng << 8) | nb;
    }
    Marshal.Copy(px, 0, d.Scan0, n);
    src.UnlockBits(s); dst.UnlockBits(d); src.Dispose();
    return dst;
  }
}
'@
$j = Get-Content "$Raw\atlases.json" -Raw | ConvertFrom-Json
Remove-Item $Out -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $Out | Out-Null
foreach ($a in $j.atlases) {
    $name = [IO.Path]::GetFileNameWithoutExtension($a.file)
    if ($Only -and $name -notlike $Only) { continue }
    $key = if ($a.screenMode -eq 'Red') { 0 } else { 1 }
    $bmp = [SkinTint]::Run("$Raw\$($a.file)", $key, 90, 160, 255)
    $bmp.Save("$Out\$($a.file)", [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
New-Item -ItemType Directory -Force $Install | Out-Null
Remove-Item "$Install\*" -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$Out\*.png" -DestinationPath "$Install\test-blue.zip" -Force
"test skin: $((Get-ChildItem $Out).Count) PNGs -> $Install\test-blue.zip"
