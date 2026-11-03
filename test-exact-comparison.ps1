# Compare working vs failing approaches
Write-Host "=== TEST 1: Original working method ===" -ForegroundColor Cyan

try {
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    $rules = $policy.Rules
    
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    
    $rule.Name = "AppLock Test 1 (Outbound)"
    $rule.Description = "Test rule for AppLock"
    $rule.Grouping = "AppLock Connection Firewall"
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    $rule.Enabled = $true
    $rule.Profiles = 0x7FFFFFFF
    $rule.Protocol = 256
    
    $rules.Add($rule)
    Write-Host "SUCCESS!" -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n=== TEST 2: With Description like C# ===" -ForegroundColor Cyan

try {
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    $rules = $policy.Rules
    
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    
    $rule.Name = "AppLock Test 2 (Outbound)"
    $rule.Description = "AppLock-managed|id=12345678-1234-1234-1234-123456789abc"
    $rule.Grouping = "AppLock Connection Firewall"
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    $rule.Enabled = $true
    $rule.Profiles = 7
    $rule.Protocol = 256
    
    $rules.Add($rule)
    Write-Host "SUCCESS!" -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n=== TEST 3: Without Description ===" -ForegroundColor Cyan

try {
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    $rules = $policy.Rules
    
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    
    $rule.Name = "AppLock Test 3 (Outbound)"
    # Skip Description
    $rule.Grouping = "AppLock Connection Firewall"
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    $rule.Enabled = $true
    $rule.Profiles = 7
    $rule.Protocol = 256
    
    $rules.Add($rule)
    Write-Host "SUCCESS!" -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n=== TEST 4: Without Grouping ===" -ForegroundColor Cyan

try {
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    $rules = $policy.Rules
    
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    
    $rule.Name = "AppLock Test 4 (Outbound)"
    $rule.Description = "Test"
    # Skip Grouping
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    $rule.Enabled = $true
    $rule.Profiles = 7
    $rule.Protocol = 256
    
    $rules.Add($rule)
    Write-Host "SUCCESS!" -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host "`n=== TEST 5: Only required properties ===" -ForegroundColor Cyan

try {
    $policyType = [Type]::GetTypeFromProgID("HNetCfg.FwPolicy2")
    $policy = [Activator]::CreateInstance($policyType)
    $rules = $policy.Rules
    
    $ruleType = [Type]::GetTypeFromProgID("HNetCfg.FWRule")
    $rule = [Activator]::CreateInstance($ruleType)
    
    $rule.Name = "AppLock Test 5 (Outbound)"
    $rule.ApplicationName = "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    $rule.Direction = 2
    $rule.Action = 0
    
    $rules.Add($rule)
    Write-Host "SUCCESS!" -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
}

Read-Host "`nPress Enter to cleanup"
Get-NetFirewallRule -DisplayName "AppLock Test*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Cleaned up"
