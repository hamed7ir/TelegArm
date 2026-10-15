# The redesign plan's mechanical gates (mdfiles/REDESIGN-PLAN.md section 12), plus the SelfTest run. Step 0.6.
#
#   .\scripts\check.ps1 [-Step 0.6] [-NoSelfTest]
#
# Each gate names the plan step it starts at. Gates at or before -Step are ENFORCED (a hit fails the run);
# later ones are listed as "pending" with today's hit count, so the remaining work stays visible.
# Raise -Step's default as the plan advances. Exit code 0 = every enforced gate passed and SelfTest passed.
#
# ASCII only: Windows PowerShell 5.1 reads a BOM-less script as ANSI.
param([string]$Step = '3.7', [switch]$NoSelfTest)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$app = Join-Path $root 'TelegArm'
$all = @(Get-ChildItem -LiteralPath $app -Recurse -Filter *.cs | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$ui = @($all | Where-Object { $_.FullName -like (Join-Path $app 'UI\*') })
function Rel([string]$p) { $p.Substring($root.Length + 1) }

# file:line: text for every line of $files whose CODE matches $pattern, minus files whose names match
# $exceptFiles. A // comment is cut off first: the gates judge code, and the comments that warn against a
# construct name it (step 1.1's "never read Control.DeviceDpi" tripped the DeviceDpi gate).
function Hits($files, [string]$pattern, [string]$exceptFiles) {
    $out = @()
    foreach ($f in $files) {
        if ($exceptFiles -and $f.Name -match $exceptFiles) { continue }
        foreach ($m in Select-String -LiteralPath $f.FullName -Pattern $pattern) {
            if (($m.Line -replace '//.*$', '') -notmatch $pattern) { continue }
            $out += (Rel $f.FullName) + ':' + $m.LineNumber + ': ' + $m.Line.Trim()
        }
    }
    return $out
}

# Step 3.3: every new Size/Point/Padding/Rectangle(...) whose ARGUMENTS hold a bare integer >= 2 outside Px(...)/P(...)
# (see the gate). The argument text is taken to the matching parenthesis, Px(...)/P(...)/F(...) calls are removed, and
# what is left is searched for a literal that is not part of a float, a name, or / * % arithmetic.
function LiteralHits($files, [string]$exceptFiles) {
    $out = @()
    foreach ($f in $files) {
        if ($exceptFiles -and $f.Name -match $exceptFiles) { continue }
        $lines = [IO.File]::ReadAllLines($f.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $code = $lines[$i] -replace '//.*$', ''
            if ($code -match 'LogicalMinimumSize\s*=') { continue }
            if ($lines[$i] -match '//.*\blogical\b') { continue }   # a declared LOGICAL constant (Px'd where it is used)
            foreach ($m in [regex]::Matches($code, 'new\s+(Size|Point|Padding|Rectangle)\s*\(')) {
                $k = $m.Index + $m.Length; $depth = 1; $args = ''
                while ($k -lt $code.Length -and $depth -gt 0) {
                    $c = $code[$k]
                    if ($c -eq '(') { $depth++ } elseif ($c -eq ')') { $depth--; if ($depth -eq 0) { break } }
                    $args += $c; $k++
                }
                $prev = $null
                while ($prev -ne $args) { $prev = $args; $args = [regex]::Replace($args, '\b(Dpi\.Px|Px|P|S|Dpi\.F|F)\s*\([^()]*\)', '0') }
                $args = [regex]::Replace($args, '0x[0-9A-Fa-f]+|(<<|>>)\s*\d+', '0')   # message decoding, not lengths
                if ($args -match '(?<![\w.])(?<![/*%]\s?)(?<![/*%])([2-9]|\d{2,})(?![\w.])(?!\s*[*/])') {   # "2 * x" scales a scaled x
                    $out += (Rel $f.FullName) + ':' + ($i + 1) + ': ' + $lines[$i].Trim(); break
                }
            }
        }
    }
    return $out
}

# The bodies of every OnPaint / Paint* method, as "file:line: text" lines (brace counting; good enough for a gate).
function PaintBodies($files) {
    $out = @()
    foreach ($f in $files) {
        $lines = [IO.File]::ReadAllLines($f.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -notmatch '\bvoid\s+(OnPaint\w*|Paint\w*)\s*\(') { continue }
            $depth = 0; $seen = $false
            for ($j = $i; $j -lt $lines.Count; $j++) {
                $out += (Rel $f.FullName) + ':' + ($j + 1) + ': ' + $lines[$j].Trim()
                $depth += ([regex]::Matches($lines[$j], '\{')).Count - ([regex]::Matches($lines[$j], '\}')).Count
                if ($lines[$j] -match '\{') { $seen = $true }
                if ($seen -and $depth -le 0) { break }
            }
        }
    }
    return $out
}

