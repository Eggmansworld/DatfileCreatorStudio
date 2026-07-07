# Parity harness: run the Python suite and the C# engine over the same test
# collection with identical settings, then compare every output dat byte for
# byte. Run from anywhere:  pwsh tools/parity/compare.ps1
$ErrorActionPreference = "Stop"

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

Write-Host ""
$failed = @($results | Where-Object Status -ne "PASS")
if ($failed.Count -eq 0) {
    Write-Host "ALL $($results.Count) PARITY RUNS PASSED — output is byte-identical." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($failed.Count) of $($results.Count) runs FAILED." -ForegroundColor Red
    exit 1
}
