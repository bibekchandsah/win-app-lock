param([string]$Image = "scratch/qr-test.png")

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$path = Join-Path (Get-Location) $Image
$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = 'None'
$form.WindowState = 'Maximized'
$form.BackColor = [System.Drawing.Color]::White
$form.BackgroundImage = [System.Drawing.Image]::FromFile($path)
$form.BackgroundImageLayout = 'Center'
$form.ShowInTaskbar = $false

# The island stays above because it is topmost and this form is not.
[System.Windows.Forms.Application]::Run($form)