# Phase 2: every file a step has converted off MaterialSkin (append as steps land; step 2.8 then forbids it everywhere).
$converted = @(
    'UI\ThemedDialog.cs',                                                                    # 2.1
    'UI\EditContactForm.cs', 'UI\CreateChatForm.cs', 'UI\CountryPickerForm.cs',              # 2.2 / 2.6b
    'UI\StoriesGridForm.cs', 'UI\PeoplePickerForm.cs', 'UI\ForwardPickerDialog.cs',
    'UI\SendMediaDialog.cs', 'UI\ProfilePhotoViewer.cs', 'UI\ProxyLinkForm.cs', 'UI\ProxyShare.cs',
    'UI\ProxyForm.cs', 'UI\AboutForm.cs', 'UI\RoundRecorderForm.cs',
    'UI\Admin\ManageChatForm.cs', 'UI\Admin\RightsChecklistForm.cs', 'UI\Admin\EditChatInfoForm.cs',
    'UI\Admin\ChannelExtrasForm.cs', 'UI\Admin\ChatMembersForm.cs', 'UI\Admin\InviteLinksForm.cs',
    'UI\MediaViewerForm.cs',                                                                 # 2.3
    'UI\LoginForm.cs',                                                                       # 2.4
    'UI\ProfileForm.cs',                                                                     # 2.5
    'UI\SettingsForm.cs', 'UI\Controls\SettingRowsExtra.cs', 'UI\Controls\SessionRowControl.cs'   # 2.6,
    'UI\MainForm.Bubbles.cs', 'UI\MainForm.ChatList.cs', 'UI\MainForm.ChatOpen.cs', 'UI\MainForm.Composer.cs', 'UI\MainForm.Dock.cs', 'UI\MainForm.Drawer.cs', 'UI\MainForm.Folders.cs', 'UI\MainForm.History.cs', 'UI\MainForm.Keyboard.cs', 'UI\MainForm.Layout.cs', 'UI\MainForm.Media.cs', 'UI\MainForm.MessageActions.cs', 'UI\MainForm.MessageExtras.cs', 'UI\MainForm.Overlays.cs', 'UI\MainForm.Presence.cs', 'UI\MainForm.Proxy.cs', 'UI\MainForm.Scroll.cs', 'UI\MainForm.Search.cs', 'UI\MainForm.Session.cs', 'UI\MainForm.Theme.cs', 'UI\MainForm.Tray.cs', 'UI\MainForm.Updates.cs', 'UI\MainForm.cs'   # 2.7a
)

$rowClasses = @($all | Where-Object { $_.Name -in 'MessageBubbleControl.cs', 'VoiceBubbleControl.cs', 'ServiceLineControl.cs', 'SponsoredCardControl.cs', 'ChatListItemControl.cs' })

