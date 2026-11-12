using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace HumanTyper
{
    /// <summary>
    /// One-click install / uninstall of the Interception keyboard driver used by the driver-level
    /// input mode. Downloads the official release, checks its SHA-256, extracts only the installer
    /// and the library, then runs the installer elevated (one UAC prompt).
    /// </summary>
    internal static class DriverSetup
    {
        private const string ZipUrl = "https://github.com/oblitum/Interception/releases/download/v1.0.1/Interception.zip";
        private const string ZipSha256 = "ad038963d6413055765128b0b931f6e765147c9916dba79e65d872b261f9af10";

        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HumanTyper", "Interception");

        public static string DllPath => Path.Combine(Folder, "interception.dll");
        public static string InstallerPath => Path.Combine(Folder, "install-interception.exe");

        /// <summary>True when the driver files are present in Windows (it may still need a reboot).</summary>
        public static bool DriverInstalled =>
            File.Exists(Path.Combine(Environment.SystemDirectory, "drivers", "keyboard.sys"));

        /// <summary>Downloads and unpacks the installer and dll (skips work already done).</summary>
        public static void EnsureFiles(Action<string> progress, string folder = null)
        {
            folder = folder ?? Folder;
            string dll = Path.Combine(folder, "interception.dll");
            string installer = Path.Combine(folder, "install-interception.exe");
            if (File.Exists(dll) && File.Exists(installer)) return;

            Directory.CreateDirectory(folder);
            string zip = Path.Combine(folder, "Interception.zip");
            progress?.Invoke("正在从 Interception 官方 GitHub 下载驱动…");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.UserAgent] = "HumanTyper";
                wc.DownloadFile(ZipUrl, zip);
            }

            progress?.Invoke("正在校验文件…");
            string hash;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(zip))
                hash = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (hash != ZipSha256)
            {
                File.Delete(zip);
                throw new InvalidOperationException("下载的文件校验失败（可能被篡改或下载不完整），已取消安装。");
            }

            string arch = Environment.Is64BitProcess ? "x64" : "x86";
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    string name = entry.FullName.Replace('\\', '/');
                    if (name.EndsWith("command line installer/install-interception.exe", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(installer, true);
                    else if (name.EndsWith("library/" + arch + "/interception.dll", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(dll, true);
                    else if (name.EndsWith("non-commercial-usage/LGPL 3.0.txt", StringComparison.OrdinalIgnoreCase))
                        entry.ExtractToFile(Path.Combine(folder, "LICENSE-LGPL-3.0.txt"), true);
                }
            }
            File.Delete(zip);
            if (!File.Exists(dll) || !File.Exists(installer))
                throw new InvalidOperationException("驱动安装包内容不完整，已取消安装。");
        }

        /// <summary>
        /// Runs the installer elevated. Returns false if the user declined the UAC prompt.
        /// </summary>
        public static bool RunInstaller(bool install)
        {
            var psi = new ProcessStartInfo(InstallerPath, install ? "/install" : "/uninstall")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Folder,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p?.WaitForExit(60000);
                }
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)   // ERROR_CANCELLED: UAC declined
            {
                return false;
            }
        }

        public static void Reboot()
        {
            Process.Start(new ProcessStartInfo("shutdown", "/r /t 5 /c \"HumanTyper：重启以启用键盘驱动\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
    }
}
