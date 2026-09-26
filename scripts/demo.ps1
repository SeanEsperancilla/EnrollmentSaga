# Drives Steps 6-8 against running services (Windows / PowerShell).
# Usage: ./scripts/demo.ps1 [happy|compensate|check|all]
param([string]$Step = "all")

$Api       = if ($env:API) { $env:API } else { "http://localhost:5080" }
$Section   = "11111111-1111-1111-1111-111111111111"   # seeded CS101
$SaPassword = if ($env:SA_PASSWORD) { $env:SA_PASSWORD } else { "Your_password123" }

function Enroll($studentId, $tuition) {
    $body = @{ studentId = $studentId; sectionId = $Section; tuition = $tuition } | ConvertTo-Json
    Invoke-RestMethod -Method Post -Uri "$Api/enrollments" -ContentType "application/json" -Body $body |
        ConvertTo-Json -Compress
}

function Show-State { Invoke-RestMethod "$Api/state" | ConvertTo-Json -Depth 5 }

function Check {
    Write-Host "== Step 8: both databases, side by side"
    Get-Content "$PSScriptRoot/consistency.sql" -Raw |
        docker exec -i sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P $SaPassword -W
}

switch ($Step) {
    "happy"      { Write-Host "== Step 6: happy path"; Enroll "2026-00123" 15000; Start-Sleep 1; Show-State }
    "compensate" { Write-Host "== Step 7: compensation path"; Enroll "2026-00124" 50000; Start-Sleep 1; Show-State }
    "check"      { Check }
    default {
        Write-Host "== Step 6: happy path"; Enroll "2026-00123" 15000
        Write-Host "== Step 7: compensation path"; Enroll "2026-00124" 50000
        Start-Sleep 1; Check
    }
}
