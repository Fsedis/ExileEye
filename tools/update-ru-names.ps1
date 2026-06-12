# Regenerates src/ExileEye/data/ru-names.json: English poe.ninja display name → official
# Russian client name. Sources:
#   - poe.ninja PoE2 exchange API (the current item set)
#   - Exiled Exchange 2's ru/items.ndjson (GGG's official translations, refName → name)
# Run after a new league launches or when poe.ninja adds items.
#   powershell -File tools/update-ru-names.ps1 [-League "Runes of Aldur"]
param([string]$League = "Runes of Aldur")

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$outFile = Join-Path $repoRoot 'src\ExileEye\data\ru-names.json'

$headers = @{
    'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36'
    'Referer'    = 'https://poe.ninja/poe2/economy'
}

Write-Host "Fetching current item names from poe.ninja ($League)..."
$ninjaNames = @()
foreach ($type in 'Currency', 'Runes', 'Expedition', 'Verisium') {
    $url = "https://poe.ninja/poe2/api/economy/exchange/current/overview?league=$([uri]::EscapeDataString($League))&type=$type"
    $resp = Invoke-WebRequest -Uri $url -Headers $headers -UseBasicParsing
    $json = $resp.Content | ConvertFrom-Json
    $ninjaNames += $json.items | ForEach-Object { $_.name }
}
$ninjaNames = $ninjaNames | Sort-Object -Unique
Write-Host "  $($ninjaNames.Count) unique items"

Write-Host "Fetching Russian translations from Exiled Exchange 2..."
$ndjsonPath = Join-Path $env:TEMP 'ee2_ru_items.ndjson'
Invoke-WebRequest -Uri 'https://raw.githubusercontent.com/Kvan7/Exiled-Exchange-2/master/renderer/public/data/ru/items.ndjson' `
    -OutFile $ndjsonPath -UseBasicParsing

Add-Type -AssemblyName System.Web.Extensions
$ser = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$ser.MaxJsonLength = [int]::MaxValue
$ruByRef = @{}
foreach ($line in [System.IO.File]::ReadAllLines($ndjsonPath, [System.Text.Encoding]::UTF8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $o = $ser.DeserializeObject($line)
    if ($o['refName'] -and $o['name'] -and -not $ruByRef.ContainsKey($o['refName'])) {
        $ruByRef[$o['refName']] = $o['name']
    }
}
Write-Host "  $($ruByRef.Count) translation entries"

$map = [ordered]@{}
$missing = @()
foreach ($name in $ninjaNames) {
    if ($ruByRef.ContainsKey($name)) { $map[$name] = $ruByRef[$name]; continue }
    # Level-suffixed items ("Thaumaturgic Flux (Level 8)") are stored without the suffix.
    if ($name -match '^(.*) \(Level (\d+)\)$' -and $ruByRef.ContainsKey($Matches[1])) {
        $map[$name] = "$($ruByRef[$Matches[1]]) (Уровень $($Matches[2]))"
        continue
    }
    $missing += $name
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine('{')
$i = 0
foreach ($key in $map.Keys) {
    $en = $key -replace '\\', '\\\\' -replace '"', '\"'
    $ru = $map[$key] -replace '\\', '\\\\' -replace '"', '\"'
    $comma = if ($i -lt $map.Count - 1) { ',' } else { '' }
    [void]$sb.AppendLine("  `"$en`": `"$ru`"$comma")
    $i++
}
[void]$sb.AppendLine('}')
New-Item -ItemType Directory -Force (Split-Path $outFile) | Out-Null
[System.IO.File]::WriteAllText($outFile, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))

Write-Host "Wrote $($map.Count) names to $outFile"
if ($missing.Count -gt 0) {
    Write-Warning "$($missing.Count) items have no translation:"
    $missing | ForEach-Object { Write-Warning "  $_" }
}
