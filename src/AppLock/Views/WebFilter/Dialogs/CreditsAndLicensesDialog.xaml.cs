using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using AppLock.Services.WebFilter;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.WebFilter.Dialogs;

public sealed partial class CreditsAndLicensesDialog : ContentDialog
{
    public sealed class ListSourceCreditItem
    {
        public string Name { get; init; } = string.Empty;
        public string Attribution { get; init; } = string.Empty;
        public string LicenseText { get; init; } = string.Empty;
        public Uri? Url { get; init; }
        public Visibility HasUrl => Url != null ? Visibility.Visible : Visibility.Collapsed;
    }

    public CreditsAndLicensesDialog(WebFilterCatalog catalog, XamlRoot? xamlRoot = null)
    {
        InitializeComponent();
        if (xamlRoot != null) XamlRoot = xamlRoot;

        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();

        // Build list of all distinct list sources
        var credits = new List<ListSourceCreditItem>();

        foreach (var cat in catalog.Categories)
        {
            foreach (var src in cat.Sources)
            {
                if (credits.Any(c => string.Equals(c.Name, src.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                Uri? parsedUri = null;
                if (!string.IsNullOrWhiteSpace(src.Url) && Uri.TryCreate(src.Url, UriKind.Absolute, out var uri))
                {
                    parsedUri = uri;
                }

                credits.Add(new ListSourceCreditItem
                {
                    Name = src.Name,
                    Attribution = string.IsNullOrWhiteSpace(src.Attribution) ? cat.Name : src.Attribution,
                    LicenseText = string.IsNullOrWhiteSpace(src.License) ? "License: Open source" : $"License: {src.License}",
                    Url = parsedUri
                });
            }
        }

        // Add additional standard contributors/components if not present
        if (!credits.Any(c => c.Name.Contains("StevenBlack", StringComparison.OrdinalIgnoreCase)))
        {
            credits.Add(new ListSourceCreditItem
            {
                Name = "StevenBlack Unified Hosts",
                Attribution = "StevenBlack's unified hosts with extensions (MIT)",
                LicenseText = "License: MIT",
                Url = new Uri("https://github.com/StevenBlack/hosts")
            });
        }

        if (!credits.Any(c => c.Name.Contains("Block List Project", StringComparison.OrdinalIgnoreCase)))
        {
            credits.Add(new ListSourceCreditItem
            {
                Name = "The Block List Project",
                Attribution = "Blocklist Project malware, phishing, and tracking lists",
                LicenseText = "License: MIT / Unlicense",
                Url = new Uri("https://github.com/blocklistproject/Lists")
            });
        }

        SourcesList.ItemsSource = credits;
        ProvidersList.ItemsSource = catalog.Providers;
    }

    private async void WebsiteLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is HyperlinkButton { Tag: Uri uri })
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        else if (sender is HyperlinkButton { Tag: string url } && Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            await Windows.System.Launcher.LaunchUriAsync(parsed);
        }
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
