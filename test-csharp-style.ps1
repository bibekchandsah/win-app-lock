# Test with C# style parameters
Write-Host "Testing with C# style parameters..." -ForegroundColor Cyan

try {
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    $rules = $policy.Rules
    
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    
    # Use C# style name with en-dash
    $rule.Name = "AppLock - Edge Test (Outbound)"
    $rule.Description = "AppLock-managed|id=12345678-1234-1234-1234-123456789abc"
    $rule.Grouping = "AppLock Connection Firewall"
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    $rule.Enabled = $true
    $rule.Profiles = [int]::MaxValue
    $rule.Protocol = 256
    
    Write-Host "Adding rule..."
    $rules.Add($rule)
    Write-Host "Success!" -ForegroundColor Green
    
    $verification = Get-NetFirewallRule -Group "AppLock Connection Firewall" -ErrorAction SilentlyContinue
    Write-Host "Rules in group: $($verification.Count)"
    $verification | Format-Table DisplayName
    
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}

Read-Host "Press Enter to cleanup"
Get-NetFirewallRule -Group "AppLock Connection Firewall" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Cleaned up"
