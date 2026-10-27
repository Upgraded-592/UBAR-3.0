using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Ubar3;

/// <summary>
/// A single unified launcher module showing UBAR branding, divider,
/// and configured quick-launch buttons.
/// </summary>
public sealed class LauncherModule : IUbarModule
{
    private StackPanel? _launchersPanel;

    public string Id => "launcher";
    public string DisplayName => "App Launcher";
    public string Glyph => "\uE8B7";
    public ModuleAlignment DefaultAlignment => ModuleAlignment.Left;
    public int DefaultOrder => 0;

    public FrameworkElement Initialize(ModuleContext ctx)
    {
        var host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, VerticalAlignment = VerticalAlignment.Center };

        // 1. Branding
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        Brush accentBrush;
        try { accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]; }
        catch { accentBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)); }

        brand.Children.Add(new FontIcon
        {
            Glyph = "\uE704",
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            Foreground = accentBrush,
            FontSize = 16,
        });
        brand.Children.Add(new TextBlock
        {
            Text = "UBAR 3.0",
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            FontSize = 14,
            CharacterSpacing = 60,
            VerticalAlignment = VerticalAlignment.Center,
        });
        host.Children.Add(brand);

        // 2. Divider
        host.Children.Add(new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"],
            BorderThickness = new Thickness(1, 0, 0, 0),
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center,
        });

        // 3. Launchers Panel
        _launchersPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(_launchersPanel);

        RefreshButtons();

        return host;
    }

    public void RefreshButtons()
    {
        if (_launchersPanel == null) return;
        _launchersPanel.Children.Clear();

        var apps = LauncherAppService.LoadApps() is { Count: > 0 } saved
            ? LauncherAppService.MergeWithDefaults(saved)
            : LauncherAppDefaults.CreateDefaultApps().ToList();

        foreach (var app in apps.Where(a => a.IsVisible))
        {
            var btn = MakeLauncherButton(app, (s, e) => Launch(app));
            _launchersPanel.Children.Add(btn);
        }
    }

    private Button MakeLauncherButton(LauncherApp app, RoutedEventHandler click)
    {
        var btn = new Button
        {
            Background = null,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6),
        };

        if (!string.IsNullOrWhiteSpace(app.ImagePath))
        {
            var imagePath = ResolveImagePath(app.ImagePath);
            Logger.Log($"[LauncherModule] App={app.DisplayName}, ImagePath={app.ImagePath}, Resolved={imagePath}, Exists={File.Exists(imagePath)}");
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    var bitmap = new BitmapImage(new Uri(imagePath, UriKind.Absolute));
                    var image = new Image
                    {
                        Source = bitmap,
                        Width = 20,
                        Height = 20,
                        Stretch = Stretch.Uniform,
                    };
                    btn.Content = image;
                    Logger.Log($"[LauncherModule] Successfully loaded image for {app.DisplayName}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[LauncherModule] Failed to load image '{app.ImagePath}': {ex.Message}");
                    btn.Content = CreateFontIcon(app.Glyph);
                }
            }
            else
            {
                btn.Content = CreateFontIcon(app.Glyph);
            }
        }
        else
        {
            btn.Content = CreateFontIcon(app.Glyph);
        }

        ToolTipService.SetToolTip(btn, app.DisplayName);
        btn.Click += click;
        return btn;
    }

    private static FontIcon CreateFontIcon(string glyph)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 15,
        };
    }

    private static string ResolveImagePath(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return string.Empty;
        Logger.Log($"[ResolveImagePath] Input: '{imagePath}'");

        // If it's already an absolute path, return as-is
        if (Path.IsPathRooted(imagePath)) 
        {
            Logger.Log($"[ResolveImagePath] Absolute path, exists={File.Exists(imagePath)}");
            return imagePath;
        }

        // Try relative to the app's local state folder
        var localStatePath = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, imagePath);
        Logger.Log($"[ResolveImagePath] Trying localState: '{localStatePath}', exists={File.Exists(localStatePath)}");
        if (File.Exists(localStatePath)) return localStatePath;

        // Try relative to the executable directory
        var exeDir = AppContext.BaseDirectory;
        var exeRelativePath = Path.Combine(exeDir, imagePath);
        Logger.Log($"[ResolveImagePath] Trying exeDir: '{exeRelativePath}', exists={File.Exists(exeRelativePath)}");
        if (File.Exists(exeRelativePath)) return exeRelativePath;

        // Try relative to the AppX package folder
        var appxPath = Path.Combine(exeDir, "AppX", imagePath);
        Logger.Log($"[ResolveImagePath] Trying AppX: '{appxPath}', exists={File.Exists(appxPath)}");
        if (File.Exists(appxPath)) return appxPath;

        Logger.Log($"[ResolveImagePath] No valid path found");
        return imagePath;
    }

    public void OnEnabled() { }
    public void OnDisabled() { }
    public void Dispose() { }

    private void Launch(LauncherApp app)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = app.Command,
                Arguments = app.Arguments ?? string.Empty,
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Logger.Log($"[LauncherModule] Failed to launch '{app.Command}': {ex.Message}");
        }
    }
}
