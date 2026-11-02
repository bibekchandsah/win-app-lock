using AppLock.Models;
using AppLock.Services.BitLocker;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AppLock.Views;

public sealed partial class EnableBitLockerDialog : ContentDialog
{
    private int _currentStep = 1;
    private bool _hasBackedUpKey = false;
    private readonly IReadOnlyList<BitLockerDrive> _drives;
    private readonly string? _initialDriveLetter;

    public BitLockerTurnOnRequest? ResultRequest { get; private set; }
    public bool IsConfirmed { get; private set; }

    public EnableBitLockerDialog(IReadOnlyList<BitLockerDrive> drives, string? initialDriveLetter = null)
    {
        InitializeComponent();
        _drives = drives;
        _initialDriveLetter = initialDriveLetter;

        PopulateDrives();
        UpdateStepUi();

        // Generate initial recovery key
        RecoveryKeyTextBox.Text = BitLockerService.GenerateValidRecoveryPassword();
    }

    private void PopulateDrives()
    {
        DriveComboBox.Items.Clear();
        int selectIndex = 0;
        int i = 0;

        foreach (var drive in _drives)
        {
            var item = new ComboBoxItem
            {
                Content = $"{drive.DisplayName} • {drive.ProtectionStatus}",
                Tag = drive
            };

            DriveComboBox.Items.Add(item);

            if (!string.IsNullOrEmpty(_initialDriveLetter) &&
                string.Equals(drive.DriveLetter, _initialDriveLetter, StringComparison.OrdinalIgnoreCase))
            {
                selectIndex = i;
            }
            i++;
        }

        if (DriveComboBox.Items.Count > 0)
        {
            DriveComboBox.SelectedIndex = selectIndex;
        }
    }

