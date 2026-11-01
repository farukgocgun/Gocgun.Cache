# Publishes Gocgun.Cache to nuget.org: dispatches the manual release.yml workflow of every
# dependency first (in dependency order), then of this repository, waiting for each run.
# Requires the GitHub CLI (gh auth login). No secrets are read or stored.
$ErrorActionPreference = 'Stop'
$owner = 'farukgocgun'
# Dependency order: bases first, this package last.
$repos = @('Gocgun.Cache')
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) is required.' }
gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "Run 'gh auth login' first." }
foreach ($repo in $repos) {
  if ($repo -eq 'Gocgun.Diagnostics.Shared') { Write-Host "Skipping $repo: it is not packed and has no release workflow."; continue }
  Write-Host "==> Dispatching release.yml in $owner/$repo (publish=true)"
  $before = gh run list -R "$owner/$repo" --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId // 0'
  gh workflow run release.yml -R "$owner/$repo" -f publish=true
  if ($LASTEXITCODE -ne 0) { throw "Dispatch of $repo failed." }
  $runId = ''
  for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 2
    $current = gh run list -R "$owner/$repo" --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId // 0'
    if ($current -ne $before -and $current -ne '0') { $runId = $current; break }
  }
  if (-not $runId) { throw "Could not find the new run of $repo." }
  gh run watch $runId -R "$owner/$repo" --exit-status
  if ($LASTEXITCODE -ne 0) { throw "Release of $repo failed; stopping." }
}
Write-Host 'Done.'
