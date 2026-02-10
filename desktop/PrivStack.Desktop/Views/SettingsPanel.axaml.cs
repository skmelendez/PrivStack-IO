using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform.Storage;
using PrivStack.Desktop.ViewModels;
using System.Linq;

namespace PrivStack.Desktop.Views;

public partial class SettingsPanel : UserControl
{
    private Expander[]? _sectionExpanders;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _sectionExpanders = this.GetLogicalDescendants()
            .OfType<Expander>()
            .Where(exp => exp.Classes.Contains("settings-section"))
            .ToArray();

        foreach (var expander in _sectionExpanders)
            expander.PropertyChanged += OnExpanderPropertyChanged;
    }

    private void OnExpanderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Expander.IsExpandedProperty || e.NewValue is not true || _sectionExpanders is null)
            return;

        foreach (var other in _sectionExpanders)
        {
            if (other != sender)
                other.IsExpanded = false;
        }
    }

    private async void OnChooseProfileImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose Profile Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"] }
            ]
        });

        if (files.Count > 0 && DataContext is SettingsViewModel vm)
        {
            var path = files[0].Path.LocalPath;
            vm.SetProfileImage(path);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_sectionExpanders is not null)
        {
            foreach (var expander in _sectionExpanders)
                expander.PropertyChanged -= OnExpanderPropertyChanged;
            _sectionExpanders = null;
        }
        base.OnUnloaded(e);
    }
}
