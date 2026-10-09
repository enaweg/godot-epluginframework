#if TOOLS
using System;
using System.Collections.Generic;
using Godot;

namespace Enaweg.Plugin.Internal.Manager;

/// <summary>Owns managed signal callbacks, which must be disconnected before their C# targets are disposed.</summary>
internal sealed class EditorSignalConnections : IDisposable
{
    private readonly List<(GodotObject Source, StringName Signal, Callable Callback)> _connections = [];

    public void Connect(GodotObject source, StringName signal, Callable callback)
    {
        var error = source.Connect(signal, callback);
        if (error != Error.Ok) throw new InvalidOperationException($"Cannot connect {signal}: {error}");
        _connections.Add((source, signal, callback));
    }

    public void Dispose()
    {
        foreach (var (source, signal, callback) in _connections)
            if (GodotObject.IsInstanceValid(source) && source.IsConnected(signal, callback))
                source.Disconnect(signal, callback);
        _connections.Clear();
    }
}
#endif
