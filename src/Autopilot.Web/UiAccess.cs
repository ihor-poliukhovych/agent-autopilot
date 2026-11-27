using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Autopilot.Core.Workspace;

namespace Autopilot.Web;

/// <summary>The port and the opt-in key of the UI, both kept in the workspace so they survive restarts.</summary>
public static class UiAccess
{
    public const int FirstPort = 5100;
    public const int LastPort = 5999;

    /// <summary>True when nothing listens on the port of the address.</summary>
    public static bool IsFree(IPAddress address, int port)
    {
        try
        {
            using var listener = new TcpListener(address, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// The port this machine uses for the UI. The first start picks a free one in 5100-5999 and saves it; later
    /// starts reuse it so the address and bookmarks stay the same. If the saved port is busy, the next free one is
    /// used for this run and the saved one is kept. An explicit port (also 0) is used as given and not saved.
    /// </summary>
    public static int ResolvePort(WorkspaceSettings settings, int? requested, IPAddress? address = null, Func<IPAddress, int, bool>? isFree = null, Random? random = null)
    {
        if (requested is { } given)
        {
            return given;
        }

        address ??= IPAddress.Loopback;
        isFree ??= IsFree;
        var span = LastPort - FirstPort + 1;
        if (settings.GetInt(WorkspaceSettings.UiPort) is { } saved && saved is >= FirstPort and <= LastPort)
        {
            return Scan(saved);
        }

        var port = Scan((random ?? Random.Shared).Next(FirstPort, LastPort + 1));
        settings.Set(WorkspaceSettings.UiPort, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return port;

        int Scan(int start)
        {
            for (var i = 0; i < span; i++)
            {
                var candidate = FirstPort + ((start - FirstPort + i) % span);
                if (isFree(address, candidate))
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"No free port between {FirstPort} and {LastPort}. Use --port to choose one.");
        }
    }

    /// <summary>The persistent key; created on first use.</summary>
    public static string GetOrCreateKey(WorkspaceSettings settings)
    {
        if (settings.Get(WorkspaceSettings.UiKey) is { Length: > 0 } existing)
        {
            return existing;
        }

        return Rotate(settings);
    }

    /// <summary>A new key. Every browser that signed in with the old one is signed out.</summary>
    public static string Rotate(WorkspaceSettings settings)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        settings.Set(WorkspaceSettings.UiKey, key);
        return key;
    }

    /// <summary>The address to open: http://localhost:port/, with the key when one is required.</summary>
    public static string Address(int port, string? key = null) =>
        key is null ? $"http://localhost:{port}/" : $"http://localhost:{port}/?key={key}";
}
