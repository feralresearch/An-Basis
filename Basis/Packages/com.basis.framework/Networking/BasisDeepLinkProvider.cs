using Basis.BasisUI;
using Basis.Network.Core;
using Basis.Scripts.Common;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Basis.Scripts.Networking
{
    /// <summary>
    /// Handles deep links on all platforms. The URL scheme is configurable via
    /// <see cref="BasisDeepLinkConfig"/> (place an instance in any Resources folder);
    /// it defaults to <c>basisdemo://</c>.
    ///
    /// Cold-start  — <see cref="TryConsumeStartupLink"/> is called from
    ///   <see cref="BasisConnectionService.TryGetBootstrapConnection"/> before the first
    ///   auto-connect attempt.  Reads <see cref="Application.absoluteURL"/> (mobile /
    ///   some desktop configs) and scans CLI args for a bare scheme argument.
    ///
    /// Warm-start — <see cref="Application.deepLinkActivated"/> fires while the app is
    ///   already running.  Shows a confirmation before connecting; routes to the
    ///   notification list in VR/DND mode rather than force-opening the menu.
    ///
    /// URL format:  <c>scheme://host[:port][?password=xxx]</c>
    ///   Password must be in the query string — URL fragments are stripped by some OSes.
    /// </summary>
    public static class BasisDeepLinkProvider
    {
        private static string _scheme = "basisdemo";
        public static string Scheme => _scheme + "://";
        private const string DeepLinkEntryId = "__deeplink__";

        // Covers the full flow: set when a deep link is accepted (before dialog shown or
        // queued), cleared when the user denies, username check fails, or connect finishes.
        private static bool _deepLinkActive;

        // Stored so the OnIstanceCreated subscription can be removed if the flow is cancelled.
        private static Action _pendingShow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Initialize()
        {
            var config = BasisClientConfiguration.Load(Application.streamingAssetsPath);
            if (!string.IsNullOrEmpty(config.DeepLinkScheme))
                _scheme = config.DeepLinkScheme;

            Application.deepLinkActivated += OnDeepLinkActivated;
            Application.quitting += () =>
            {
                if (_pendingShow != null)
                {
                    BasisNetworkManagement.OnIstanceCreated -= _pendingShow;
                    _pendingShow = null;
                }
            };
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            if (!InitializeSingleInstance()) return;
            RegisterPlatformUrlScheme();
#elif UNITY_STANDALONE_LINUX && !UNITY_EDITOR
            RegisterPlatformUrlScheme();
#endif
        }

        /// <summary>
        /// Called once at startup from <see cref="BasisConnectionService.TryGetBootstrapConnection"/>.
        /// </summary>
        public static bool TryConsumeStartupLink(out ServerDirectoryEntry entry)
        {
            if (!string.IsNullOrEmpty(Application.absoluteURL)
                && TryParseBasisUrl(Application.absoluteURL, out entry))
                return true;
            return TryGetCliDeepLink(out entry);
        }

        /// <summary>
        /// Formats a <c>basisvr://</c> invite link. Returns <see cref="string.Empty"/> if
        /// <paramref name="host"/> is null or empty.
        /// </summary>
        public static string FormatDeepLink(string host, ushort port, string password = null)
        {
            if (string.IsNullOrEmpty(host)) return string.Empty;
            string link = Scheme + host + ":" + port.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(password))
                link += "?password=" + Uri.EscapeDataString(password);
            return link;
        }

        /// <summary>
        /// Formats a <c>basisvr://</c> invite link from a <see cref="ServerDirectoryEntry"/>.
        /// Returns <see cref="string.Empty"/> if the entry has no address.
        /// </summary>
        public static string FormatDeepLink(ServerDirectoryEntry entry)
        {
            if (entry == null) return string.Empty;
            string addr = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            if (string.IsNullOrEmpty(addr)) return string.Empty;
            string portStr = entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;
            ushort port = ushort.TryParse(portStr, out ushort p) ? p : LNLConnectionTargetParser.DefaultPort;
            return FormatDeepLink(addr, port, !string.IsNullOrEmpty(entry.Password) ? entry.Password : null);
        }

        private static void OnDeepLinkActivated(string url)
        {
            if (_deepLinkActive) return;
            if (!TryParseBasisUrl(url, out ServerDirectoryEntry entry)) return;

            _deepLinkActive = true;

            void Show()
            {
                if (_pendingShow != null)
                {
                    BasisNetworkManagement.OnIstanceCreated -= _pendingShow;
                    _pendingShow = null;
                }
                ShowConfirmation(entry);
            }

            if (BasisNetworkManagement.IsInitialized)
            {
                Show();
            }
            else
            {
                _pendingShow = Show;
                BasisNetworkManagement.OnIstanceCreated += Show;
            }
        }

        private static void ShowConfirmation(ServerDirectoryEntry entry, bool forceShow = false)
        {
            string addr = entry.Target?.Get(ConnectionTarget.Keys.Address) ?? string.Empty;
            string portStr = entry.Target?.Get(ConnectionTarget.Keys.Port) ?? string.Empty;
            string serverLabel = string.IsNullOrEmpty(portStr) ? addr : $"{addr}:{portStr}";

            // In VR / DND mode, route to the notification list instead of opening the menu.
            if (!forceShow && BasisNotificationCenter.RouteToNotifications)
            {
                BasisNotificationCenter.AddPending(
                    "Join Server?",
                    serverLabel,
                    AddressableAssets.Sprites.Network,
                    reopen: () => ShowConfirmation(entry, forceShow: true),
                    onDismiss: () => { _deepLinkActive = false; });
                return;
            }

            BasisMainMenu.Open();
            if (BasisMainMenu.Instance == null) { _deepLinkActive = false; BasisDebug.LogWarning("[BasisDeepLink] Main menu instance unavailable — deep link cancelled."); return; }
            if (BasisMainMenu.Instance.Dialogue != null)
                BasisMainMenu.Instance.Dialogue.ReleaseInstance();

            BasisMainMenu.Instance.OpenDialogue(
                "Join Server?",
                serverLabel,
                BasisLocalization.Get("ui.yes"),
                BasisLocalization.Get("ui.no"),
                accepted =>
                {
                    if (!accepted) { _deepLinkActive = false; return; }

                    string userName = BasisDataStore.LoadString(
                        BasisConnectionService.UsernameFileName, string.Empty);

                    if (string.IsNullOrEmpty(userName))
                    {
                        _deepLinkActive = false;
                        if (BasisMainMenu.Instance?.Dialogue != null)
                            BasisMainMenu.Instance.Dialogue.ReleaseInstance();
                        BasisMainMenu.Instance?.OpenDialogue(
                            "Username Required",
                            "Set a username before joining a server.",
                            BasisLocalization.Get("ui.ok"),
                            _ => { });
                        return;
                    }

                    _ = ConnectAndUnlock(entry, userName);
                });
        }

        private static async Task ConnectAndUnlock(ServerDirectoryEntry entry, string userName)
        {
            try { await BasisConnectionService.ConnectAsync(entry, userName); }
            finally { _deepLinkActive = false; }
        }

        private static bool TryGetCliDeepLink(out ServerDirectoryEntry entry)
        {
            entry = null;
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return false; }
            if (args == null) return false;

            foreach (string arg in args)
            {
                if (!string.IsNullOrEmpty(arg) && arg.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
                    return TryParseBasisUrl(arg, out entry);
            }
            return false;
        }

        public static bool TryParseBasisUrl(string url, out ServerDirectoryEntry entry)
        {
            entry = null;
            if (string.IsNullOrEmpty(url)) return false;
            if (!url.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) return false;

            string rest = url.Substring(Scheme.Length).TrimEnd('/');

            // Strip fragment — unreliable across platforms (iOS strips, Android preserves).
            int fragmentIdx = rest.IndexOf('#');
            if (fragmentIdx >= 0) rest = rest.Substring(0, fragmentIdx);

            string connectionPart = rest;
            string password = string.Empty;

            int queryIdx = rest.IndexOf('?');
            if (queryIdx >= 0)
            {
                connectionPart = rest.Substring(0, queryIdx).TrimEnd('/');
                password = ParsePasswordFromQuery(rest.Substring(queryIdx + 1));
            }

            if (!LNLConnectionTargetParser.TryParseConnectionString(
                    connectionPart, out string addr, out ushort port, out _, out _))
                return false;

            ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.DefaultId, $"{addr}:{port}");
            target.Set(ConnectionTarget.Keys.Address, addr);
            target.Set(ConnectionTarget.Keys.Port, port.ToString(CultureInfo.InvariantCulture));

            entry = new ServerDirectoryEntry
            {
                Id = DeepLinkEntryId,
                SourceId = SavedServersDirectorySource.Id,
                DisplayName = string.Empty,
                Target = target,
                HasPassword = !string.IsNullOrEmpty(password),
                Password = password,
                CanEdit = false,
                CanRemove = false,
            };
            return true;
        }

        private static string ParsePasswordFromQuery(string query)
        {
            foreach (string param in query.Split('&'))
            {
                int eqIdx = param.IndexOf('=');
                if (eqIdx < 0) continue;
                if (string.Equals(param.Substring(0, eqIdx).Trim(), "password", StringComparison.OrdinalIgnoreCase))
                {
                    string val = param.Substring(eqIdx + 1);
                    try { return Uri.UnescapeDataString(val); }
                    catch { return val; }
                }
            }
            return string.Empty;
        }

