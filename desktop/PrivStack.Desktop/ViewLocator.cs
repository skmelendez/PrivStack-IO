using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PrivStack.Desktop.ViewModels;
using Serilog;

namespace PrivStack.Desktop;

/// <summary>
/// Given a view model, returns the corresponding view if possible.
/// Convention: "FooViewModel" → "FooView" (same namespace/assembly).
///
/// Views are cached per ViewModel type so that switching tabs reuses the
/// existing view instance instead of creating a new one every time.
/// This prevents memory from growing linearly with each tab switch.
/// </summary>
[RequiresUnreferencedCode(
    "Default implementation of ViewLocator involves reflection which may be trimmed away.",
    Url = "https://docs.avaloniaui.net/docs/concepts/view-locator")]
public class ViewLocator : IDataTemplate
{
    private static readonly ConcurrentDictionary<string, Type?> _typeCache = new();
    private readonly Dictionary<Type, Control> _viewCache = new();

    public Control? Build(object? param)
    {
        if (param is null)
            return null;

        var vmType = param.GetType();

        // Return cached view if we already built one for this VM type.
        if (_viewCache.TryGetValue(vmType, out var cached))
        {
            Log.Information("[ViewLocator] CACHE HIT for {VmType} — ManagedHeap={Heap}MB, WorkingSet={WS}MB",
                vmType.Name,
                GC.GetTotalMemory(false) / 1024 / 1024,
                System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024);
            return cached;
        }

        Log.Information("[ViewLocator] CACHE MISS — creating view for {VmType}", vmType.Name);

        // Special case: Wasm plugin proxy → generic renderer
        if (param is PrivStack.Desktop.Services.Plugin.WasmViewModelProxy)
        {
            var wasmView = new Views.WasmPluginView();
            _viewCache[vmType] = wasmView;
            return wasmView;
        }

        var viewName = vmType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);

        var viewType = _typeCache.GetOrAdd(viewName, name =>
        {
            // 1. Try the ViewModel's own assembly first (handles external plugins)
            var type = vmType.Assembly.GetType(name);
            if (type != null) return type;

            // 2. Try the Desktop (host) assembly
            type = typeof(ViewLocator).Assembly.GetType(name);
            if (type != null) return type;

            // 3. Fallback: search all loaded assemblies (slow, cached)
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(name);
                    if (type != null) return type;
                }
                catch
                {
                    // Skip assemblies that throw on GetType
                }
            }

            return null;
        });

        if (viewType != null)
        {
            try
            {
                Log.Information("[ViewLocator] Instantiating view {ViewType} from assembly {Assembly}",
                    viewType.FullName, viewType.Assembly.GetName().Name);
                var view = (Control)Activator.CreateInstance(viewType)!;
                _viewCache[vmType] = view;
                Log.Information("[ViewLocator] Successfully created view {ViewType}", viewType.FullName);
                return view;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ViewLocator] FAILED to create view {ViewType} for VM {VmType}",
                    viewType.FullName, vmType.FullName);
                return new TextBlock { Text = $"View Error: {viewType.Name}\n{ex.InnerException?.Message ?? ex.Message}" };
            }
        }

        Log.Warning("[ViewLocator] View type NOT FOUND for {ViewName} (VM: {VmType}, Assembly: {Assembly})",
            viewName, vmType.FullName, vmType.Assembly.GetName().Name);
        return new TextBlock { Text = "Not Found: " + viewName };
    }

    public bool Match(object? data)
    {
        return data is ViewModelBase || data is PrivStack.Sdk.ViewModelBase;
    }
}
