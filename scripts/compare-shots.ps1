# Compares two folders of --shoot PNGs pixel by pixel (redesign plan, step 0.1).
#
#   .\scripts\compare-shots.ps1 -Baseline <dir> -Candidate <dir> [-Tolerance 0] [-Mask mask.txt] [-DiffDir <dir>]
#
# -Tolerance  largest per-channel difference (0-255) that still counts as equal. 0 = exact.
# -Mask       a text file of regions to ignore, one per line:  <name-pattern> <x> <y> <w> <h>
#             The pattern is matched with -like against the file name without ".png" ("main-*", "*").
#             Lines starting with # are comments.
# -DiffDir    writes <name>-diff.png for every file that differs: changed pixels red, the rest dimmed.
#
# Exit code: 0 when every PNG in both folders matches, 1 otherwise (differences, missing files, size changes).
# A baseline is only comparable on the machine, DPI and time zone it was taken with (see ShotRig.cs).
#
# ASCII only on purpose: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
param(
    [Parameter(Mandatory = $true)][string]$Baseline,
    [Parameter(Mandatory = $true)][string]$Candidate,
    [ValidateRange(0, 255)][int]$Tolerance = 0,
    [string]$Mask,
    [string]$DiffDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Pixel loops in PowerShell would take minutes per image; compile the inner loop once.
if (-not ('ShotDiff' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class ShotDiff
{
    static byte[] Pixels(Bitmap b)
    {
        var r = new Rectangle(0, 0, b.Width, b.Height);
        var d = b.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new byte[d.Stride * b.Height];
            Marshal.Copy(d.Scan0, px, 0, px.Length);
            return px;
        }
        finally { b.UnlockBits(d); }
    }

    // Returns { changed, minX, minY, maxX, maxY }, or { -1, wA, hA, wB, hB } when the sizes differ.
    // mask: x,y,w,h quadruples of ignored regions.
    public static long[] Compare(string a, string b, int tol, int[] mask, string diffPath)
    {
        using (var ba = new Bitmap(a))
        using (var bb = new Bitmap(b))
        {
            if (ba.Width != bb.Width || ba.Height != bb.Height)
                return new long[] { -1, ba.Width, ba.Height, bb.Width, bb.Height };
            int w = ba.Width, h = ba.Height;
            byte[] pa = Pixels(ba), pb = Pixels(bb);
            int stride = pa.Length / h;
            long changed = 0; int minX = w, minY = h, maxX = -1, maxY = -1;
            byte[] outPx = !string.IsNullOrEmpty(diffPath) ? new byte[pa.Length] : null;   // PowerShell passes $null as ""
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    bool masked = false;
                    for (int m = 0; m + 3 < mask.Length; m += 4)
                        if (x >= mask[m] && y >= mask[m + 1] && x < mask[m] + mask[m + 2] && y < mask[m + 1] + mask[m + 3]) { masked = true; break; }
                    bool diff = !masked && (Math.Abs(pa[i] - pb[i]) > tol || Math.Abs(pa[i + 1] - pb[i + 1]) > tol
                                         || Math.Abs(pa[i + 2] - pb[i + 2]) > tol || Math.Abs(pa[i + 3] - pb[i + 3]) > tol);
                    if (diff)
                    {
                        changed++;
                        if (x < minX) minX = x; if (y < minY) minY = y;
                        if (x > maxX) maxX = x; if (y > maxY) maxY = y;
                    }
                    if (outPx != null)
                    {
                        if (diff) { outPx[i] = 0; outPx[i + 1] = 0; outPx[i + 2] = 255; }
                        else { outPx[i] = (byte)(pb[i] / 4); outPx[i + 1] = (byte)(pb[i + 1] / 4); outPx[i + 2] = (byte)(pb[i + 2] / 4); }
                        outPx[i + 3] = 255;
                    }
                }
            }
            if (outPx != null && changed > 0)
            {
                using (var o = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    var d = o.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try { Marshal.Copy(outPx, 0, d.Scan0, outPx.Length); } finally { o.UnlockBits(d); }
                    o.Save(diffPath, ImageFormat.Png);
                }
            }
            return new long[] { changed, minX, minY, maxX, maxY };
        }
    }
}
'@
}

function Get-MaskFor([string]$name) {
    $q = New-Object System.Collections.Generic.List[int]
    if ($Mask) {
        foreach ($line in Get-Content -LiteralPath $Mask) {
            $t = $line.Trim()
            if ($t -eq '' -or $t.StartsWith('#')) { continue }
            $f = $t -split '\s+'
            if ($f.Count -ne 5) { throw "mask line needs '<pattern> x y w h': $t" }
            if ($name -like $f[0]) { foreach ($n in $f[1..4]) { $q.Add([int]$n) } }
        }
    }
    return , $q.ToArray()
}

$Baseline = (Resolve-Path -LiteralPath $Baseline).Path
$Candidate = (Resolve-Path -LiteralPath $Candidate).Path
if ($DiffDir) { New-Item -ItemType Directory -Force -Path $DiffDir | Out-Null; $DiffDir = (Resolve-Path -LiteralPath $DiffDir).Path }

$names = @(Get-ChildItem -LiteralPath $Baseline -Filter *.png | ForEach-Object { $_.BaseName }) +
         @(Get-ChildItem -LiteralPath $Candidate -Filter *.png | ForEach-Object { $_.BaseName }) | Sort-Object -Unique
$bad = 0
foreach ($n in $names) {
    $a = Join-Path $Baseline ($n + '.png'); $b = Join-Path $Candidate ($n + '.png')
    if (-not (Test-Path -LiteralPath $b)) { "MISSING  $n  (not in candidate)"; $bad++; continue }
    if (-not (Test-Path -LiteralPath $a)) { "NEW      $n  (not in baseline)"; $bad++; continue }
    $diffPath = if ($DiffDir) { Join-Path $DiffDir ($n + '-diff.png') } else { $null }
    $r = [ShotDiff]::Compare($a, $b, $Tolerance, (Get-MaskFor $n), $diffPath)
    if ($r[0] -lt 0) { "SIZE     $n  {0}x{1} -> {2}x{3}" -f $r[1], $r[2], $r[3], $r[4]; $bad++ }
    elseif ($r[0] -eq 0) { "same     $n" }
    else { "DIFF     $n  {0} px  box ({1},{2})-({3},{4})" -f $r[0], $r[1], $r[2], $r[3], $r[4]; $bad++ }
}
""
if ($bad -eq 0) { "All $($names.Count) shots match." } else { "$bad of $($names.Count) shots differ." }
exit ([int]($bad -ne 0))
