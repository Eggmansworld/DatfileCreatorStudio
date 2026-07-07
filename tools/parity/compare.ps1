# Parity harness: run the Python suite and the C# engine over the same test
# collection with identical settings, then compare every output dat byte for
# byte. Run from anywhere:  pwsh tools/parity/compare.ps1
$ErrorActionPreference = "Stop"

# Both engines emit UTF-8 text (em-dashes in analyzer findings etc.) — make
# sure PowerShell captures it as such on both sides
$env:PYTHONIOENCODING = "utf-8"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$parityOut = Join-Path $repo "parity-out"
$collection = Join-Path $parityOut "TestCollection"
$runsDir = Join-Path $parityOut "runs"
$settingsDir = Join-Path $parityOut "settings"
$runnerProj = Join-Path $repo "tools\ParityRunner"

# ── Build test collection + C# runner ────────────────────────────────────
python (Join-Path $PSScriptRoot "make_test_collection.py")
if ($LASTEXITCODE -ne 0) { throw "test collection build failed" }

dotnet build $runnerProj -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ParityRunner build failed" }
$runnerExe = Join-Path $runnerProj "bin\Release\net10.0\ParityRunner.exe"

if (Test-Path $runsDir) { Remove-Item -Recurse -Force $runsDir }
New-Item -ItemType Directory -Force $runsDir, $settingsDir | Out-Null

# ── Run matrix ────────────────────────────────────────────────────────────
$fixedDate = "2026-07-07"
$common = @{
    input_root  = $collection
    parent_name = "Parity"
    description = "Parity Test Collection"
    category    = "Test"
    version     = $fixedDate
    date        = $fixedDate
    author      = "Eggman"
    url         = "https://github.com/Eggmansworld"
    homepage    = "https://github.com/Eggmansworld"
    comment     = "parity harness"
    multithread = $true
    threads     = 4
}

$runs = @(
    @{ name = "mixed_perroot_opt2";  dat_type = "mixed";  gen_mode = "per_root"; structure = "opt2"; dat_format = "modern" }
    @{ name = "zipped_perroot_opt2"; dat_type = "zipped"; gen_mode = "per_root"; structure = "opt2"; dat_format = "modern" }
    @{ name = "mixed_pertop_opt2";   dat_type = "mixed";  gen_mode = "per_top";  structure = "opt2"; dat_format = "modern" }
    @{ name = "zipped_pertop_opt2";  dat_type = "zipped"; gen_mode = "per_top";  structure = "opt2"; dat_format = "modern" }
    @{ name = "mixed_perall";        dat_type = "mixed";  gen_mode = "per_all";  structure = "opt2"; dat_format = "modern" }
    @{ name = "zipped_perall";       dat_type = "zipped"; gen_mode = "per_all";  structure = "opt2"; dat_format = "modern" }
    @{ name = "zipped_perroot_allhashes"; dat_type = "zipped"; gen_mode = "per_root"; structure = "opt2"; dat_format = "modern";
       include_md5 = $true; include_sha256 = $true; include_blake3 = $true; incl_file_date = $true }
    @{ name = "mixed_perroot_allhashes"; dat_type = "mixed"; gen_mode = "per_root"; structure = "opt2"; dat_format = "modern";
       include_md5 = $true; include_sha256 = $true; include_blake3 = $true }
    @{ name = "mixed_perroot_legacy";    dat_type = "mixed";  gen_mode = "per_root"; structure = "opt2"; dat_format = "legacy" }
    @{ name = "zipped_perroot_machine";  dat_type = "zipped"; gen_mode = "per_root"; structure = "opt2"; dat_format = "modern"; use_machine = $true }
    # Structures 2-4 land officially in Session 2, but the writers are already
    # ported — verify them now for extra confidence.
    @{ name = "zipped_perroot_opt3"; dat_type = "zipped"; gen_mode = "per_root"; structure = "opt3"; dat_format = "modern" }
    @{ name = "zipped_perroot_opt4"; dat_type = "zipped"; gen_mode = "per_root"; structure = "opt4"; dat_format = "modern" }
    @{ name = "zipped_perroot_opt1"; dat_type = "zipped"; gen_mode = "per_root"; structure = "opt1"; dat_format = "modern" }
    @{ name = "mixed_perroot_opt3";  dat_type = "mixed";  gen_mode = "per_root"; structure = "opt3"; dat_format = "modern" }
    @{ name = "mixed_perroot_opt4";  dat_type = "mixed";  gen_mode = "per_root"; structure = "opt4"; dat_format = "modern" }
    @{ name = "mixed_perroot_opt1";  dat_type = "mixed";  gen_mode = "per_root"; structure = "opt1"; dat_format = "modern" }
    @{ name = "mixed_perroot_extfilter"; dat_type = "mixed"; gen_mode = "per_root"; structure = "opt2"; dat_format = "modern";
       ext_include = ".ima, .zip, .bin"; ext_exclude = "thumbs.db, .txt" }
)

