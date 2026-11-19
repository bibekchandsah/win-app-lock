using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using AppLock.Services;
using AppLock.Services.Vault;
using AppLock.Services.Vault.Crypto;
using WinRT.Interop;
using SysPath = System.IO.Path;

namespace AppLock.Views.Vault;

public sealed partial class CreateVaultWizardDialog : ContentDialog
{
    // ---- State --------------------------------------------------------
    private int _step = 1;
    private const int TotalSteps = 5;

    private string? _recoveryKeyFormatted;
    private byte[]? _rawRecoveryKey;
    private int _proofGroupIndex1 = -1;
    private int _proofGroupIndex2 = -1;

    private CancellationTokenSource? _createCts;

    /// <summary>Set when creation succeeds; caller can read this.</summary>
    public Models.Vault.VaultRegistration? CreatedRegistration { get; private set; }

    // ---- Constructor --------------------------------------------------
    public CreateVaultWizardDialog()
    {
        InitializeComponent();
        UpdateStep();
    }

    // ---- Step navigation ----------------------------------------------

    private void UpdateStep()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5Panel.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;

        // Step dots — accent for current step, subdued for others
        SetDot(Dot1, 1);
        SetDot(Dot2, 2);
        SetDot(Dot3, 3);
        SetDot(Dot4, 4);
        SetDot(Dot5, 5);

        // Button labels
        bool isCreating = _step == TotalSteps;
        PrimaryButtonText = _step == TotalSteps - 1 ? "Create vault" :
                            isCreating               ? ""             :
                            "Next";
        IsPrimaryButtonEnabled   = !isCreating;
        IsSecondaryButtonEnabled = !isCreating;

