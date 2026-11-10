#nullable enable
// Shared source: compiled into each client plugin via
//   <Compile Include="..\shared\OverlayClient.cs" Link="OverlayClient.cs" />
// Binds by reflection to the JellyTvOverlay host plugin (each plugin has its own AssemblyLoadContext),
// using only BCL delegate types. Never reference the host assembly from client code.
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Xjellys.Overlay
{
    internal sealed class OverlayClient
    {
        private const string HostTypeName = "Jellyfin.Plugin.JellyTvOverlay.OverlayHost";
        private const int SupportedProtocol = 1;
        private const long RetryTicks = 5 * TimeSpan.TicksPerSecond;

        private static readonly object Gate = new object();
        private static OverlayClient? _current;
        private static long _lastScan = -1;

        private readonly string _clientId;
        private readonly Func<string, string, string, bool> _show;
        private readonly Action<string, string, string> _close;
        private readonly Func<string, bool> _hasApp;

        private OverlayClient(
            string clientId,
            Func<string, string, string, bool> show,
            Action<string, string, string> close,
            Func<string, bool> hasApp)
        {
            _clientId = clientId;
            _show = show;
            _close = close;
            _hasApp = hasApp;
        }

        /// <summary>The bound client, or null until the host is found.</summary>
        public static OverlayClient? Current => Volatile.Read(ref _current);

        /// <summary>
        /// Binds (once) and registers. Call at startup and before each use. While the host is absent it rescans at most
        /// once per ~5 s. Returns the client or null.
        /// </summary>
        public static OverlayClient? EnsureBound(
            string clientId,
            string displayName,
            Func<string, Task<string?>> onEvent,
            Func<Type?>? typeLocator = null)
        {
            var cur = Volatile.Read(ref _current);
            if (cur != null)
            {
                return cur;
            }

            lock (Gate)
            {
                if (_current != null)
                {
                    return _current;
                }

                var now = Stopwatch.GetTimestamp();
                var elapsedTicks = (now - _lastScan) * TimeSpan.TicksPerSecond / Stopwatch.Frequency;
                if (_lastScan >= 0 && elapsedTicks < RetryTicks)
                {
                    return null;
                }

                _lastScan = now;
                _current = TryBind(clientId, displayName, onEvent, typeLocator);
                return _current;
            }
        }

        public static OverlayClient? TryBind(
            string clientId,
            string displayName,
            Func<string, Task<string?>> onEvent,
            Func<Type?>? typeLocator = null)
        {
            try
            {
                var host = (typeLocator ?? FindHostType)();
                if (host == null)
                {
                    return null;
                }

                var field = host.GetField("ProtocolVersion", BindingFlags.Public | BindingFlags.Static);
                if (field == null || !(field.GetRawConstantValue() is int v) || v != SupportedProtocol)
                {
                    return null;
                }

                var register = Bind<Func<string, string, Func<string, Task<string?>>, bool>>(host, "Register");
                var show = Bind<Func<string, string, string, bool>>(host, "Show");
                var close = Bind<Action<string, string, string>>(host, "Close");
                var hasApp = Bind<Func<string, bool>>(host, "HasApp");
                if (register == null || show == null || close == null || hasApp == null)
                {
                    return null;
                }

                if (!register(clientId, displayName, onEvent))
                {
                    return null;
                }

                return new OverlayClient(clientId, show, close, hasApp);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public bool Show(string deviceId, string layoutJson)
        {
            try
            {
                return _show(_clientId, deviceId, layoutJson);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Close(string deviceId, string layoutId)
        {
            try
            {
                _close(_clientId, deviceId, layoutId);
            }
            catch (Exception)
            {
            }
        }

        public bool HasApp(string deviceId)
        {
            try
            {
                return _hasApp(deviceId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _current = null;
                _lastScan = -1;
            }
        }

        internal static void ResetThrottleForTests()
        {
            lock (Gate)
            {
                _lastScan = -1;
            }
        }

        private static T? Bind<T>(Type host, string name)
            where T : Delegate
        {
            var m = host.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Static,
                null,
                typeof(T).GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray(),
                null);
            return m == null ? null : (T)Delegate.CreateDelegate(typeof(T), m);
        }

        private static Type? FindHostType()
        {
            foreach (var alc in AssemblyLoadContext.All)
            {
                foreach (var asm in alc.Assemblies)
                {
                    try
                    {
                        var t = asm.GetType(HostTypeName, false);
                        if (t != null)
                        {
                            return t;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            return null;
        }
    }
}