$results = @()
foreach ($run in $runs) {
    $name = $run.name
    $pyOut = Join-Path $runsDir "$name\py"
    $csOut = Join-Path $runsDir "$name\cs"

    foreach ($side in @(@{ dir = $pyOut }, @{ dir = $csOut })) {
        New-Item -ItemType Directory -Force $side.dir | Out-Null
    }

    $pyPreview = Join-Path $runsDir "$name\py-previews"
    $csPreview = Join-Path $runsDir "$name\cs-previews"

    foreach ($pair in @(@($pyOut, "py", $pyPreview), @($csOut, "cs", $csPreview))) {
        $settings = $common.Clone()
        foreach ($k in $run.Keys) { if ($k -ne "name") { $settings[$k] = $run[$k] } }
        $settings["output_root"] = $pair[0]
        $settingsPath = Join-Path $settingsDir "$name-$($pair[1]).json"
        $settings | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

        if ($pair[1] -eq "py") {
            $log = python (Join-Path $PSScriptRoot "run_python.py") --settings $settingsPath --preview-dir $pair[2] 2>&1
        } else {
            $log = & $runnerExe --settings $settingsPath --preview-dir $pair[2] 2>&1
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[$name/$($pair[1])] engine reported errors:" -ForegroundColor Yellow
            $log | Select-Object -Last 8 | ForEach-Object { Write-Host "   $_" }
        }
    }

    # ── Compare (written dats + preview renders) ─────────────────────────
    $pyFiles = @(Get-ChildItem -Recurse -File $pyOut | ForEach-Object {
        $_.FullName.Substring($pyOut.Length + 1) })
    $csFiles = @(Get-ChildItem -Recurse -File $csOut | ForEach-Object {
        $_.FullName.Substring($csOut.Length + 1) })
    $pyFiles += @(Get-ChildItem -Recurse -File $pyPreview | ForEach-Object {
        "previews\" + $_.FullName.Substring($pyPreview.Length + 1) })
    $csFiles += @(Get-ChildItem -Recurse -File $csPreview | ForEach-Object {
        "previews\" + $_.FullName.Substring($csPreview.Length + 1) })

    $status = "PASS"
    $detail = "$($pyFiles.Count) dat(s)"
    $missing = @(Compare-Object $pyFiles $csFiles)
    if ($missing.Count -gt 0) {
        $status = "FAIL"
        $detail = "file lists differ: " + (($missing | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)" }) -join "; ")
    } else {
        $previewCount = @($pyFiles | Where-Object { $_.StartsWith("previews\") }).Count
        $detail = "$($pyFiles.Count - $previewCount) dat(s) + $previewCount preview render(s)"
        foreach ($rel in $pyFiles) {
            if ($rel.StartsWith("previews\")) {
                $sub = $rel.Substring(9)
                $a = Get-FileHash -Algorithm SHA256 (Join-Path $pyPreview $sub)
                $b = Get-FileHash -Algorithm SHA256 (Join-Path $csPreview $sub)
            } else {
                $a = Get-FileHash -Algorithm SHA256 (Join-Path $pyOut $rel)
                $b = Get-FileHash -Algorithm SHA256 (Join-Path $csOut $rel)
            }
            if ($a.Hash -ne $b.Hash) {
                $status = "FAIL"
                $detail = "byte mismatch: $rel"
                break
            }
        }
    }
    $results += [pscustomobject]@{ Run = $name; Status = $status; Detail = $detail }
    $colour = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("{0,-28} {1}  {2}" -f $name, $status, $detail) -ForegroundColor $colour
}

# ══ Incremental update scenarios ══════════════════════════════════════════
# Baseline dats are generated from the ORIGINAL collection, the collection is
# mutated (add/remove/change items), then both engines run an incremental
# update against per-side copies of the baseline dats. Compared: the updated
# dats, the dat-source dir state (.old retirement), and the carried/hashed
# summary line.
Write-Host ""
Write-Host "Incremental update scenarios:" -ForegroundColor Cyan

python (Join-Path $PSScriptRoot "make_incr_mutation.py")
if ($LASTEXITCODE -ne 0) { throw "mutation build failed" }
$mutated = Join-Path $parityOut "incr\TestCollection"

$incrRuns = @(
    @{ name = "incr_zipped";          dat_type = "zipped"; incr = @{} }
    @{ name = "incr_mixed";           dat_type = "mixed";  incr = @{} }
    @{ name = "incr_blake3_backfill"; dat_type = "zipped"; incr = @{ include_blake3 = $true } }
)

foreach ($run in $incrRuns) {
    $name = $run.name

    # Baseline dats from the original collection (C# engine; both engines are
    # already proven byte-identical on this path above)
    $baseline = Join-Path $runsDir "$name\baseline"
    New-Item -ItemType Directory -Force $baseline | Out-Null
    $baseSettings = $common.Clone()
    $baseSettings["dat_type"] = $run.dat_type
    $baseSettings["gen_mode"] = "per_root"
    $baseSettings["structure"] = "opt2"
    $baseSettings["dat_format"] = "modern"
    $baseSettings["output_root"] = $baseline
    $basePath = Join-Path $settingsDir "$name-baseline.json"
    $baseSettings | ConvertTo-Json | Set-Content -Path $basePath -Encoding UTF8
    & $runnerExe --settings $basePath | Out-Null

    $summaries = @{}
    foreach ($side in @("py", "cs")) {
        # Per-side copy of the baseline dats (retirement renames them)
        $sideDats = Join-Path $runsDir "$name\$side-dats"
        Copy-Item -Recurse -Force $baseline $sideDats
        $sideOut = Join-Path $runsDir "$name\$side"
        New-Item -ItemType Directory -Force $sideOut | Out-Null

        $settings = $common.Clone()
        $settings["input_root"] = $mutated
        $settings["output_root"] = $sideOut
        $settings["dat_type"] = $run.dat_type
        $settings["gen_mode"] = "per_root"
        $settings["structure"] = "opt2"
        $settings["dat_format"] = "modern"
        $settings["date"] = "2026-07-08"
        $settings["version"] = "2026-07-08-incr"
        $settings["incremental"] = $true
        $settings["incremental_dat_path"] = Join-Path $sideDats "TestCollection"
        $settings["retire_old_dats"] = $true
        foreach ($k in $run.incr.Keys) { $settings[$k] = $run.incr[$k] }
        $settingsPath = Join-Path $settingsDir "$name-$side.json"
        $settings | ConvertTo-Json | Set-Content -Path $settingsPath -Encoding UTF8

        if ($side -eq "py") {
            $log = python (Join-Path $PSScriptRoot "run_python.py") --settings $settingsPath 2>&1
        } else {
            $log = & $runnerExe --settings $settingsPath 2>&1
        }
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[$name/$side] engine reported errors:" -ForegroundColor Yellow
            $log | Select-Object -Last 8 | ForEach-Object { Write-Host "   $_" }
        }
        $summaryLine = @($log | Where-Object { $_ -match "Incremental summary:" })
        $summaries[$side] = if ($summaryLine.Count -gt 0) {
            ($summaryLine[0] -replace ".*Incremental summary:", "").Trim() } else { "(none)" }
    }

    # ── Compare updated dats + retired dat-source state + summary ────────
    $pyOut = Join-Path $runsDir "$name\py"
    $csOut = Join-Path $runsDir "$name\cs"
    $status = "PASS"
    $detail = ""

    $pyFiles = @(Get-ChildItem -Recurse -File $pyOut | ForEach-Object {
        $_.FullName.Substring($pyOut.Length + 1) })
    $csFiles = @(Get-ChildItem -Recurse -File $csOut | ForEach-Object {
        $_.FullName.Substring($csOut.Length + 1) })
    $missing = @(Compare-Object $pyFiles $csFiles)
    if ($missing.Count -gt 0) {
        $status = "FAIL"; $detail = "output file lists differ"
    } else {
        foreach ($rel in $pyFiles) {
            $a = Get-FileHash -Algorithm SHA256 (Join-Path $pyOut $rel)
            $b = Get-FileHash -Algorithm SHA256 (Join-Path $csOut $rel)
            if ($a.Hash -ne $b.Hash) { $status = "FAIL"; $detail = "byte mismatch: $rel"; break }
        }
    }

    if ($status -eq "PASS") {
        # Dat-source dirs must end in the same state (.old renames)
        $pyDatState = @(Get-ChildItem -Recurse -File (Join-Path $runsDir "$name\py-dats") |
            ForEach-Object { $_.Name }) | Sort-Object
        $csDatState = @(Get-ChildItem -Recurse -File (Join-Path $runsDir "$name\cs-dats") |
            ForEach-Object { $_.Name }) | Sort-Object
        if (@(Compare-Object $pyDatState $csDatState).Count -gt 0) {
            $status = "FAIL"; $detail = "retired dat-source states differ"
        }
    }

    if ($status -eq "PASS") {
        if ($summaries["py"] -ne $summaries["cs"]) {
            $status = "FAIL"
            $detail = "summary mismatch: py=[$($summaries['py'])] cs=[$($summaries['cs'])]"
        } elseif ($summaries["py"] -eq "(none)") {
            $status = "FAIL"; $detail = "no incremental summary reported"
        } else {
            $oldCount = @(Get-ChildItem -Recurse -File (Join-Path $runsDir "$name\py-dats") -Filter "*.old").Count
            $detail = "$($pyFiles.Count) dat(s), $($summaries['py']), $oldCount retired"
        }
    }

    $results += [pscustomobject]@{ Run = $name; Status = $status; Detail = $detail }
    $colour = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("{0,-28} {1}  {2}" -f $name, $status, $detail) -ForegroundColor $colour
}

# ══ Dat tools parity (Counter / Validator / Bulk Header Updater) ══════════
Write-Host ""
Write-Host "Dat tools parity:" -ForegroundColor Cyan

# Test data: generated dats + a deliberately broken dat + a minimal-header dat
$toolsData = Join-Path $parityOut "tools-data"
if (Test-Path $toolsData) { Remove-Item -Recurse -Force $toolsData }
New-Item -ItemType Directory -Force $toolsData | Out-Null
Copy-Item -Recurse (Join-Path $runsDir "zipped_perroot_opt2\py\TestCollection") (Join-Path $toolsData "dats")

$brokenDat = @'
<?xml version="1.0"?>
<datafile>
	<header>
		<name>Broken Test</name>
		<description>validator test</description>
		<version>1</version>
		<date>2026-01-01</date>
	</header>
	<game name="Bad Game">
		<description>Bad Game</description>
		<rom name="ok.bin" size="10" crc="0011aabb" sha1="da39a3ee5e6b4b0d3255bfef95601890afd80709"/>
		<rom name="bad1.bin" size="abc" crc="zz11aabb" sha1="short"/>
		<rom name="bad2.bin" crc="0011aabb" md5="nothex" sha256="aa"/>
		<rom name="bad3.bin" size="" sha1="da39a3ee5e6b4b0d3255bfef95601890afd80709" blake3="00"/>
	</game>
</datafile>
'@
Set-Content -Path (Join-Path $toolsData "broken.dat") -Value $brokenDat -Encoding UTF8 -NoNewline

$minimalDat = @'
<?xml version="1.0"?>
<datafile>
	<header>
		<name>Minimal Header (2026-01-01_RomVault)</name>
		<date>2026-01-01</date>
	</header>
	<game name="Only Game">
		<rom name="a.bin" size="4" crc="00112233" sha1="da39a3ee5e6b4b0d3255bfef95601890afd80709"/>
	</game>
</datafile>
'@
Set-Content -Path (Join-Path $toolsData "Minimal Header (2026-01-01_RomVault).xml") -Value $minimalDat -Encoding UTF8 -NoNewline

# ── Counter + Validator (read-only, shared data) ─────────────────────────
foreach ($mode in @("count", "validate")) {
    $name = "tools_$mode"
    if ($mode -eq "count") {
        $pyLines = @(python (Join-Path $PSScriptRoot "run_tools.py") count $toolsData 2>&1)
        $csLines = @(& $runnerExe --count $toolsData 2>&1)
    } else {
        $pyLines = @(python (Join-Path $PSScriptRoot "run_tools.py") validate $toolsData 2>&1)
        $csLines = @(& $runnerExe --validate $toolsData 2>&1)
    }
    $diff = @(Compare-Object $pyLines $csLines)
    if ($diff.Count -eq 0) {
        $status = "PASS"; $detail = "$($pyLines.Count) line(s) identical"
    } else {
        $status = "FAIL"
        $detail = ($diff | Select-Object -First 2 | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)" }) -join "; "
    }
    $results += [pscustomobject]@{ Run = $name; Status = $status; Detail = $detail }
    $colour = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("{0,-28} {1}  {2}" -f $name, $status, $detail) -ForegroundColor $colour
}

# ── Bulk Header Updater (mutates — per-side copies) ──────────────────────
$bhuName = "tools_bhu"
$bhuPy = Join-Path $parityOut "tools-bhu\py"
$bhuCs = Join-Path $parityOut "tools-bhu\cs"
if (Test-Path (Join-Path $parityOut "tools-bhu")) {
    Remove-Item -Recurse -Force (Join-Path $parityOut "tools-bhu")
}
Copy-Item -Recurse $toolsData $bhuPy
Copy-Item -Recurse $toolsData $bhuCs

$pyLines = @(python (Join-Path $PSScriptRoot "run_tools.py") bhu $bhuPy "2026-08-01" `
    "author=Parity Bot" "version=v2-parity" --clear comment --fp 2>&1) | Sort-Object
$csLines = @(& $runnerExe --bhu $bhuCs --bhu-date "2026-08-01" `
    --bhu-set "author=Parity Bot" --bhu-set "version=v2-parity" `
    --bhu-clear comment --bhu-fp 2>&1) | Sort-Object

$status = "PASS"; $detail = ""
$diff = @(Compare-Object $pyLines $csLines)
if ($diff.Count -gt 0) {
    $status = "FAIL"
    $detail = "detail lines differ: " + (($diff | Select-Object -First 2 | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)" }) -join "; ")
} else {
    $pyFiles = @(Get-ChildItem -Recurse -File $bhuPy | ForEach-Object {
        $_.FullName.Substring($bhuPy.Length + 1) })
    $csFiles = @(Get-ChildItem -Recurse -File $bhuCs | ForEach-Object {
        $_.FullName.Substring($bhuCs.Length + 1) })
    if (@(Compare-Object $pyFiles $csFiles).Count -gt 0) {
        $status = "FAIL"; $detail = "renamed file lists differ"
    } else {
        foreach ($rel in $pyFiles) {
            $a = Get-FileHash -Algorithm SHA256 (Join-Path $bhuPy $rel)
            $b = Get-FileHash -Algorithm SHA256 (Join-Path $bhuCs $rel)
            if ($a.Hash -ne $b.Hash) { $status = "FAIL"; $detail = "byte mismatch: $rel"; break }
        }
        if ($status -eq "PASS") {
            $detail = "$($pyFiles.Count) updated dat(s) byte-identical, $($pyLines.Count) detail line(s) equal"
        }
    }
}
$results += [pscustomobject]@{ Run = $bhuName; Status = $status; Detail = $detail }
$colour = if ($status -eq "PASS") { "Green" } else { "Red" }
Write-Host ("{0,-28} {1}  {2}" -f $bhuName, $status, $detail) -ForegroundColor $colour

# ══ File-wrangling tools parity (Merge / Packer / Extractor) ══════════════
Write-Host ""
Write-Host "File tools parity:" -ForegroundColor Cyan

# ── Merge Datfiles (writes dats — full byte parity) ──────────────────────
# Category root with first-level subfolders holding deeper dats. [ADF] merges
# two depth-2 dats; [Deep] tests depth-3 rom-name prefixing; [BIN] is skipped
# (dat present directly); [Empty] has nothing.
function New-MergeSourceDat($path, $name, $game, $rom) {
    $dat = @"
<?xml version="1.0"?>
<datafile>
	<header>
		<name>$name</name>
		<description>merge source</description>
		<version>1</version>
		<date>2026-01-01</date>
		<author>Src</author>
	</header>
	<game name="$game">
		<rom name="$rom" size="8" crc="0a0b0c0d" sha1="da39a3ee5e6b4b0d3255bfef95601890afd80709"/>
	</game>
</datafile>
"@
    # .NET file APIs treat paths literally — the [ADF]/[#-B] folder names
    # contain [ ] which PowerShell's -Path would interpret as wildcards.
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($path)) | Out-Null
    [System.IO.File]::WriteAllText($path, $dat, (New-Object System.Text.UTF8Encoding($false)))
}

$mergeSrc = Join-Path $parityOut "merge-src\Category"
if (Test-Path (Split-Path $mergeSrc)) { Remove-Item -Recurse -Force (Split-Path $mergeSrc) }
New-MergeSourceDat "$mergeSrc\[ADF]\[#-B]\a.xml"        "src-ab"   "GameAB"   "ab.adf"
New-MergeSourceDat "$mergeSrc\[ADF]\[C-D]\c.xml"        "src-cd"   "GameCD"   "cd.adf"
New-MergeSourceDat "$mergeSrc\[Deep]\[X]\[A-F]\d.xml"   "src-deep" "GameDeep" "deep.adf"
New-MergeSourceDat "$mergeSrc\[BIN]\direct.xml"         "src-bin"  "GameBin"  "bin.rom"
New-Item -ItemType Directory -Force "$mergeSrc\[Empty]" | Out-Null

$mergePy = Join-Path $parityOut "merge-run\py\Category"
$mergeCs = Join-Path $parityOut "merge-run\cs\Category"
if (Test-Path (Split-Path (Split-Path $mergePy))) { Remove-Item -Recurse -Force (Split-Path (Split-Path $mergePy)) }
Copy-Item -Recurse $mergeSrc $mergePy
Copy-Item -Recurse $mergeSrc $mergeCs

$pyLines = @(python (Join-Path $PSScriptRoot "run_tools.py") merge $mergePy "2026-08-01" 2>&1) | Sort-Object
$csLines = @(& $runnerExe --merge $mergeCs --merge-date "2026-08-01" 2>&1) | Sort-Object

$status = "PASS"; $detail = ""
if (@(Compare-Object $pyLines $csLines).Count -gt 0) {
    $status = "FAIL"; $detail = "merge report lines differ"
} else {
    $pyFiles = @(Get-ChildItem -Recurse -File $mergePy | ForEach-Object { $_.FullName.Substring($mergePy.Length + 1) })
    $csFiles = @(Get-ChildItem -Recurse -File $mergeCs | ForEach-Object { $_.FullName.Substring($mergeCs.Length + 1) })
    if (@(Compare-Object $pyFiles $csFiles).Count -gt 0) {
        $status = "FAIL"; $detail = "merged file lists differ"
    } else {
        foreach ($rel in $pyFiles) {
            $a = Get-FileHash -Algorithm SHA256 (Join-Path $mergePy $rel)
            $b = Get-FileHash -Algorithm SHA256 (Join-Path $mergeCs $rel)
            if ($a.Hash -ne $b.Hash) { $status = "FAIL"; $detail = "byte mismatch: $rel"; break }
        }
        if ($status -eq "PASS") {
            $mergedN = @($pyLines | Where-Object { $_ -match "^merge\|" }).Count
            $detail = "$mergedN merged dat(s) byte-identical"
        }
    }
}
$results += [pscustomobject]@{ Run = "tools_merge"; Status = $status; Detail = $detail }
$colour = if ($status -eq "PASS") { "Green" } else { "Red" }
Write-Host ("{0,-28} {1}  {2}" -f "tools_merge", $status, $detail) -ForegroundColor $colour

# ── ZIP Store Packer (entry listing parity) ──────────────────────────────
$packSrc = Join-Path $parityOut "pack-src"
if (Test-Path $packSrc) { Remove-Item -Recurse -Force $packSrc }
New-Item -ItemType Directory -Force "$packSrc\sub" | Out-Null
Set-Content -Path "$packSrc\one.bin"     -Value "content of one bin file"          -Encoding Ascii -NoNewline
Set-Content -Path "$packSrc\two.rom"     -Value "another rom file with more bytes" -Encoding Ascii -NoNewline
Set-Content -Path "$packSrc\sub\three.bin" -Value "nested bin"                     -Encoding Ascii -NoNewline
Set-Content -Path "$packSrc\keep.txt"    -Value "not a target extension"           -Encoding Ascii -NoNewline

$packPy = Join-Path $parityOut "pack-run\py"
$packCs = Join-Path $parityOut "pack-run\cs"
if (Test-Path (Split-Path $packPy)) { Remove-Item -Recurse -Force (Split-Path $packPy) }
Copy-Item -Recurse $packSrc $packPy
Copy-Item -Recurse $packSrc $packCs

$pyLines = @(python (Join-Path $PSScriptRoot "run_tools.py") pack $packPy "bin,rom" 2>&1) | Sort-Object
$csLines = @(& $runnerExe --pack $packCs --pack-exts "bin,rom" 2>&1) | Sort-Object
$status = "PASS"; $detail = ""
if (@(Compare-Object $pyLines $csLines).Count -gt 0) {
    $status = "FAIL"
    $detail = ((Compare-Object $pyLines $csLines | Select-Object -First 2 | ForEach-Object {
        "$($_.SideIndicator) $($_.InputObject)" }) -join "; ")
} else {
    $pyLeft = @(Get-ChildItem -Recurse -File $packPy).Count
    $detail = "$($pyLines.Count) zip entr(ies) identical, $pyLeft file(s) remain"
}
$results += [pscustomobject]@{ Run = "tools_pack"; Status = $status; Detail = $detail }
$colour = if ($status -eq "PASS") { "Green" } else { "Red" }
Write-Host ("{0,-28} {1}  {2}" -f "tools_pack", $status, $detail) -ForegroundColor $colour

# ── Recursive Archive Extractor (tree parity; requires 7z) ───────────────
$sevenZip = "C:\Program Files\7-Zip-Zstandard\7z.exe"
if (Test-Path $sevenZip) {
    $exPy = Join-Path $parityOut "extract-run\py"
    $exCs = Join-Path $parityOut "extract-run\cs"
    if (Test-Path (Split-Path $exPy)) { Remove-Item -Recurse -Force (Split-Path $exPy) }
    Copy-Item -Recurse (Join-Path $repo "test_rvzstd_archives") $exPy
    Copy-Item -Recurse (Join-Path $repo "test_rvzstd_archives") $exCs

    $pyLines = @(python (Join-Path $PSScriptRoot "run_tools.py") extract $exPy $sevenZip 2>&1) | Sort-Object
    $csLines = @(& $runnerExe --extract $exCs --sevenzip $sevenZip 2>&1) | Sort-Object
    $status = "PASS"; $detail = ""
    if (@(Compare-Object $pyLines $csLines).Count -gt 0) {
        $status = "FAIL"
        $detail = ((Compare-Object $pyLines $csLines | Select-Object -First 2 | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)" }) -join "; ")
    } else {
        $detail = "$($pyLines.Count) extracted path(s) identical"
    }
    $results += [pscustomobject]@{ Run = "tools_extract"; Status = $status; Detail = $detail }
    $colour = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("{0,-28} {1}  {2}" -f "tools_extract", $status, $detail) -ForegroundColor $colour
} else {
    Write-Host ("{0,-28} {1}  {2}" -f "tools_extract", "SKIP", "7z-zstd not found") -ForegroundColor DarkGray
}

# ══ Folder Structure Analyzer parity ══════════════════════════════════════
Write-Host ""
Write-Host "Analyzer parity:" -ForegroundColor Cyan
foreach ($atype in @("mixed", "zipped")) {
    $name = "analyzer_$atype"
    $pyLines = @(python (Join-Path $PSScriptRoot "run_analyzer.py") $collection $atype 2>&1)
    $csLines = @(& $runnerExe --analyze $collection --dat-type $atype 2>&1)
    $diff = @(Compare-Object $pyLines $csLines)
    if ($diff.Count -eq 0) {
        $status = "PASS"; $detail = "$($pyLines.Count) finding line(s) identical"
    } else {
        $status = "FAIL"
        $detail = ($diff | Select-Object -First 2 | ForEach-Object {
            "$($_.SideIndicator) $($_.InputObject)" }) -join "; "
    }
    $results += [pscustomobject]@{ Run = $name; Status = $status; Detail = $detail }
    $colour = if ($status -eq "PASS") { "Green" } else { "Red" }
    Write-Host ("{0,-28} {1}  {2}" -f $name, $status, $detail) -ForegroundColor $colour
}

Write-Host ""
$failed = @($results | Where-Object Status -ne "PASS")
if ($failed.Count -eq 0) {
    Write-Host "ALL $($results.Count) PARITY RUNS PASSED — output is byte-identical." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($failed.Count) of $($results.Count) runs FAILED." -ForegroundColor Red
    exit 1
}
