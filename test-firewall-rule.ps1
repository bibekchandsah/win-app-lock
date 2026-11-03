# Test creating a firewall rule directly via COM
# Run as Administrator

Write-Host "Testing COM Firewall API..." -ForegroundColor Cyan

try {
    # Create policy object
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    Write-Host "Created FwPolicy2 instance" -ForegroundColor Green
    
    # Get rules collection
    $rules = $policy.Rules
    Write-Host "Got Rules collection, count: $($rules.Count)" -ForegroundColor Green
    
    # Create a test rule
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    Write-Host "Created FWRule instance" -ForegroundColor Green
    
    # Set rule properties
    $rule.Name = "AppLock Test Rule (Outbound)"
    $rule.Description = "Test rule for AppLock"
    $rule.Grouping = "AppLock Connection Firewall"
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    $rule.Enabled = $true
    $rule.Profiles = 0x7FFFFFFF
    $rule.Protocol = 256
    Write-Host "Set rule properties" -ForegroundColor Green
    
    # Add rule
    Write-Host "Adding rule to firewall..." -ForegroundColor Yellow
    $rules.Add($rule)
    Write-Host "Rule added successfully!" -ForegroundColor Green
    
    # Verify
    Write-Host ""
    Write-Host "Verifying rule was created..." -ForegroundColor Cyan
    $verification = Get-NetFirewallRule -Group "AppLock Connection Firewall" -ErrorAction SilentlyContinue
    if ($verification) {
        Write-Host "SUCCESS - Rule verified in firewall!" -ForegroundColor Green
        $verification | Format-Table DisplayName, Enabled, Direction, Action
    } else {
        Write-Host "FAILED - Rule not found in firewall" -ForegroundColor Red
    }
    
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "Press Enter to clean up test rule..."
Read-Host

# Cleanup
try {
    Remove-NetFirewallRule -DisplayName "AppLock Test Rule (Outbound)" -ErrorAction SilentlyContinue
    Write-Host "Test rule removed" -ForegroundColor Green
} catch {
    Write-Host "Could not remove test rule" -ForegroundColor Yellow
}
