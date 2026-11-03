# View AppLock firewall logs
$logPath = "$env:APPDATA\AppLock\logs"

Write-Host "Checking for logs in: $logPath`n" -ForegroundColor Cyan

if (Test-Path $logPath) {
    $logFiles = Get-ChildItem $logPath -Filter "*.log" | Sort-Object LastWriteTime -Descending
    
    if ($logFiles.Count -eq 0) {
        Write-Host "No log files found." -ForegroundColor Yellow
    } else {
        Write-Host "Found $($logFiles.Count) log file(s):`n" -ForegroundColor Green
        
        foreach ($file in $logFiles) {
            Write-Host "=== $($file.Name) ===" -ForegroundColor Cyan
            Write-Host "Last modified: $($file.LastWriteTime)" -ForegroundColor Gray
            Write-Host ""
            
            $content = Get-Content $file.FullName -ErrorAction SilentlyContinue
            if ($content) {
                # Show last 30 lines
                $content | Select-Object -Last 30
            } else {
                Write-Host "(empty)" -ForegroundColor Gray
            }
            Write-Host ""
        }
    }
} else {
    Write-Host "Log directory does not exist yet." -ForegroundColor Yellow
    Write-Host "Logs will be created when you use the Internet Access feature." -ForegroundColor Gray
}

Write-Host "`nPress Enter to exit..."
Read-Host