        // Title
        Title = _step switch
        {
            1 => "Create Vault — Name & Location",
            2 => "Create Vault — Set Password",
            3 => "Create Vault — Recovery Key",
            4 => "Create Vault — Options",
            5 => "Creating Vault…",
            _ => "Create Vault"
        };
    }

    private void SetDot(Ellipse dot, int dotStep)
    {
        if (Application.Current.Resources.TryGetValue(
                dotStep <= _step ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush",
                out var res) && res is SolidColorBrush brush)
        {
            dot.Fill = brush;
        }
    }

    private async void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            bool canAdvance = ValidateCurrentStep();
            if (!canAdvance)
            {
                args.Cancel = true;
                return;
            }

            if (_step == TotalSteps - 1)
            {
                // "Create vault" button — advance to step 5 and start creating
                _step = TotalSteps;
                UpdateStep();
                await StartCreationAsync();
            }
            else if (_step < TotalSteps)
            {
                _step++;
                UpdateStep();
                OnStepShown();
                args.Cancel = true; // Don't close, we advanced
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnStepShown()
    {
        if (_step == 3)
            GenerateRecoveryKey();
    }

    // ---- Step 1 validation --------------------------------------------

    private bool ValidateCurrentStep()
    {
        return _step switch
        {
            1 => ValidateStep1(),
            2 => ValidateStep2(),
            3 => ValidateStep3(),
            4 => true,
            _ => false
        };
    }

    private bool ValidateStep1()
    {
        Step1ErrorText.Visibility = Visibility.Collapsed;

        var name = VaultNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ShowStep1Error("Please enter a name for the vault.");
            return false;
        }

        var path = VaultPathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            path = VaultFactory.GetDefaultVaultPath(name);
            VaultPathBox.Text = path;
        }

        var err = VaultFactory.ValidatePath(path);
        if (err != null)
        {
            PathErrorText.Text = err;
            PathErrorText.Visibility = Visibility.Visible;
            return false;
        }

        PathErrorText.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ShowStep1Error(string msg)
    {
        Step1ErrorText.Text = msg;
        Step1ErrorText.Visibility = Visibility.Visible;
    }

    private void UseAdminPasswordCheckBox_CheckChanged(object sender, RoutedEventArgs e)
    {
        if (UseAdminPasswordCheckBox.IsChecked == true)
        {
            PasswordBox.Header = "Enter your AppLock admin password";
        }
        else
        {
            PasswordBox.Header = "Vault password (minimum 10 characters)";
        }
    }

    private bool ValidateStep2()
    {
        Step2ErrorText.Visibility = Visibility.Collapsed;

        var pw  = PasswordBox.Password;
        var pw2 = ConfirmPasswordBox.Password;

        if (UseAdminPasswordCheckBox.IsChecked == true)
        {
            if (!ServiceContainer.AuthService.VerifyPassword(pw))
            {
                Step2ErrorText.Text = "Password does not match your current AppLock admin password.";
                Step2ErrorText.Visibility = Visibility.Visible;
                return false;
            }
        }
        else
        {
            var strength = PasswordStrength.Evaluate(pw);
            if (!strength.MeetsMinimum)
            {
                Step2ErrorText.Text = pw.Length < PasswordStrength.MinLength
                    ? $"Password must be at least {PasswordStrength.MinLength} characters."
                    : strength.Description;
                Step2ErrorText.Visibility = Visibility.Visible;
                return false;
            }
        }

        if (pw != pw2)
        {
            Step2ErrorText.Text = "Passwords do not match.";
            Step2ErrorText.Visibility = Visibility.Visible;
            return false;
        }

        return true;
    }

    private bool ValidateStep3()
    {
        ProofErrorText.Visibility = Visibility.Collapsed;

        if (_recoveryKeyFormatted == null || _rawRecoveryKey == null)
        {
            ProofErrorText.Text = "Recovery key was not generated. Please wait.";
            ProofErrorText.Visibility = Visibility.Visible;
            return false;
        }

        var groups = _recoveryKeyFormatted.Split('-');
        if (groups.Length != 8)
        {
            ProofErrorText.Text = "Internal error: recovery key format is invalid.";
            ProofErrorText.Visibility = Visibility.Visible;
            return false;
        }

        var input1 = ProofBox1.Text.Trim().ToUpperInvariant().Replace(" ", "");
        var input2 = ProofBox2.Text.Trim().ToUpperInvariant().Replace(" ", "");

        string expected1 = groups[_proofGroupIndex1];
        string expected2 = groups[_proofGroupIndex2];

        bool ok1 = string.Equals(input1, expected1, StringComparison.OrdinalIgnoreCase);
        bool ok2 = string.Equals(input2, expected2, StringComparison.OrdinalIgnoreCase);

        if (!ok1 || !ok2)
        {
            ProofErrorText.Text = "One or both groups don't match the recovery key. Check your copy carefully.";
            ProofErrorText.Visibility = Visibility.Visible;
            return false;
        }

        return true;
    }

    // ---- Step 2: Password events ---------------------------------------

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        var pw = PasswordBox.Password;
        var strength = PasswordStrength.Evaluate(pw);

        StrengthBar.Value = strength.Score;
        StrengthText.Text = strength.Description;

        StrengthBar.Foreground = strength.Label switch
        {
            PasswordStrength.Level.VeryWeak   => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            PasswordStrength.Level.Weak       => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28)),
            PasswordStrength.Level.Fair       => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 200, 130, 0)),
            PasswordStrength.Level.Strong     => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16)),
            PasswordStrength.Level.VeryStrong => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16)),
            _                                 => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128))
        };

        WeakPasswordWarning.IsOpen = pw.Length >= 4 && strength.Label <= PasswordStrength.Level.Weak;
        Step2ErrorText.Visibility = Visibility.Collapsed;
    }

    // ---- Step 1: Path events ------------------------------------------

    private void VaultNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = VaultNameBox.Text.Trim();
        if (!string.IsNullOrEmpty(name) && string.IsNullOrEmpty(VaultPathBox.Text))
        {
            VaultPathBox.Text = VaultFactory.GetDefaultVaultPath(name);
            ValidatePathText(VaultPathBox.Text);
        }
        Step1ErrorText.Visibility = Visibility.Collapsed;
    }

    private async void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                VaultPathBox.Text = folder.Path;
                ValidatePathText(folder.Path);

                NetworkPathWarning.IsOpen =
                    folder.Path.StartsWith(@"\\") ||
                    IsRemovableDrive(folder.Path);
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"BrowseFolder failed: {ex.Message}");
        }
    }

    private void ValidatePathText(string path)
    {
        var err = VaultFactory.ValidatePath(path);
        PathErrorText.Text = err ?? "";
        PathErrorText.Visibility = err != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsRemovableDrive(string path)
    {
        try
        {
            var root = SysPath.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            var driveInfo = new DriveInfo(root);
            return driveInfo.DriveType == DriveType.Removable || driveInfo.DriveType == DriveType.Network;
        }
        catch { return false; }
    }

    // ---- Step 3: Recovery Key -----------------------------------------

    private void GenerateRecoveryKey()
    {
        var (rawKey, formatted) = RecoveryKey.Generate();
        _rawRecoveryKey = rawKey;
        _recoveryKeyFormatted = formatted;
        RecoveryKeyText.Text = formatted;

        // Pick two random groups to ask for verification
        var seed = BitConverter.ToInt32(rawKey, 0);
        var rng  = new Random(seed);
        var idx  = Enumerable.Range(0, 8).OrderBy(_ => rng.Next()).Take(2).OrderBy(x => x).ToArray();
        _proofGroupIndex1 = idx[0];
        _proofGroupIndex2 = idx[1];

        ProofLabel1.Text = $"Group {_proofGroupIndex1 + 1}:";
        ProofLabel2.Text = $"Group {_proofGroupIndex2 + 1}:";
        ProofBox1.Text   = "";
        ProofBox2.Text   = "";
        ProofErrorText.Visibility  = Visibility.Collapsed;
        CopyFeedbackText.Visibility = Visibility.Collapsed;
    }

    private void CopyKeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recoveryKeyFormatted == null) return;
        var dp = new DataPackage();
        dp.SetText(_recoveryKeyFormatted);
        Clipboard.SetContent(dp);
        CopyFeedbackText.Text = "Copied! The clipboard will clear after 60 seconds.";
        CopyFeedbackText.Visibility = Visibility.Visible;

        _ = Task.Delay(60000).ContinueWith(_ =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                try { Clipboard.ClearHistory(); } catch { }
                CopyFeedbackText.Text = "Clipboard cleared.";
            });
        });
    }

    private async void SaveKeyToFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recoveryKeyFormatted == null) return;
        try
        {
            var picker = new FileSavePicker();
            picker.SuggestedFileName = "AppLock-Vault-Recovery-Key";
            picker.FileTypeChoices.Add("Text file", [".txt"]);
            picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

            var hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                var vaultName = VaultNameBox.Text.Trim();
                var content =
                    $"AppLock Vault Recovery Key\r\n" +
                    $"Vault: {vaultName}\r\n" +
                    $"Created: {DateTime.Now:yyyy-MM-dd HH:mm}\r\n\r\n" +
                    $"{_recoveryKeyFormatted}\r\n\r\n" +
                    $"IMPORTANT: Store this in a safe place separate from your vault.\r\n" +
                    $"Without your password OR this key, your files are permanently unreadable.\r\n";
                await FileIO.WriteTextAsync(file, content);
                CopyFeedbackText.Text = $"Saved to: {file.Path}";
                CopyFeedbackText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"SaveKey failed: {ex.Message}");
        }
    }

    private void ProofBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ProofErrorText.Visibility = Visibility.Collapsed;
    }

    // ---- Step 5: Creation ---------------------------------------------

    private async Task StartCreationAsync()
    {
        UpdateStep();
        CreatingRing.IsActive = true;
        CreatingStatusText.Text = "Creating your vault…";
        CreatingErrorText.Visibility = Visibility.Collapsed;
        IsPrimaryButtonEnabled   = false;
        IsSecondaryButtonEnabled = false;
        CloseButtonText = "";

        _createCts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<(int Percent, string Status)>(p =>
            {
                DispatcherQueue.TryEnqueue(() =>
                    CreatingStatusText.Text = $"({p.Percent}%) {p.Status}");
            });

            int? autoLock = double.IsNaN(AutoLockBox.Value) || AutoLockBox.Value <= 0
                ? null
                : (int?)AutoLockBox.Value;

            var reg = await VaultFactory.CreateVaultAsync(
                name:            VaultNameBox.Text.Trim(),
                vaultPath:       VaultPathBox.Text.Trim(),
                password:        PasswordBox.Password,
                rawRecoveryKey:  _rawRecoveryKey!,
                autoLockMinutes: autoLock,
                progress:        progress,
                ct:              _createCts.Token);

            CreatedRegistration = reg;
            reg.UsesAdminPassword = UseAdminPasswordCheckBox.IsChecked == true;
            ServiceContainer.SettingsStore.Save(ServiceContainer.Settings);

            // Clear the password from memory ASAP
            PasswordBox.Password = "";
            ConfirmPasswordBox.Password = "";
            if (_rawRecoveryKey != null)
                CryptographicOperations.ZeroMemory(_rawRecoveryKey);

            CreatingStatusText.Text = "Vault created successfully!";
            CreatingRing.IsActive = false;
            await Task.Delay(500, _createCts.Token);
            Hide();
        }
        catch (OperationCanceledException)
        {
            CreatingStatusText.Text = "Cancelled.";
            CreatingRing.IsActive = false;
        }
        catch (Exception ex)
        {
            LogService.LogAction("Vault", $"CreateVault failed: {ex.GetType().Name}: {ex.Message}");
            CreatingRing.IsActive = false;
            CreatingStatusText.Text = "Failed to create vault.";
            CreatingErrorText.Text = ex.Message;
            CreatingErrorText.Visibility = Visibility.Visible;
            CloseButtonText = "Close";
            IsSecondaryButtonEnabled = true;
        }
    }
}
