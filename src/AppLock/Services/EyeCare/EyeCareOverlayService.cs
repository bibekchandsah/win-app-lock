using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using AppLock.Models;
using AppLock.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;

namespace AppLock.Services.EyeCare;

public sealed class EyeCareOverlayService
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Func<EyeCareSettings> _getSettings;
    private readonly List<EyeCareOverlayWindow> _activeWindows = new();
    
    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "EyeCare");
    
    private static readonly string TemplatesPath = Path.Combine(AppDataPath, "templates");
    private static readonly string RenderPath = Path.Combine(AppDataPath, ".render");
    private static readonly string BuiltInTemplatePath = 
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "EyeCare", "index.html");

    public EyeCareOverlayService(DispatcherQueue dispatcher, Func<EyeCareSettings> getSettings)
    {
        _dispatcher = dispatcher;
        _getSettings = getSettings;
        
        EnsureDirectoriesExist();
        CopyBuiltInTemplateIfNeeded();
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(TemplatesPath);
        Directory.CreateDirectory(RenderPath);
    }

    private void CopyBuiltInTemplateIfNeeded()
    {
        try
        {
            var defaultTemplatePath = Path.Combine(TemplatesPath, "default.html");
            if (!File.Exists(defaultTemplatePath) && File.Exists(BuiltInTemplatePath))
            {
                File.Copy(BuiltInTemplatePath, defaultTemplatePath, overwrite: false);
                System.Diagnostics.Debug.WriteLine("[EyeCare] Copied built-in template to templates folder");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Failed to copy built-in template: {ex.Message}");
        }
    }

    public async Task ShowAsync()
    {
        if (_activeWindows.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine("[EyeCare] Overlay already visible");
            return;
        }

        try
        {
            var settings = _getSettings();
            var renderedPath = await RenderTemplateAsync(settings);

            // Get all displays
            var displays = DisplayArea.FindAll();
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Creating overlay on {displays.Count} display(s)");

            foreach (var display in displays)
            {
                var window = new EyeCareOverlayWindow(renderedPath, settings.DurationSeconds, CloseAll);
                _activeWindows.Add(window);
                
                // Position and show window
                window.Activate();
                
                // Position on specific display (AppWindow is inherited from Window base class)
                if (window.AppWindow != null)
                {
                    window.AppWindow.MoveAndResize(display.OuterBounds);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Show overlay error: {ex.Message}");
            CloseAll();
        }
    }

    private void CloseAll()
    {
        System.Diagnostics.Debug.WriteLine($"[EyeCare] Closing {_activeWindows.Count} window(s)");
        
        foreach (var window in _activeWindows.ToList())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EyeCare] Error closing window: {ex.Message}");
            }
        }
        
        _activeWindows.Clear();
        CleanupRenderedFiles();
    }

    private async Task<string> RenderTemplateAsync(EyeCareSettings settings)
    {
        // Determine template source
        string templatePath;
        if (!string.IsNullOrEmpty(settings.TemplatePath) && File.Exists(settings.TemplatePath))
        {
            templatePath = settings.TemplatePath;
        }
        else if (File.Exists(BuiltInTemplatePath))
        {
            templatePath = BuiltInTemplatePath;
        }
        else
        {
            throw new FileNotFoundException("No valid template found");
        }

        // Read template
        var templateContent = await File.ReadAllTextAsync(templatePath);

        // Get message
        var message = string.IsNullOrWhiteSpace(settings.Message) 
            ? EyeCareSettings.DefaultMessage 
            : settings.Message;

        // Replace placeholders with HTML-encoded values
        var rendered = templateContent
            .Replace("{{REMINDER_MESSAGE}}", WebUtility.HtmlEncode(message))
            .Replace("{{DURATION}}", settings.DurationSeconds.ToString());

        // Write rendered file
        var renderedPath = Path.Combine(RenderPath, "rendered.html");
        await File.WriteAllTextAsync(renderedPath, rendered);

        System.Diagnostics.Debug.WriteLine($"[EyeCare] Rendered template: {templatePath}");
        return renderedPath;
    }

    private void CleanupRenderedFiles()
    {
        try
        {
            if (Directory.Exists(RenderPath))
            {
                foreach (var file in Directory.GetFiles(RenderPath))
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Cleanup error: {ex.Message}");
        }
    }

    public List<string> GetAvailableTemplates()
    {
        var templates = new List<string> { "Default (Built-in)" };

        try
        {
            if (Directory.Exists(TemplatesPath))
            {
                var files = Directory.GetFiles(TemplatesPath, "*.html");
                templates.AddRange(files.Select(Path.GetFileName).Where(f => f != null).Cast<string>());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Error listing templates: {ex.Message}");
        }

        return templates;
    }

    public string GetTemplatesFolder() => TemplatesPath;
}
