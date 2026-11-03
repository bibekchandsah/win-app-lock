# Test different profile values
Write-Host "Testing profile values..." -ForegroundColor Cyan

$profileTests = @(
    @{ Name = "int.MaxValue"; Value = [int]::MaxValue },
    @{ Name = "0x7FFFFFFF"; Value = 0x7FFFFFFF },
    @{ Name = "7 (All profiles)"; Value = 7 },
    @{ Name = "Domain(1) + Private(2) + Public(4)"; Value = 1 + 2 + 4 }
)

foreach ($test in $profileTests) {
    Write-Host "`nTesting: $($test.Name) = $($test.Value)" -ForegroundColor Yellow
    
    try {
        $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
        $policy = [Activator]::CreateInstance($policyType)
        $rules = $policy.Rules
        
        $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
        $rule = [Activator]::CreateInstance($ruleType)
        
        $rule.Name = "Test Rule - $($test.Name)"
        $rule.Grouping = "AppLock Connection Firewall"
        $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        $rule.Direction = 2
        $rule.Action = 0
        $rule.Enabled = $true
        $rule.Profiles = $test.Value
        $rule.Protocol = 256
        
        $rules.Add($rule)
        Write-Host "  SUCCESS!" -ForegroundColor Green
        
    } catch {
        Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host "`nCleaning up..."
Get-NetFirewallRule -Group "AppLock Connection Firewall" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Done"
