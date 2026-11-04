using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;

namespace AppLock.Views;

public sealed partial class OthersPage : Page
{
    public OthersPage()
    {
        this.InitializeComponent();
        LoadUtilityCards();
    }

    private void LoadUtilityCards()
    {
        var cards = new List<UtilityCard>
        {
            new UtilityCard
            {
                Id = "brightness",
                Icon = "\uE706", // Sun icon
                Title = "Brightness",
                Description = "Control screen brightness with shortcuts and a blur overlay",
                Status = "Ready to configure",
                StatusVisibility = Visibility.Collapsed
            }
        };

        UtilityCardsControl.ItemsSource = cards;
    }

    private void UtilityCard_Click(object sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var card = button?.DataContext as UtilityCard;

        if (card?.Id == "brightness")
        {
            // Navigate to BrightnessPage
            Frame.Navigate(typeof(BrightnessPage));
        }
    }
}

public class UtilityCard
{
    public string Id { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Visibility StatusVisibility { get; set; } = Visibility.Collapsed;
}
