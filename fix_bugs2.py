cs_path = r'd:\programming exercise\winUI\win app lock\src\AppLock\Views\BitLockerPage.xaml.cs'
with open(cs_path, 'r', encoding='utf-8') as f:
    content = f.read()

old = '''            System.Diagnostics.Process.Start(psi);

            // Hide our window immediately so the user only sees the elevated window,
            // then terminate this non-elevated process forcefully (Application.Current.Exit()
            // is async in WinUI 3 and can show two windows briefly).
            var mainWin = App.MainWindowInstance;
            mainWin?.Hide();

            ServiceContainer.Reset();
            Environment.Exit(0);'''

new = '''            System.Diagnostics.Process.Start(psi);

            // Terminate this non-elevated process forcefully.
            // Environment.Exit is synchronous, ensuring only the elevated window remains.
            ServiceContainer.Reset();
            Environment.Exit(0);'''

if old in content:
    content = content.replace(old, new)
    with open(cs_path, 'w', encoding='utf-8') as f:
        f.write(content)
    print("BitLockerPage.xaml.cs patch 2 OK")
else:
    print("ERROR: block not found")
    idx = content.find('System.Diagnostics.Process.Start(psi)')
    print(repr(content[max(0,idx-50):idx+600]))
