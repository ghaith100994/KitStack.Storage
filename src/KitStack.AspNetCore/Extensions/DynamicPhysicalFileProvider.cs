using System;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using System.IO;

namespace KitStack.AspNetCore.Extensions
{
    /// <summary>
    /// Lightweight wrapper around PhysicalFileProvider that allows swapping the root path at runtime.
    /// This keeps the same IFileProvider instance wired into the middleware but updates the underlying provider.
    /// </summary>
    internal class DynamicPhysicalFileProvider : IFileProvider, IDisposable
    {
        private PhysicalFileProvider _inner;
        private readonly object _lock = new object();

        public DynamicPhysicalFileProvider(string root)
        {
            _inner = new PhysicalFileProvider(root);
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            lock (_lock) return _inner.GetDirectoryContents(subpath);
        }

        public IFileInfo GetFileInfo(string subpath)
        {
            lock (_lock) return _inner.GetFileInfo(subpath);
        }

        public IChangeToken Watch(string filter)
        {
            lock (_lock) return _inner.Watch(filter);
        }

        public void UpdateRoot(string root)
        {
            lock (_lock)
            {
                var old = _inner;
                try
                {
                    _inner = new PhysicalFileProvider(root);
                }
                finally
                {
                    try { old.Dispose(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _inner.Dispose(); } catch { }
            }
        }
    }
}
