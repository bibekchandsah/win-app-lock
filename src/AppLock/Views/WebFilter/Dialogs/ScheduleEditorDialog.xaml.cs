using System;
using System.Collections.Generic;
using System.Linq;
using AppLock.Models;
using AppLock.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppLock.Views.WebFilter.Dialogs;

public sealed partial class ScheduleEditorDialog : ContentDialog
{
    private const int MaxBlocks = 10;
    private readonly List<TimeBlockCard> _cards = new();

    public List<TimeBlock> ResultBlocks { get; private set; } = new();

    public ScheduleEditorDialog(string targetName, IEnumerable<TimeBlock>? existingBlocks)
    {
        InitializeComponent();
        Title = $"Schedule — {targetName}";
        TargetSubheader.Text = $"Active periods for {targetName}";

        ApplyTheme();
        Loaded += (s, e) => ApplyTheme();

        PrimaryButtonClick += (s, e) =>
        {
            ResultBlocks = _cards.Select(c => c.Block.Clone()).ToList();
        };

        if (existingBlocks != null)
        {
            foreach (var b in existingBlocks)
            {
                AddCard(b.Clone());
            }
        }

        UpdateUiState();
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

    private void AddCard(TimeBlock block)
    {
        if (_cards.Count >= MaxBlocks) return;

        var card = new TimeBlockCard(block);
        card.DeleteRequested += (s, e) =>
        {
            if (s is TimeBlockCard c)
            {
                _cards.Remove(c);
                BlocksContainer.Children.Remove(c);
                UpdateUiState();
            }
        };

        _cards.Add(card);
        BlocksContainer.Children.Add(card);
        UpdateUiState();
    }

    private void AddBlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cards.Count >= MaxBlocks) return;

        var block = new TimeBlock
        {
            Start = new TimeOnly(9, 0),
            End = new TimeOnly(17, 0),
            Days = new[] { false, true, true, true, true, true, false } // Mon-Fri default
        };

        AddCard(block);
    }

    private void UpdateUiState()
    {
        AddBlockButton.IsEnabled = _cards.Count < MaxBlocks;
        EmptyStateBorder.Visibility = _cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
