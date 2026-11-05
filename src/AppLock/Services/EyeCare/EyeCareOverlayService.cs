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
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Got settings");
            
            var renderedPath = await RenderTemplateAsync(settings);
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Template rendered to: {renderedPath}");

            // Get all displays
            System.Diagnostics.Debug.WriteLine($"[EyeCare] About to call DisplayArea.FindAll()...");
            
            IReadOnlyList<DisplayArea>? displays = null;
            try
            {
                displays = DisplayArea.FindAll();
                System.Diagnostics.Debug.WriteLine($"[EyeCare] DisplayArea.FindAll() returned successfully");
            }
            catch (InvalidCastException ice)
            {
                System.Diagnostics.Debug.WriteLine($"[EyeCare] InvalidCastException in DisplayArea.FindAll()!");
                System.Diagnostics.Debug.WriteLine($"[EyeCare] Message: {ice.Message}");
                System.Diagnostics.Debug.WriteLine($"[EyeCare] HResult: 0x{ice.HResult:X8}");
                throw;
            }
            
            if (displays == null || displays.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[EyeCare] No displays found!");
                return;
            }
            
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Creating overlay on {displays.Count} display(s)");

            // Convert to list to avoid WinRT interop issues with IReadOnlyList enumeration
            var displayList = new List<DisplayArea>();
            for (int i = 0; i < displays.Count; i++)
            {
                try
                {
                    var display = displays[i];
                    displayList.Add(display);
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Display {i}: {display.OuterBounds}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Failed to access display {i}: {ex.Message}");
                }
            }

            foreach (var display in displayList)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] About to create EyeCareOverlayWindow...");
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Rendered path: {renderedPath}");
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Duration: {settings.DurationSeconds}");
                    
                    EyeCareOverlayWindow? window = null;
                    
                    try
                    {
                        window = new EyeCareOverlayWindow(renderedPath, settings.DurationSeconds, CloseAll);
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Window object created");
                    }
                    catch (InvalidCastException ice)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] InvalidCastException during window creation!");
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Message: {ice.Message}");
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Source: {ice.Source}");
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] HResult: 0x{ice.HResult:X8}");
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Stack trace:\n{ice.StackTrace}");
                        if (ice.InnerException != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[EyeCare] Inner: {ice.InnerException.GetType().Name} - {ice.InnerException.Message}");
                            System.Diagnostics.Debug.WriteLine($"[EyeCare] Inner stack:\n{ice.InnerException.StackTrace}");
                        }
                        throw;
                    }
                    
                    if (window == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Window is null after creation!");
                        continue;
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Window created successfully, about to activate");
                    
                    // Activate first to ensure window handle is valid
                    window.Activate();
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Window activated");
                    
                    _activeWindows.Add(window);
                    
                    // Position on specific display after activation
                    if (window.AppWindow != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Moving window to display bounds: {display.OuterBounds}");
                        window.AppWindow.MoveAndResize(display.OuterBounds);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] WARNING: Cannot position window - AppWindow is null");
                    }
                }
                catch (Exception innerEx)
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Window creation failed: {innerEx.GetType().Name}");
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Message: {innerEx.Message}");
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Stack trace: {innerEx.StackTrace}");
                    if (innerEx.InnerException != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"[EyeCare] Inner exception: {innerEx.InnerException.GetType().Name} - {innerEx.InnerException.Message}");
                    }
                    throw;
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
