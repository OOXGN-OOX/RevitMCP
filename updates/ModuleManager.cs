using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using RevitMCP.Contracts;

namespace RevitMCP.Updates;

public sealed class ModuleManager(string applicationRoot, Version loaderVersion) : IDisposable
{
    readonly object sync = new();
    ModuleHandle? handle;
    InstalledVersion? current;
    public string Root { get; } = Path.GetFullPath(applicationRoot);
    public InstalledVersion? Current { get { lock (sync) return current; } }
    public string? ActiveDirectory { get { lock (sync) return current is null ? null : SafePath.Child(Root, current.RelativePath); } }
    public bool CanRollback => File.Exists(Path.Combine(Root, "previous.json"));
    public List<WeakReference> RetiredContexts { get; } = [];
    public string? CleanupWarning { get; private set; }

    public void LoadActive()
    {
        lock (sync)
        {
            try { Activate(ReadPointer("active.json"), savePrevious: false); }
            catch when (CanRollback) { Activate(ReadPointer("previous.json"), savePrevious: false); }
        }
    }

    public bool IsNewer(UpdateManifest next)
    {
        lock (sync)
        {
            if (current is null) return true;
            var order = Version.Parse(next.Version).CompareTo(Version.Parse(current.Manifest.Version));
            return order > 0 || (order == 0 && next.Sequence > current.Manifest.Sequence && next.BuildId != current.Manifest.BuildId);
        }
    }

    public void Activate(InstalledVersion next, bool savePrevious = true)
    {
        lock (sync)
        {
            ValidateLocation(next.RelativePath);
            var directory = SafePath.Child(Root, next.RelativePath);
            var manifest = UpdateManifest.Read(directory);
            if (manifest.BuildId != next.Manifest.BuildId || manifest.Version != next.Manifest.Version)
                throw new InvalidDataException("Установленная сборка не совпадает с выбранной.");
            if (Version.Parse(manifest.MinimumLoaderVersion) > loaderVersion)
                throw new InvalidOperationException("Для этой сборки нужна новая версия загрузчика. Текущая рабочая часть не изменена.");
            if (savePrevious && current is not null && !IsNewer(manifest))
                throw new InvalidOperationException("Эта сборка уже установлена или старше текущей.");
            using var loaded = ModuleHandle.Load(Path.Combine(directory, "core", "RevitMCP.Core.dll"), manifest);
            Directory.CreateDirectory(Root);
            var descriptor = new InstalledVersion(next.RelativePath, manifest);
            // Both writes must succeed before replacing the live module.
            if (savePrevious && current is not null) WritePointer("previous.json", current);
            WritePointer("active.json", descriptor);
            var previous = handle;
            handle = loaded.TakeOwnership();
            current = descriptor;
            if (previous is not null)
            {
                try { previous.Dispose(); CleanupWarning = null; }
                catch (Exception ex) { CleanupWarning = ex.Message; }
                finally { RetiredContexts.Add(previous.ContextReference); }
            }
        }
    }

    public void Rollback()
    {
        lock (sync)
        {
            var old = current;
            Activate(ReadPointer("previous.json"), savePrevious: false);
            if (old is not null) WritePointer("previous.json", old);
        }
    }

    public string Execute(object application, string request, Func<bool> cancelled)
    {
        lock (sync)
        {
            if (handle is null) throw new InvalidOperationException("Рабочая часть Revit MCP не загружена. Откройте «Обновление» для восстановления.");
            return handle.Module.Execute(application, request, cancelled);
        }
    }

    public void DocumentChanged(object? sender, object args)
    {
        lock (sync) handle?.Module.DocumentChanged(sender, args);
    }

    InstalledVersion ReadPointer(string file)
    {
        var descriptor = JsonSerializer.Deserialize<InstalledVersion>(File.ReadAllText(Path.Combine(Root, file)), UpdateManifest.Json)
            ?? throw new InvalidDataException("Не найдена установленная сборка.");
        ValidateLocation(descriptor.RelativePath);
        return descriptor;
    }

    static void ValidateLocation(string path)
    {
        if (!path.StartsWith("updates/", StringComparison.Ordinal) && !path.StartsWith("versions/", StringComparison.Ordinal))
            throw new InvalidDataException("Неизвестный каталог установленной сборки.");
    }

    void WritePointer(string name, InstalledVersion descriptor)
    {
        var target = Path.Combine(Root, name);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(descriptor, UpdateManifest.Json));
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        lock (sync) { handle?.Dispose(); handle = null; }
    }
}

sealed class ModuleHandle : IDisposable
{
    ICoreModule? module;
    CoreLoadContext? context;
    bool transferred;
    public ICoreModule Module => module ?? throw new ObjectDisposedException(nameof(ModuleHandle));
    public WeakReference ContextReference { get; }

    ModuleHandle(CoreLoadContext context, ICoreModule module)
    {
        this.context = context;
        this.module = module;
        ContextReference = new WeakReference(context);
    }

    public static ModuleHandle Load(string assemblyPath, UpdateManifest manifest)
    {
        var context = new CoreLoadContext();
        try
        {
            // Loading from bytes never locks an installed DLL or overwrites a loaded image.
            using var bytes = new MemoryStream(File.ReadAllBytes(assemblyPath));
            var assembly = context.LoadFromStream(bytes);
            var type = assembly.GetType("RevitMCP.RuntimeEntry", throwOnError: true)!;
            var instance = Activator.CreateInstance(type) as ICoreModule ?? throw new InvalidDataException("Рабочая часть не реализует контракт загрузчика.");
            if (instance.ProtocolVersion != manifest.ProtocolVersion || instance.Version != manifest.Version)
            {
                instance.Dispose();
                throw new InvalidDataException("Версия рабочей части не совпадает с манифестом.");
            }
            return new(context, instance);
        }
        catch { context.Unload(); throw; }
    }

    public ModuleHandle TakeOwnership()
    {
        transferred = true;
        return new ModuleHandle(context!, module!);
    }

    public void Dispose()
    {
        if (transferred) { module = null; context = null; return; }
        try { module?.Dispose(); }
        finally { module = null; context?.Unload(); context = null; }
    }
}

sealed class CoreLoadContext() : AssemblyLoadContext(isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name == typeof(ICoreModule).Assembly.GetName().Name) return typeof(ICoreModule).Assembly;
        return Default.Assemblies.FirstOrDefault(assembly => assembly.GetName().Name == assemblyName.Name);
    }
}
