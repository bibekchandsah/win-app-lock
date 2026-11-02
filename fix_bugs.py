import os

# Fix BitLockerPage.xaml.cs - RestartAsAdminButton_Click
cs_path = r'd:\programming exercise\winUI\win app lock\src\AppLock\Views\BitLockerPage.xaml.cs'
with open(cs_path, 'r', encoding='utf-8') as f:
    content = f.read()

old = '''    private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null) return;
            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            System.Diagnostics.Process.Start(psi);
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart as admin: {ex.Message}");
        }
    }'''

new = '''    private void RestartAsAdminButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Already elevated — nothing to do
            if (ServiceContainer.BitLockerSupportService.IsElevated) return;

            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe == null) return;

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            System.Diagnostics.Process.Start(psi);

            // Hide our window immediately so the user only sees the elevated window,
            // then terminate this non-elevated process forcefully (Application.Current.Exit()
            // is async in WinUI 3 and can show two windows briefly).
            var mainWin = App.MainWindowInstance;
            mainWin?.Hide();

            ServiceContainer.Reset();
            Environment.Exit(0);
        }
        catch (OperationCanceledException) { /* user cancelled UAC prompt */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to restart as admin: {ex.Message}");
        }
    }'''

if old in content:
    content = content.replace(old, new)
    with open(cs_path, 'w', encoding='utf-8') as f:
        f.write(content)
    print("BitLockerPage.xaml.cs patched OK")
else:
    print("ERROR: target block not found in BitLockerPage.xaml.cs")
    # Show what the file contains around RestartAsAdmin
    idx = content.find('RestartAsAdminButton_Click')
    print(repr(content[max(0,idx-50):idx+500]))

