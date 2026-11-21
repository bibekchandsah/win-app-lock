using System;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AppLock.Views.WebFilter.Dialogs;

public sealed partial class SetupWizardDialog : ContentDialog
{
    private readonly WebFilterSettings _settings;
    private int _step = 1;

    public bool WasApplied { get; private set; }

    public SetupWizardDialog(WebFilterSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        InitializeComponent();
        UpdateStepUI();

        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();
    }

    private void PrimaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step < 3)
        {
            // Do not close the dialog yet
            args.Cancel = true;
            _step++;
            UpdateStepUI();
        }
        else
        {
            // Apply step
            ApplyChoices();
            WasApplied = true;
        }
    }

    private void SecondaryButton_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_step > 1)
        {
            args.Cancel = true;
            _step--;
            UpdateStepUI();
        }
    }

    private void UpdateStepUI()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        // Button texts
        PrimaryButtonText = _step == 3 ? "Apply" : "Next";
        SecondaryButtonText = _step > 1 ? "Back" : string.Empty;

        // Dot indicator
        var accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var strokeBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"];

        Dot1.Fill = _step >= 1 ? accentBrush : strokeBrush;
        Dot2.Fill = _step >= 2 ? accentBrush : strokeBrush;
        Dot3.Fill = _step >= 3 ? accentBrush : strokeBrush;

        if (_step == 3)
        {
            bool isLocal = LocalFilterRadio.IsChecked == true;
            ReviewDnsText.Text = isLocal
                ? "Windows DNS will be set to the AppLock filter on: Wi-Fi, Ethernet."
                : "Windows DNS will be set to the Cloudflare Family filter on: Wi-Fi, Ethernet.";

            ReviewFirewallRow.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
            ReviewBrowserRow.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyChoices()
    {
        _settings.Mode = LocalFilterRadio.IsChecked == true ? WebFilterMode.LocalFilter : WebFilterMode.Provider;
        _settings.DefaultsSeeded = true;

        void SetCategory(string id, bool enabled)
        {
            if (!_settings.Categories.TryGetValue(id, out var rule))
            {
                rule = new CategoryRule();
                _settings.Categories[id] = rule;
            }
            rule.Enabled = enabled;
        }

        SetCategory("adult", AdultCheck.IsChecked == true);
        SetCategory("gambling", GamblingCheck.IsChecked == true);
        SetCategory("social", SocialCheck.IsChecked == true);
        SetCategory("malware", MalwareCheck.IsChecked == true);
        SetCategory("bypass", BypassCheck.IsChecked == true);
    }

    private void ApplyTheme()
    {
        if (XamlRoot?.Content is FrameworkElement rootFe && rootFe.ActualTheme != ElementTheme.Default)
        {
            RequestedTheme = rootFe.ActualTheme;
        }
        else
        {
            RequestedTheme = ServiceContainer.CurrentTheme;
        }
    }
}
