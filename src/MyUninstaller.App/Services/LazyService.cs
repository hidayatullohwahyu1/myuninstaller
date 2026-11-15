using Microsoft.Extensions.DependencyInjection;

namespace MyUninstaller.App.Services;

/// <summary>Lazy&lt;T&gt; untuk DI: layanan singleton baru dibuat saat <see cref="Lazy{T}.Value"/> pertama kali dibaca.</summary>
public sealed class LazyService<T>(IServiceProvider services) : Lazy<T>(services.GetRequiredService<T>)
    where T : class;