    private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveComboBox.SelectedItem is ComboBoxItem item && item.Tag is BitLockerDrive drive)
        {
            bool isOs = drive.DriveType == AppLock.Models.DriveType.OperatingSystem;
            OsDriveWarningBar.IsOpen = isOs;
            IsPrimaryButtonEnabled = !isOs;
        }
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var pwd = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;

        UpdatePasswordStrength(pwd);

        if (string.IsNullOrEmpty(pwd))
        {
            PasswordErrorText.Visibility = Visibility.Collapsed;
            IsPrimaryButtonEnabled = false;
            return;
        }

        if (pwd.Length < 8)
        {
            PasswordErrorText.Text = "Password must be at least 8 characters long.";
            PasswordErrorText.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = false;
            return;
        }

        if (!string.IsNullOrEmpty(confirm) && pwd != confirm)
        {
            PasswordErrorText.Text = "Passwords do not match.";
            PasswordErrorText.Visibility = Visibility.Visible;
            IsPrimaryButtonEnabled = false;
            return;
        }

        PasswordErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled = !string.IsNullOrEmpty(confirm) && pwd == confirm;
    }

    private void UpdatePasswordStrength(string pwd)
    {
        if (string.IsNullOrEmpty(pwd))
        {
            StrengthBar.Value = 0;
            StrengthText.Text = "";
            return;
        }

        int score = 0;
        if (pwd.Length >= 8) score += 25;
        if (pwd.Length >= 12) score += 15;
        if (pwd.Any(char.IsUpper)) score += 20;
        if (pwd.Any(char.IsLower)) score += 20;
        if (pwd.Any(char.IsDigit)) score += 10;
        if (pwd.Any(ch => !char.IsLetterOrDigit(ch))) score += 10;

        StrengthBar.Value = Math.Clamp(score, 0, 100);
        StrengthText.Text = score switch
        {
            < 50 => "Weak",
            < 80 => "Medium",
            _ => "Strong"
        };
    }

    private void SaveToFileButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var drive = GetSelectedDrive();
            var letter = drive?.DriveLetter ?? "Drive";
            var cleanLetter = letter.TrimEnd('\\', ':');
            var key = RecoveryKeyTextBox.Text;

            var defaultFileName = $"BitLocker Recovery Key {cleanLetter}.txt";
            var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            var filePath = Path.Combine(desktopPath, defaultFileName);

            var content = $"BitLocker Drive Encryption Recovery Key\n" +
                          $"----------------------------------------\n" +
                          $"Drive: {letter}\n" +
                          $"Date Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                          $"Recovery Key: {key}\n\n" +
                          $"IMPORTANT: Store this key in a secure location separate from your computer.\n";

            File.WriteAllText(filePath, content);

            _hasBackedUpKey = true;
            BackupStatusInfoBar.Title = "Recovery Key Saved";
            BackupStatusInfoBar.Message = $"Saved to Desktop as '{defaultFileName}'.";
            BackupStatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = true;
        }
        catch (Exception ex)
        {
            BackupStatusInfoBar.Severity = InfoBarSeverity.Error;
            BackupStatusInfoBar.Title = "Failed to Save";
            BackupStatusInfoBar.Message = ex.Message;
            BackupStatusInfoBar.IsOpen = true;
        }
    }

    private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(RecoveryKeyTextBox.Text);
            Clipboard.SetContent(package);

            // Auto-clear clipboard after 60 seconds per Section 6 & 5.6
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                try { Clipboard.Clear(); } catch { }
            };
            timer.Start();

            _hasBackedUpKey = true;
            BackupStatusInfoBar.Title = "Copied to Clipboard";
            BackupStatusInfoBar.Message = "Recovery key copied. Clipboard will automatically clear in 60 seconds.";
            BackupStatusInfoBar.IsOpen = true;
            IsPrimaryButtonEnabled = true;
        }
        catch (Exception ex)
        {
            BackupStatusInfoBar.Severity = InfoBarSeverity.Error;
            BackupStatusInfoBar.Title = "Copy Failed";
            BackupStatusInfoBar.Message = ex.Message;
            BackupStatusInfoBar.IsOpen = true;
        }
    }

    private void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_currentStep == 1)
        {
            args.Cancel = true;
            _currentStep = 2;
            UpdateStepUi();
        }
        else if (_currentStep == 2)
        {
            args.Cancel = true;
            _currentStep = 3;
            UpdateStepUi();
        }
        else if (_currentStep == 3)
        {
            if (!_hasBackedUpKey)
            {
                args.Cancel = true;
                return;
            }

            // Build request
            var drive = GetSelectedDrive();
            if (drive == null) return;

            ResultRequest = new BitLockerTurnOnRequest
            {
                DriveLetter = drive.DriveLetter,
                Passphrase = NewPasswordBox.Password,
                RecoveryPassword = RecoveryKeyTextBox.Text,
                Scope = ScopeUsedSpaceRadio.IsChecked == true ? BitLockerEncryptionScope.UsedSpaceOnly : BitLockerEncryptionScope.EntireDrive,
                Method = MethodComboBox.SelectedIndex switch
                {
                    1 => BitLockerEncryptionMethod.XtsAes256,
                    2 => BitLockerEncryptionMethod.AesCbc128,
                    3 => BitLockerEncryptionMethod.AesCbc256,
                    _ => BitLockerEncryptionMethod.XtsAes128
                }
            };

            IsConfirmed = true;
        }
    }

    private void SecondaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_currentStep > 1)
        {
            args.Cancel = true;
            _currentStep--;
            UpdateStepUi();
        }
    }

    private void UpdateStepUi()
    {
        Step1Panel.Visibility = _currentStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _currentStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _currentStep == 3 ? Visibility.Visible : Visibility.Collapsed;

        SecondaryButtonText = _currentStep > 1 ? "Back" : "";

        if (_currentStep == 1)
        {
            StepTitleText.Text = "Choose Drive & Encryption Mode";
            StepIndicatorText.Text = "Step 1 of 3";
            PrimaryButtonText = "Next";

            var drive = GetSelectedDrive();
            IsPrimaryButtonEnabled = drive != null && drive.DriveType != AppLock.Models.DriveType.OperatingSystem;
        }
        else if (_currentStep == 2)
        {
            StepTitleText.Text = "Set BitLocker Password";
            StepIndicatorText.Text = "Step 2 of 3";
            PrimaryButtonText = "Next";

            var pwd = NewPasswordBox.Password;
            var confirm = ConfirmPasswordBox.Password;
            IsPrimaryButtonEnabled = !string.IsNullOrEmpty(pwd) && pwd.Length >= 8 && pwd == confirm;
        }
        else if (_currentStep == 3)
        {
            StepTitleText.Text = "Back Up Recovery Key";
            StepIndicatorText.Text = "Step 3 of 3";
            PrimaryButtonText = "Start Encryption";

            IsPrimaryButtonEnabled = _hasBackedUpKey;
        }
    }

    private BitLockerDrive? GetSelectedDrive()
    {
        if (DriveComboBox.SelectedItem is ComboBoxItem item && item.Tag is BitLockerDrive drive)
        {
            return drive;
        }
        return null;
    }
}