$gates = @(
    @{ From = 'always'; Name = 'every .cs under TelegArm\ is in TelegArm.csproj'; Test = {
        $proj = [IO.File]::ReadAllText((Join-Path $app 'TelegArm.csproj'))
        $listed = @([regex]::Matches($proj, '<Compile Include="([^"]+)"') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() })
        @($all | ForEach-Object { $_.FullName.Substring($app.Length + 1) } | Where-Object { $listed -notcontains $_.ToLowerInvariant() } | ForEach-Object { "not compiled: TelegArm\$_" }) } }
    @{ From = 'always'; Name = 'no BackColor = Color.Transparent on the chat row classes (a plain Control throws: the v1.9.0 launch crash)'; Test = {
        Hits $rowClasses 'BackColor\s*=\s*Color\.Transparent' $null } }
    @{ From = 'always'; Name = 'PowerShell scripts are ASCII'; Test = {
        @(Get-ChildItem -LiteralPath $root -Recurse -Filter *.ps1 | Where-Object { $_.FullName -notmatch '\\(bin|obj|mdfiles|\.claude)\\' } |
            Where-Object { @([IO.File]::ReadAllBytes($_.FullName) | Where-Object { $_ -gt 127 }).Count -gt 0 } | ForEach-Object { 'non-ASCII: ' + (Rel $_.FullName) }) } }
    @{ From = '1.1'; Name = 'no Control.DeviceDpi (it reports the system DPI under PMv1 - track DPI per window)'; Test = { Hits $all '\.DeviceDpi\b' $null } }
    @{ From = '1.3'; Name = 'no ClientSize = in AppForm / DialogForm subclasses (set Size)'; Test = {
        $forms = @($all | Where-Object { (Select-String -LiteralPath $_.FullName -Pattern ':\s*(AppForm|DialogForm)\b' -Quiet) })
        Hits $forms '\bClientSize\s*=(?!=)' $null } }
    @{ From = '1.3'; Name = 'AppForm asks DWM for Windows 11 round corners (DWMWCP_ROUND; the owner wants them kept)'; Test = {
        $af = @($all | Where-Object { $_.Name -eq 'AppForm.cs' })
        if (@(Hits $af 'DwmSetWindowAttribute\(Handle,\s*DWMWA_WINDOW_CORNER_PREFERENCE').Count -eq 0 -or @(Hits $af 'int\s+round\s*=\s*DWMWCP_ROUND').Count -eq 0) {
            'UI\AppForm.cs: the DWMWCP_ROUND request in ApplyDwmAttributes is gone' } } }
    @{ From = 'always'; Name = 'input evidence survives Release: no Debug.WriteLine for [KBD]/[INPUT] lines (R7, device trip)'; Test = {
        Hits $all 'Debug\.WriteLine\(\s*"\[(KBD|INPUT)' $null } }
    @{ From = 'always'; Name = 'no MaterialSkin composer leftovers: baseTextBox reflection, EnableComposerMultiline, CreateLegacy (plan E5)'; Test = {
        Hits $all '\bbaseTextBox\b|EnableComposerMultiline|CreateLegacy' $null } }
    @{ From = '2.1'; Name = 'no Material in the files Phase 2 has converted (plan 5 (f))'; Test = {
        $done = @($all | Where-Object { $converted -contains $_.FullName.Substring($app.Length + 1) })
        # String literals are cut too: About's credits name the library (it ships until 2.8), which is not a use of it.
        @(Hits $done '\bMaterial[A-Z]\w*|\bMaterialSkin\b' $null | Where-Object { ($_ -replace '^.+?:\d+: ', '' -replace '"[^"]*"', '""') -match '\bMaterial[A-Z]\w*|\bMaterialSkin\b' }) } }
    @{ From = '2.2'; Name = 'no MessageBox in the files Phase 2 has converted (plan 5 (b): ThemedDialog)'; Test = {
        $done = @($all | Where-Object { $converted -contains $_.FullName.Substring($app.Length + 1) })
        Hits $done 'MessageBox\.Show' $null } }
    @{ From = '2.6'; Name = 'no ThemedChrome (the 1.x shim is deleted once 2.6b has no callers)'; Test = { Hits $all 'ThemedChrome' $null } }
    @{ From = '2.8'; Name = 'no MaterialSkin anywhere'; Test = { Hits $all '\bMaterial[A-Z]\w*|\bMaterialSkin\b' $null } }
    @{ From = '3.1'; Name = 'no CreateGraphics().Dpi / .DpiX / .DpiY (a window''s DPI comes from Dpi.Of / ForPopup)'; Test = {
        Hits $all 'CreateGraphics\(\)\s*\.\s*Dpi|\.Dpi[XY]\b' '^(EmojiComposer|DrawHelper)\.cs$' } }
    # Step 3.3 (made honest in Phase 3): fonts come from FontCache/FontHelper. Allowed: a PIXEL-unit font sized from
    # geometry (it is DPI-correct by construction), InlineText's fonts derived from its base font in the base font's
    # unit (by design), and DrawHelper's measuring of a font it is handed.
    @{ From = '3.3'; Name = 'no new Font( or .SizeInPoints outside FontCache / FontHelper (pixel-unit fonts from geometry allowed)'; Test = {
        @(Hits $all 'new\s+Font\s*\(|\.SizeInPoints\b' '^(FontCache|FontHelper|InlineText|DrawHelper|RoundProbeForm)\.cs$' |
          Where-Object { $_ -notmatch 'GraphicsUnit\.Pixel' }) } }
    # Step 3.3: a 96-DPI pixel literal in a Size/Point/Padding/Rectangle. Honest version: the ARGUMENTS are parsed and
    # only an integer >= 2 that is not inside Px(...)/P(...) and not part of arithmetic on something already scaled
    # (/ * %) counts. 0 and 1 (origins, hairlines) and LogicalMinimumSize (logical by design) do not.
    @{ From = '3.3'; Name = 'no 96-DPI int literals in new Size/Point/Padding/Rectangle under UI\ (use Px)'; Test = {
        @(LiteralHits $ui '^(RoundProbeForm|DesignGalleryForm|TextBoxProbe|DialogProbe|SettingsProbe|InputLabForm)\.cs$') } }
    @{ From = '5.1'; Name = 'no Color.FromArgb( outside Palette.cs / ColorMath.cs / DrawHelper.cs'; Test = { Hits $all 'Color\.FromArgb\s*\(' '^(Palette|ColorMath|DrawHelper)\.cs$' } }
    @{ From = '5.1'; Name = 'no registry read inside a paint method (read ThemeHelper.Palette)'; Test = {
        @(PaintBodies $all | Where-Object { $_ -match 'Registry\.|GetWindowsAccentColor\s*\(|IsDarkMode\s*\(' }) } }
)