#if (UNITY_STANDALONE_WIN || UNITY_STANDALONE_LINUX) && !UNITY_EDITOR
        private static void RegisterPlatformUrlScheme()
        {
            try
            {
#if UNITY_STANDALONE_WIN
                // Application.dataPath = "C:\path\to\Basis Unity_Data" — strip suffix, add .exe
                string dataPath = Application.dataPath.Replace('/', '\\');
                if (!dataPath.EndsWith("_Data", StringComparison.OrdinalIgnoreCase)) return;
                string exePath = dataPath.Substring(0, dataPath.Length - 5) + ".exe";
                RegisterWindowsScheme(exePath);
#elif UNITY_STANDALONE_LINUX
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(exePath)) return;
                RegisterLinuxScheme(exePath);
#endif
            }
            catch (Exception ex)
            {
                BasisDebug.LogWarning($"[BasisDeepLink] URL scheme registration failed: {ex.Message}");
            }
        }
#endif

#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
        // ── Single-instance + warm-start via named mutex + named pipe ──────────
        // Application.deepLinkActivated never fires on Win32 standalone. When a
        // basisvr:// link is clicked while the app is already running, Windows
        // launches a second instance. That second instance detects the mutex,
        // pipes the URL to the primary instance, and quits immediately.

        private static string MutexName => _scheme.ToUpperInvariant() + "_DeepLink_Instance";
        private static string PipeName  => @"\\.\pipe\" + _scheme + "_deeplink";
        private const int    PipeBufSz  = 8192;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateMutexW(IntPtr attr, bool owner, string name);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateNamedPipeW(string name, uint openMode, uint pipeMode,
            uint maxInstances, uint outBuf, uint inBuf, uint timeout, IntPtr security);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ConnectNamedPipe(IntPtr pipe, IntPtr overlapped);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr file, byte[] buf, uint toRead, out uint read, IntPtr overlapped);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr tmpl);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr file, byte[] buf, uint toWrite, out uint written, IntPtr overlapped);

        // Returns true if this is the primary instance, false if secondary (caller should return immediately).
        private static bool InitializeSingleInstance()
        {
            const int ERROR_ALREADY_EXISTS = 183;
            CreateMutexW(IntPtr.Zero, true, MutexName);
            bool alreadyRunning = System.Runtime.InteropServices.Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS;

            if (alreadyRunning)
            {
                // Forward URL to primary instance, then quit this second instance.
                string url = GetCliDeepLinkUrl();
                if (!string.IsNullOrEmpty(url))
                {
                    byte[] data = System.Text.Encoding.UTF8.GetBytes(url);
                    IntPtr pipe = CreateFileW(PipeName, 0x40000000u /*GENERIC_WRITE*/, 0,
                        IntPtr.Zero, 3u /*OPEN_EXISTING*/, 0, IntPtr.Zero);
                    if (pipe != new IntPtr(-1))
                    {
                        WriteFile(pipe, data, (uint)data.Length, out _, IntPtr.Zero);
                        CloseHandle(pipe);
                    }
                }
                Application.Quit();
                return false;
            }

            // Primary instance — listen for URLs forwarded by future second instances.
            Thread t = new Thread(PipeServerLoop) { IsBackground = true, Name = "BasisVR_PipeServer" };
            t.Start();
            return true;
        }

        private static void PipeServerLoop()
        {
            const uint PIPE_ACCESS_INBOUND = 0x1;
            const uint PIPE_TYPE_BYTE      = 0x0;
            const uint PIPE_WAIT           = 0x0;
            IntPtr invalid = new IntPtr(-1);

            while (true)
            {
                IntPtr pipe = CreateNamedPipeW(PipeName, PIPE_ACCESS_INBOUND,
                    PIPE_TYPE_BYTE | PIPE_WAIT, 1, 0, PipeBufSz, 0, IntPtr.Zero);
                if (pipe == invalid) break;

                if (!ConnectNamedPipe(pipe, IntPtr.Zero)) { CloseHandle(pipe); continue; }

                byte[] buf = new byte[PipeBufSz];
                if (ReadFile(pipe, buf, (uint)buf.Length, out uint read, IntPtr.Zero) && read > 0)
                {
                    if (read == PipeBufSz)
                        BasisDebug.LogWarning("[BasisDeepLink] Pipe read hit buffer limit — URL may be truncated.");
                    string url = System.Text.Encoding.UTF8.GetString(buf, 0, (int)read);
                    Basis.Scripts.Device_Management.BasisDeviceManagement.mainThreadActions
                        .Enqueue(() => OnDeepLinkActivated(url));
                }

                CloseHandle(pipe);
            }
        }

        private static string GetCliDeepLinkUrl()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                if (args == null) return null;
                foreach (string arg in args)
                    if (!string.IsNullOrEmpty(arg) && arg.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
                        return arg;
            }
            catch { }
            return null;
        }

        // ── Registry URL scheme registration ──────────────────────────────────

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegDeleteTreeW(IntPtr hKey, string subKey);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegCreateKeyExW(IntPtr hKey, string subKey, int reserved, IntPtr lpClass,
            int options, int samDesired, IntPtr secAttr, out IntPtr result, out int disposition);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegSetValueExW(IntPtr hKey, string valueName, int reserved, int type,
            string data, int cbData);

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr hKey);

        private static void RegisterWindowsScheme(string exePath)
        {
            const string prefsKey = "__basis_dl_scheme__";
            IntPtr hkcu = new IntPtr(unchecked((int)0x80000001));

            string prevScheme = PlayerPrefs.GetString(prefsKey, string.Empty);
            if (!string.IsNullOrEmpty(prevScheme) && prevScheme != _scheme)
            {
                if (RegDeleteTreeW(hkcu, $@"Software\Classes\{prevScheme}") == 0)
                    BasisDebug.Log($"[BasisDeepLink] Removed stale URL scheme {prevScheme}://");
            }

            const int KEY_ALL_ACCESS = 0xF003F;
            const int REG_SZ = 1;

            int ret1 = RegCreateKeyExW(hkcu, $@"Software\Classes\{_scheme}", 0, IntPtr.Zero, 0, KEY_ALL_ACCESS, IntPtr.Zero, out IntPtr key1, out _);
            if (ret1 != 0) { BasisDebug.LogError($"[BasisDeepLink] RegCreateKeyExW({_scheme}) failed: {ret1}"); return; }
            string proto = $"URL:{_scheme} Protocol";
            RegSetValueExW(key1, "", 0, REG_SZ, proto, (proto.Length + 1) * 2);
            RegSetValueExW(key1, "URL Protocol", 0, REG_SZ, "", 2);
            RegCloseKey(key1);

            int ret2 = RegCreateKeyExW(hkcu, $@"Software\Classes\{_scheme}\shell\open\command", 0, IntPtr.Zero, 0, KEY_ALL_ACCESS, IntPtr.Zero, out IntPtr key2, out _);
            if (ret2 != 0) { BasisDebug.LogError($"[BasisDeepLink] RegCreateKeyExW(command) failed: {ret2}"); return; }
            string cmd = $"\"{exePath}\" \"%1\"";
            RegSetValueExW(key2, "", 0, REG_SZ, cmd, (cmd.Length + 1) * 2);
            RegCloseKey(key2);

            PlayerPrefs.SetString(prefsKey, _scheme);
            PlayerPrefs.Save();
            BasisDebug.Log($"[BasisDeepLink] Registered {_scheme}:// → {cmd}");
        }
#endif

#if UNITY_STANDALONE_LINUX && !UNITY_EDITOR
        private static void RegisterLinuxScheme(string exePath)
        {
            string desktopDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "applications");
            System.IO.Directory.CreateDirectory(desktopDir);

            string safeName = Application.productName.Replace("\n", "").Replace("\r", "");
            string safeExe = exePath.Replace("\n", "").Replace("\r", "");

            string desktopFile = System.IO.Path.Combine(desktopDir, $"{_scheme}-handler.desktop");
            System.IO.File.WriteAllText(desktopFile,
                "[Desktop Entry]\n" +
                $"Name={safeName}\n" +
                $"Exec=\"{safeExe}\" %u\n" +
                "Type=Application\n" +
                "NoDisplay=true\n" +
                $"MimeType=x-scheme-handler/{_scheme};\n");

            RunProcess("xdg-mime", $"default {_scheme}-handler.desktop x-scheme-handler/{_scheme}");
            RunProcess("update-desktop-database", desktopDir);
        }

        private static void RunProcess(string fileName, string args)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                })?.WaitForExit(3000);
            }
            catch { }
        }
#endif
    }
}
