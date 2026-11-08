using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
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
    private TaskCompletionSource? _overlayClosedTcs;

    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppLock", "EyeCare");

    private static readonly string TemplatesPath = Path.Combine(AppDataPath, "templates");
    private static readonly string RenderPath = Path.Combine(AppDataPath, ".render");

    public bool IsVisible => _activeWindows.Count > 0;

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

    public string GetBuiltInTemplatePath()
    {
        string[] candidates =
        [
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "EyeCare", "index.html"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "EyeCare", "index.html"),
            Path.Combine(Directory.GetCurrentDirectory(), "Assets", "EyeCare", "index.html"),
            Path.Combine(TemplatesPath, "default.html")
        ];

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return candidates[0];
    }

    private void CopyBuiltInTemplateIfNeeded()
    {
        try
        {
            var defaultTemplatePath = Path.Combine(TemplatesPath, "default.html");
            var builtInPath = GetBuiltInTemplatePath();

            if (File.Exists(builtInPath))
            {
                if (!File.Exists(defaultTemplatePath))
                {
                    File.Copy(builtInPath, defaultTemplatePath, overwrite: false);
                    System.Diagnostics.Debug.WriteLine("[EyeCare] Copied built-in template to templates folder");
                }
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

        _overlayClosedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var settings = _getSettings();
            var message = string.IsNullOrWhiteSpace(settings.Message)
                ? EyeCareSettings.DefaultMessage
                : settings.Message;

            var renderedPath = await RenderTemplateAsync(settings);
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Template rendered to: {renderedPath}");

            // Enumerate displays safely
            var displayList = new List<DisplayArea>();
            try
            {
                var displays = DisplayArea.FindAll();
                if (displays != null)
                {
                    for (int i = 0; i < displays.Count; i++)
                    {
                        try
                        {
                            displayList.Add(displays[i]);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EyeCare] DisplayArea.FindAll fallback: {ex.Message}");
            }

            if (displayList.Count == 0)
            {
                try
                {
                    var primary = DisplayArea.Primary;
                    if (primary != null)
                    {
                        displayList.Add(primary);
                    }
                }
                catch { }
            }

            if (displayList.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[EyeCare] No display found to show overlay");
                _overlayClosedTcs?.TrySetResult();
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[EyeCare] Creating overlay on {displayList.Count} display(s)");

            foreach (var display in displayList)
            {
                try
                {
                    var window = new EyeCareOverlayWindow(
                        renderedPath,
                        settings.DurationSeconds,
                        message,
                        CloseAll);

                    _activeWindows.Add(window);

                    // Show window
                    window.Activate();

                    // Position and enforce topmost on the display
                    window.MakeTopMostAndPosition(display.OuterBounds);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[EyeCare] Window creation failed: {ex.Message}");
                }
            }

            if (_activeWindows.Count == 0)
            {
                _overlayClosedTcs?.TrySetResult();
                return;
            }

            // Wait until closed
            await _overlayClosedTcs.Task;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] Show overlay error: {ex.Message}");
            CloseAll();
        }
    }

    private void CloseAll()
    {
        _dispatcher.TryEnqueue(() =>
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
            _overlayClosedTcs?.TrySetResult();
            CleanupRenderedFiles();
        });
    }

    private async Task<string> RenderTemplateAsync(EyeCareSettings settings)
    {
        // Determine template source
        string templatePath;
        if (!string.IsNullOrEmpty(settings.TemplatePath) && File.Exists(settings.TemplatePath))
        {
            templatePath = settings.TemplatePath;
        }
        else
        {
            templatePath = GetBuiltInTemplatePath();
            if (!File.Exists(templatePath))
            {
                templatePath = Path.Combine(TemplatesPath, "default.html");
            }
        }

        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException($"No valid template found at {templatePath}");
        }

        // Read template
        var templateContent = await File.ReadAllTextAsync(templatePath);

        // Get message
        var message = string.IsNullOrWhiteSpace(settings.Message)
            ? EyeCareSettings.DefaultMessage
            : settings.Message;

        // Replace placeholders case-insensitively with HTML-encoded values
        var rendered = Regex.Replace(
            templateContent,
            @"\{\{REMINDER_MESSAGE\}\}",
            WebUtility.HtmlEncode(message),
            RegexOptions.IgnoreCase);

        rendered = Regex.Replace(
            rendered,
            @"\{\{DURATION\}\}",
            settings.DurationSeconds.ToString(),
            RegexOptions.IgnoreCase);

        // Ensure clean render directory
        Directory.CreateDirectory(RenderPath);

        // Copy any sibling assets (images, css, js) from the template folder to RenderPath
        var sourceDir = Path.GetDirectoryName(templatePath);
        if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
        {
            CopySiblingAssets(sourceDir, RenderPath, Path.GetFileName(templatePath));
        }

        // Write rendered file
        var renderedPath = Path.Combine(RenderPath, "rendered.html");
        await File.WriteAllTextAsync(renderedPath, rendered);

        System.Diagnostics.Debug.WriteLine($"[EyeCare] Rendered template '{templatePath}' to '{renderedPath}'");
        return renderedPath;
    }

    private void CopySiblingAssets(string sourceDir, string targetDir, string mainHtmlFileName)
    {
        try
        {
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var fileName = Path.GetFileName(file);
                // Do not overwrite rendered.html or copy unrelated html
                if (string.Equals(fileName, "rendered.html", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dest = Path.Combine(targetDir, fileName);
                try
                {
                    File.Copy(file, dest, overwrite: true);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EyeCare] CopySiblingAssets error: {ex.Message}");
        }
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
                var files = Directory.GetFiles(TemplatesPath, "*.html")
                    .Concat(Directory.GetFiles(TemplatesPath, "*.htm"));

                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (!string.IsNullOrEmpty(name) && !templates.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        templates.Add(name);
                    }
                }
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