$cur = [version]($Step + '.0')
$failed = 0
""
"TelegArm checks at step $Step"
foreach ($g in $gates) {
    $hits = @(& $g.Test)
    $active = $g.From -eq 'always' -or ([version]($g.From + '.0')) -le $cur
    if (-not $active) { "  pending ({0,-6}) {1}  [{2} today]" -f $g.From, $g.Name, $hits.Count; continue }
    if ($hits.Count -eq 0) { "  PASS             $($g.Name)"; continue }
    "  FAIL             $($g.Name)  [$($hits.Count)]"
    $hits | Select-Object -First 15 | ForEach-Object { "                     $_" }
    if ($hits.Count -gt 15) { "                     ... and $($hits.Count - 15) more" }
    $failed++
}

if (-not $NoSelfTest) {
    ""
    $st = @('Release', 'Debug') | ForEach-Object { Join-Path $root "TelegArm.SelfTest\bin\$_\TelegArm.SelfTest.exe" } | Where-Object { Test-Path -LiteralPath $_ } |
          Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Select-Object -Last 1
    if (-not $st) { "  FAIL             SelfTest is not built (build TelegArm.sln)"; $failed++ }
    else {
        "SelfTest: $(Rel $st)"
        & $st | ForEach-Object { "  $_" }
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }
}
""
if ($failed -eq 0) { "All enforced checks passed." } else { "$failed check(s) FAILED." }
exit ([int]($failed -ne 0))
