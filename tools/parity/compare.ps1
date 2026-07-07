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

Write-Host ""
$failed = @($results | Where-Object Status -ne "PASS")
if ($failed.Count -eq 0) {
    Write-Host "ALL $($results.Count) PARITY RUNS PASSED — output is byte-identical." -ForegroundColor Green
    exit 0
} else {
    Write-Host "$($failed.Count) of $($results.Count) runs FAILED." -ForegroundColor Red
    exit 1
}
