# Test setting properties one by one to find the culprit
Write-Host "Testing property by property..." -ForegroundColor Cyan

$policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
$policy = [Activator]::CreateInstance($policyType)
$rules = $policy.Rules

$ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
$rule = [Activator]::CreateInstance($ruleType)

# Test each property in the exact order as C# code
$properties = @(
    @{ Name = "Name"; Value = "AppLock - msedge (Outbound)" },
    @{ Name = "Description"; Value = "AppLock-managed|id=12345678-1234-1234-1234-123456789abc" },
    @{ Name = "Grouping"; Value = "AppLock Connection Firewall" },
    @{ Name = "ApplicationName"; Value = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" },
    @{ Name = "Direction"; Value = 2 },
    @{ Name = "Action"; Value = 0 },
    @{ Name = "Enabled"; Value = $true },
    @{ Name = "Profiles"; Value = 7 },
    @{ Name = "Protocol"; Value = 256 }
)

foreach ($prop in $properties) {
    try {
        Write-Host "Setting $($prop.Name) = $($prop.Value)..." -NoNewline
        $rule.$($prop.Name) = $prop.Value
        Write-Host " OK" -ForegroundColor Green
    } catch {
        Write-Host " FAILED: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Stopping test - found the problem property!" -ForegroundColor Yellow
        exit
    }
}

Write-Host ""
Write-Host "All properties set successfully. Now testing rules.Add()..." -ForegroundColor Cyan
try {
    $rules.Add($rule)
    Write-Host "SUCCESS - Rule added!" -ForegroundColor Green
    
    $verification = Get-NetFirewallRule -Group "AppLock Connection Firewall" -ErrorAction SilentlyContinue
    Write-Host "Rule count: $($verification.Count)"
    
} catch {
    Write-Host "FAILED at rules.Add: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Read-Host "Press Enter to cleanup"
Get-NetFirewallRule -Group "AppLock Connection Firewall" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Cleaned up"
