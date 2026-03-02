param(
  [string]$RepoRoot = "C:\dev\Shoots"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Info($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "!!  $msg" -ForegroundColor Yellow }

$uiProj = Join-Path $RepoRoot "ui\Shoots.Ui\Shoots.Ui.csproj"
$uiDir  = Join-Path $RepoRoot "ui\Shoots.Ui"
$loaderDir = Join-Path $RepoRoot "src\Runtime\Shoots.Runtime.Loader"

# -----------------------------
# 0) sanity
# -----------------------------
if (-not (Test-Path $uiProj)) { throw "Missing UI project: $uiProj" }

# -----------------------------
# 1) RootFsDescriptor: allow only ONE partial record with ctor params
#    Keep ctor in ui\Shoots.Ui\ExecutionEnvironments\RootFsDescriptor.cs
#    Strip ctor parameter list everywhere else if it exists
# -----------------------------
Write-Info "Fixing RootFsDescriptor partial record constructor duplication..."

$rootFsCanonical = Join-Path $uiDir "ExecutionEnvironments\RootFsDescriptor.cs"
if (-not (Test-Path $rootFsCanonical)) {
  Write-Warn "Canonical file not found: $rootFsCanonical (skipping canonical enforcement)"
}

# Find any file that declares partial record RootFsDescriptor(
$rootFsHits = Get-ChildItem -Path $uiDir -Recurse -Filter *.cs |
  Where-Object {
    (Select-String -Path $_.FullName -Pattern "partial\s+record\s+RootFsDescriptor\s*\(" -Quiet)
  } |
  Select-Object -ExpandProperty FullName

# Strip ctor from all except canonical
foreach ($f in $rootFsHits) {
  if ($f -ieq $rootFsCanonical) { continue }

  $txt = Get-Content -LiteralPath $f -Raw
  $new = [regex]::Replace(
    $txt,
    "public\s+partial\s+record\s+RootFsDescriptor\s*\([^\)]*\)",
    "public partial record RootFsDescriptor",
    [Text.RegularExpressions.RegexOptions]::Multiline
  )

  if ($new -ne $txt) {
    Set-Content -LiteralPath $f -Value $new -NoNewline
    Write-Host "    stripped ctor params in: $f"
  }
}

# -----------------------------
# 2) MainWindowViewModel: kill CS8955 by removing file-scoped namespace
#    Convert: namespace X;
#    To:      namespace X { ... }
# -----------------------------
Write-Info "Fixing MainWindowViewModel namespace style (file-scoped -> block)..."

$mainVm = Join-Path $uiDir "ViewModels\MainWindowViewModel.cs"
if (Test-Path $mainVm) {
  $txt = Get-Content -LiteralPath $mainVm -Raw

  # If file-scoped namespace exists, convert it to block form once.
  if ($txt -match "^\s*namespace\s+Shoots\.UI\.ViewModels\s*;\s*(\r?\n)") {
    $txt2 = [regex]::Replace(
      $txt,
      "^\s*namespace\s+Shoots\.UI\.ViewModels\s*;\s*(\r?\n)",
      "namespace Shoots.UI.ViewModels$1{$1",
      [Text.RegularExpressions.RegexOptions]::Multiline
    )

    # Ensure we have one closing brace at the end
    # (Only add if not already clearly closed)
    if ($txt2 -notmatch "\r?\n\}\s*$") {
      $txt2 = $txt2.TrimEnd() + "`r`n}`r`n"
    }

    if ($txt2 -ne $txt) {
      Set-Content -LiteralPath $mainVm -Value $txt2 -NoNewline
      Write-Host "    converted file-scoped namespace to block in: $mainVm"
    }
  }
} else {
  Write-Warn "Missing file: $mainVm (skipping)"
}

# -----------------------------
# 3) Ensure UI project references Runtime.Ui.Abstractions + Runtime.Abstractions
# -----------------------------
Write-Info "Ensuring UI project references required Runtime abstraction projects..."

$uiCsproj = Get-Content -LiteralPath $uiProj -Raw

$needRefs = @(
  "..\..\src\Runtime\Shoots.Runtime.Ui.Abstractions\Shoots.Runtime.Ui.Abstractions.csproj",
  "..\..\src\Runtime\Shoots.Runtime.Abstractions\Shoots.Runtime.Abstractions.csproj",
  "..\..\src\Contracts\Shoots.Contracts.Core\Shoots.Contracts.Core.csproj"
)

foreach ($ref in $needRefs) {
  if ($uiCsproj -notmatch [regex]::Escape($ref)) {
    # Insert a new ItemGroup near the end (before </Project>)
    $insert = "  <ItemGroup>`r`n    <ProjectReference Include=`"$ref`" />`r`n  </ItemGroup>`r`n"
    $uiCsproj = $uiCsproj -replace "\r?\n</Project>\s*$", "`r`n$insert</Project>`r`n"
    Write-Host "    added ProjectReference: $ref"
  }
}

Set-Content -LiteralPath $uiProj -Value $uiCsproj -NoNewline

# -----------------------------
# 4) Loader drift quick fixes:
#    - QueryStatus -> QueryStatusAsync
#    - .Label -> .ToString() (RuntimeVersion has no Label)
# -----------------------------
Write-Info "Applying Loader drift quick fixes..."

if (Test-Path $loaderDir) {
  $loaderFiles = Get-ChildItem -Path $loaderDir -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName

  foreach ($f in $loaderFiles) {
    $txt = Get-Content -LiteralPath $f -Raw
    $new = $txt

    # QueryStatus(...) -> QueryStatusAsync(...)
    $new = $new -replace "\.QueryStatus\s*\(", ".QueryStatusAsync("
    # Common extension-ish usage: QueryStatus() -> QueryStatusAsync()
    $new = $new -replace "\.QueryStatus\s*\(\s*\)", ".QueryStatusAsync()"

    # RuntimeVersion.Label -> RuntimeVersion.ToString()
    $new = $new -replace "\.Label\b", ".ToString()"

    if ($new -ne $txt) {
      Set-Content -LiteralPath $f -Value $new -NoNewline
      Write-Host "    patched: $f"
    }
  }
} else {
  Write-Warn "Loader dir not found: $loaderDir (skipping)"
}

Write-Info "Done. Now restore/build to see what's left."