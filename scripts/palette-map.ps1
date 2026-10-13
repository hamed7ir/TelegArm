# Writes palette-map.tsv: every Color literal in TelegArm\ with the Palette token(s) it equals (redesign plan
# step 1.2; the input for the step 1.7 and 5.1 sweeps that replace literals with tokens).
#
#   .\scripts\palette-map.ps1 [-Out mdfiles\palette-map.tsv]
#
# Columns: file, line, kind, literal, tokens.
#   pair   - "cond ? Color.X(dark) : Color.Y(light)" where one token has exactly that dark AND light value
#   dark / light - one literal equal to a token's value in that theme
#   none   - no token has this value yet (step 1.7/5.1 decide: new token, or a deliberate one-off)
# Tokens are read from the BUILT TelegArm.exe (TelegArm\bin\Release), so build first. Accent-derived tokens are
# left out: they are found by building the palette with two different accents and dropping what changed.
#
# ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) { $Out = Join-Path $root 'mdfiles\palette-map.tsv' }
Add-Type -AssemblyName System.Drawing
$exe = Join-Path $root 'TelegArm\bin\Release\TelegArm.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Build Release first: $exe" }
$pt = [Reflection.Assembly]::LoadFrom($exe).GetType('TelegArm.Helpers.Palette')
$build = $pt.GetMethod('Build')
$props = @($pt.GetProperties() | Where-Object { $_.PropertyType -eq [System.Drawing.Color] })

function Argb($c) { '{0},{1},{2},{3}' -f $c.A, $c.R, $c.G, $c.B }
$tokens = @{}   # name -> @{ dark = 'a,r,g,b'; light = 'a,r,g,b' }
$s1 = [System.Drawing.Color]::FromArgb(1, 2, 3); $s2 = [System.Drawing.Color]::FromArgb(201, 202, 203)
foreach ($dark in $true, $false) {
    $p1 = $build.Invoke($null, @($dark, $s1)); $p2 = $build.Invoke($null, @($dark, $s2))
    foreach ($pr in $props) {
        $v1 = Argb ($pr.GetValue($p1, $null)); $v2 = Argb ($pr.GetValue($p2, $null))
        if ($v1 -ne $v2) { continue }   # accent-derived
        if (-not $tokens.ContainsKey($pr.Name)) { $tokens[$pr.Name] = @{} }
        $tokens[$pr.Name][$(if ($dark) { 'dark' } else { 'light' })] = $v1
    }
}

# A literal as 'a,r,g,b', or $null when it is not a constant colour.
function Lit([string]$s) {
    $s = $s.Trim()
    if ($s -eq 'Color.White') { return '255,255,255,255' }
    if ($s -eq 'Color.Black') { return '255,0,0,0' }
    $m = [regex]::Match($s, '^Color\.FromArgb\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*(\d+)\s*)?\)$')
    if (-not $m.Success) { return $null }
    if ($m.Groups[4].Success) { return '{0},{1},{2},{3}' -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value, $m.Groups[4].Value }
    return '255,{0},{1},{2}' -f $m.Groups[1].Value, $m.Groups[2].Value, $m.Groups[3].Value
}
$litRx = 'Color\.FromArgb\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*(?:,\s*\d+\s*)?\)|Color\.White|Color\.Black'
$pairRx = "\?\s*($litRx)\s*:\s*($litRx)"

$rows = New-Object Collections.Generic.List[string]
$rows.Add("file`tline`tkind`tliteral`ttokens")
$files = Get-ChildItem -LiteralPath (Join-Path $root 'TelegArm') -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.Name -notin 'Palette.cs', 'ColorMath.cs' }
$counts = @{ pair = 0; dark = 0; light = 0; none = 0 }
foreach ($f in $files) {
    $rel = $f.FullName.Substring($root.Length + 1)
    $n = 0
    foreach ($line in [IO.File]::ReadAllLines($f.FullName)) {
        $n++
        $code = $line -replace '//.*$', ''
        if ($code -notmatch $litRx) { continue }
        $used = @{}
        foreach ($m in [regex]::Matches($code, $pairRx)) {
            $d = Lit $m.Groups[1].Value; $l = Lit $m.Groups[2].Value
            $hit = @($tokens.Keys | Where-Object { $tokens[$_].dark -eq $d -and $tokens[$_].light -eq $l } | Sort-Object)
            $kind = if ($hit.Count) { 'pair' } else { 'none' }
            $counts[$kind]++
            $rows.Add("$rel`t$n`t$kind`t$($m.Value.Trim())`t$($hit -join ' ')")
            $used[$m.Groups[1].Index] = 1; $used[$m.Groups[2].Index] = 1
        }
        foreach ($m in [regex]::Matches($code, $litRx)) {
            if ($used.ContainsKey($m.Index)) { continue }
            $v = Lit $m.Value
            $hd = @($tokens.Keys | Where-Object { $tokens[$_].dark -eq $v } | Sort-Object)
            $hl = @($tokens.Keys | Where-Object { $tokens[$_].light -eq $v } | Sort-Object)
            if ($hd.Count -and $hl.Count) { $kind = 'dark+light'; $t = ($hd + $hl | Sort-Object -Unique) -join ' ' }
            elseif ($hd.Count) { $kind = 'dark'; $t = $hd -join ' ' }
            elseif ($hl.Count) { $kind = 'light'; $t = $hl -join ' ' }
            else { $kind = 'none'; $t = '' }
            if ($counts.ContainsKey($kind)) { $counts[$kind]++ } else { $counts[$kind] = 1 }
            $rows.Add("$rel`t$n`t$kind`t$($m.Value)`t$t")
        }
    }
}
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllLines($Out, $rows, (New-Object Text.UTF8Encoding($false)))
"$($rows.Count - 1) literals -> $Out"
$counts.GetEnumerator() | Sort-Object Name | ForEach-Object { "  {0,-11} {1}" -f $_.Name, $_.Value }
