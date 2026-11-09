/*
 * This file is part of Fujify.
 *
 * Copyright (C) 2024 Isidore Paulin contact@ipweb.dev
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fujify.Common
{
    public static class CrossPlatformLauncher
    {
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO lpExecInfo);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr GetProp(IntPtr hWnd, string lpString);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHELLEXECUTEINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpVerb;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpFile;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpParameters;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPTStr)]
            public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIcon;
            public IntPtr hProcess;
        }

        private const int SW_SHOW = 5;
        private const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;

        [DllImport("shell32.dll", EntryPoint = "SHOpenWithDialog", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHOpenWithDialog(IntPtr hWndParent, ref OPENASINFO oOAI);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENASINFO
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string cszFile;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? cszClass;

            public OPENASINFOFLAGS oaifFlags;
        }

        [Flags]
        private enum OPENASINFOFLAGS : uint
        {
            OAIF_ALLOW_REGISTRATION = 0x00000001,
            OAIF_REGISTER_EXT = 0x00000002,
            OAIF_EXEC = 0x00000004,
            OAIF_FORCE_REGISTRATION = 0x00000008,
            OAIF_HIDE_REGISTRATION = 0x00000020,
            OAIF_URLPROTOCOL = 0x00000040,
            OAIF_FILE_MOVED = 0x00000080,
            OAIF_IS_URI = 0x00000100
        }

        public static void OpenPath(string path)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                }
                else
                {
                    var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                    psi.ArgumentList.Add(path);
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error launching path {path}: {ex.Message}");
            }
        }

        public static bool OpenInCaptureOne(IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0) return false;

            var validFiles = filePaths.Where(File.Exists).ToList();
            if (validFiles.Count == 0) return false;

            // 1. Mobile platform callback (e.g. iOS)
            if (Fujify.ViewModels.MainViewModel.PlatformOpenInCaptureOneCallback != null)
            {
                Fujify.ViewModels.MainViewModel.PlatformOpenInCaptureOneCallback(validFiles);
                return true;
            }

            // 2. Windows implementation
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                string? exePath = FindCaptureOneWindows();
                string executable = (!string.IsNullOrEmpty(exePath) && File.Exists(exePath)) ? exePath : "CaptureOne.exe";

                // Check BEFORE launching whether Capture One is already running
                bool alreadyRunning = Process.GetProcessesByName("CaptureOne").Length > 0;

                if (!alreadyRunning && validFiles.Count > 1)
                {
                    // Cold start with multiple files:
                    // Launch Capture One with NO arguments so its main window and IPC receiver initialize cleanly
                    // without modal blocking, then queue all files once IPC is ready.
                    bool launched = LaunchCaptureOneAppWindows(executable);
                    if (!launched && executable != "CaptureOne.exe")
                    {
                        launched = LaunchCaptureOneAppWindows("CaptureOne.exe");
                        if (launched) executable = "CaptureOne.exe";
                    }

                    if (!launched)
                    {
                        // Fallback: default shell open
                        try
                        {
                            foreach (var f in validFiles) OpenPath(f);
                            return true;
                        }
                        catch { return false; }
                    }

                    string targetExe = executable;
                    _ = Task.Run(async () =>
                    {
                        if (!OperatingSystem.IsWindows()) return;
                        try
                        {
                            // Wait for Capture One to start and register its IPC receiver window property
                            int maxWaitMs = 25000;
                            int waitedMs = 0;

                            while (waitedMs < maxWaitMs)
                            {
                                await Task.Delay(300);
                                waitedMs += 300;

                                if (IsCaptureOneIpcReady())
                                {
                                    break;
                                }
                            }

                            // Deliver first file to trigger the Importer modal
                            var proc0 = LaunchCaptureOneProcessWindows(targetExe, validFiles[0]);
                            if (proc0 != null)
                            {
                                try { proc0.WaitForExit(3000); } catch { }
                            }

                            // Allow time for the Importer modal to initialize and display
                            await Task.Delay(1500);

                            // Deliver each remaining file to AddFileToImporterCollection
                            foreach (var f in validFiles.Skip(1))
                            {
                                var proc = LaunchCaptureOneProcessWindows(targetExe, f);
                                if (proc != null)
                                {
                                    try { proc.WaitForExit(3000); } catch { }
                                }
                                await Task.Delay(150);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[CrossPlatformLauncher] Error queuing Capture One files: {ex.Message}");
                        }
                    });

                    return true;
                }
                else
                {
                    // Capture One already running OR single file cold start:
                    // Launch first file directly into Capture One to trigger import modal preloading
                    bool success = LaunchCaptureOneFileWindows(executable, validFiles[0]);
                    if (!success && executable != "CaptureOne.exe")
                    {
                        success = LaunchCaptureOneFileWindows("CaptureOne.exe", validFiles[0]);
                        if (success) executable = "CaptureOne.exe";
                    }

                    if (!success)
                    {
                        try
                        {
                            foreach (var f in validFiles) OpenPath(f);
                            return true;
                        }
                        catch { return false; }
                    }

                    if (validFiles.Count > 1)
                    {
                        var remaining = validFiles.Skip(1).ToList();
                        string targetExe = executable;
                        _ = Task.Run(async () =>
                        {
                            if (!OperatingSystem.IsWindows()) return;
                            try
                            {
                                await Task.Delay(1500);
                                foreach (var f in remaining)
                                {
                                    var proc = LaunchCaptureOneProcessWindows(targetExe, f);
                                    if (proc != null)
                                    {
                                        try { proc.WaitForExit(3000); } catch { }
                                    }
                                    await Task.Delay(150);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[CrossPlatformLauncher] Error queuing additional Capture One files: {ex.Message}");
                            }
                        });
                    }

                    return true;
                }
            }
            // 3. macOS implementation
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                try
                {
                    var psi = new ProcessStartInfo("open")
                    {
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("-a");
                    psi.ArgumentList.Add("Capture One");
                    foreach (var f in validFiles)
                    {
                        psi.ArgumentList.Add(f);
                    }
                    var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                    if (proc != null && proc.ExitCode == 0)
                    {
                        return true;
                    }
                }
                catch { }

                // Fallback: generic open
                try
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    foreach (var f in validFiles)
                    {
                        psi.ArgumentList.Add(f);
                    }
                    Process.Start(psi);
                    return true;
                }
                catch { }
            }
            // 4. Linux implementation
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    foreach (var f in validFiles)
                    {
                        OpenPath(f);
                    }
                    return true;
                }
                catch { }
            }

            return false;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static Process? LaunchCaptureOneProcessWindows(string executable, string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            // 1. ShellExecute (matches Windows Explorer Open With behavior)
            try
            {
                var psi = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true,
                    Arguments = $"\"{filePath}\"",
                    WorkingDirectory = Path.GetDirectoryName(filePath) ?? string.Empty
                };
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CrossPlatformLauncher] ShellExecute failed for {filePath}: {ex.Message}");
            }

            // 2. Direct Process creation fallback
            try
            {
                var psi = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(filePath) ?? string.Empty
                };
                psi.ArgumentList.Add(filePath);
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CrossPlatformLauncher] CreateProcess fallback failed for {filePath}: {ex.Message}");
            }

            return null;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool LaunchCaptureOneFileWindows(string executable, string filePath)
        {
            return LaunchCaptureOneProcessWindows(executable, filePath) != null;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool LaunchCaptureOneAppWindows(string executable)
        {
            try
            {
                var psi = new ProcessStartInfo(executable)
                {
                    UseShellExecute = true
                };
                var proc = Process.Start(psi);
                return proc != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CrossPlatformLauncher] ShellExecute failed to launch {executable}: {ex.Message}");
            }

            try
            {
                var psi = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                return proc != null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CrossPlatformLauncher] CreateProcess fallback failed for {executable}: {ex.Message}");
            }

            return false;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static bool IsCaptureOneIpcReady()
        {
            bool ready = false;
            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    IntPtr prop = GetProp(hWnd, @"Global\PhaseOne.CaptureOne");
                    if (prop == hWnd)
                    {
                        ready = true;
                        return false; // Found, stop enumeration
                    }
                    return true; // Continue enumeration
                }, IntPtr.Zero);
            }
            catch
            {
                var procs = Process.GetProcessesByName("CaptureOne");
                if (procs.Length > 0)
                {
                    var c1 = procs[0];
                    c1.Refresh();
                    ready = c1.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(c1.MainWindowTitle);
                }
            }
            return ready;
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static string? FindCaptureOneWindows()
        {
            // A. Check Registry Applications Open Command (matches Explorer Open With)
            try
            {
                using var cmdKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"Applications\CaptureOne.exe\shell\open\command");
                if (cmdKey != null)
                {
                    string? cmd = cmdKey.GetValue(null)?.ToString();
                    if (!string.IsNullOrEmpty(cmd))
                    {
                        int firstQuote = cmd.IndexOf('"');
                        if (firstQuote >= 0)
                        {
                            int secondQuote = cmd.IndexOf('"', firstQuote + 1);
                            if (secondQuote > firstQuote)
                            {
                                string exe = cmd.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
                                if (File.Exists(exe)) return exe;
                            }
                        }
                        else
                        {
                            string exe = cmd.Split(' ')[0];
                            if (File.Exists(exe)) return exe;
                        }
                    }
                }
            }
            catch { }

            // B. Check Registry App Paths
            try
            {
                using var appKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\CaptureOne.exe")
                                ?? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\CaptureOne.exe");
                if (appKey != null)
                {
                    string? path = appKey.GetValue(null)?.ToString();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;
                }
            }
            catch { }

            // C. Check standard Program Files installation paths
            var programDirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };

            foreach (var baseDir in programDirs)
            {
                if (string.IsNullOrEmpty(baseDir) || !Directory.Exists(baseDir)) continue;

                var candidateFolders = new[]
                {
                    Path.Combine(baseDir, "Capture One"),
                    Path.Combine(baseDir, "Phase One")
                };

                foreach (var cFolder in candidateFolders)
                {
                    if (!Directory.Exists(cFolder)) continue;

                    string directExe = Path.Combine(cFolder, "Capture One", "CaptureOne.exe");
                    if (File.Exists(directExe)) return directExe;

                    string rootExe = Path.Combine(cFolder, "CaptureOne.exe");
                    if (File.Exists(rootExe)) return rootExe;

                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(cFolder))
                        {
                            string subExe = Path.Combine(subDir, "CaptureOne.exe");
                            if (File.Exists(subExe)) return subExe;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        public static void OpenWith(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!File.Exists(filePath)) return;
                    string fullPath = Path.GetFullPath(filePath);
                    OpenWithWindows(fullPath);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (!File.Exists(filePath)) return;
                    string escapedPath = filePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    RunOsaScript(
                        "tell application \"System Events\" to activate",
                        "try",
                        $"set targetFile to (POSIX file \"{escapedPath}\") as alias",
                        "set chosenApp to choose application with prompt \"Choose an application to open this file with:\"",
                        "tell application \"Finder\" to open targetFile using chosenApp",
                        "end try"
                    );
                }
                else
                {
                    var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                    psi.ArgumentList.Add(filePath);
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error launching Open With for {filePath}: {ex.Message}");
            }
        }

        [System.Runtime.Versioning.SupportedOSPlatform("windows")]
        private static void OpenWithWindows(string fullPath)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var oai = new OPENASINFO
                    {
                        cszFile = fullPath,
                        cszClass = null,
                        oaifFlags = OPENASINFOFLAGS.OAIF_EXEC | OPENASINFOFLAGS.OAIF_ALLOW_REGISTRATION
                    };

                    int hr = SHOpenWithDialog(IntPtr.Zero, ref oai);
                    if (hr < 0)
                    {
                        Debug.WriteLine($"SHOpenWithDialog failed with HRESULT 0x{hr:X8}, trying fallback");
                        TryRundll32Fallback(fullPath);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"SHOpenWithDialog error: {ex.Message}");
                    TryRundll32Fallback(fullPath);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
        }

        private static void TryRundll32Fallback(string fullPath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,OpenAs_RunDLL \"{fullPath}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Fallback OpenAs_RunDLL failed: {ex.Message}");
            }
        }

        public static void ShowFileProperties(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var info = new SHELLEXECUTEINFO();
                    info.cbSize = Marshal.SizeOf(info);
                    info.lpVerb = "properties";
                    info.lpFile = filePath;
                    info.nShow = SW_SHOW;
                    info.fMask = SEE_MASK_INVOKEIDLIST;
                    ShellExecuteEx(ref info);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    if (!File.Exists(filePath) && !Directory.Exists(filePath)) return;
                    string escapedPath = filePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    RunOsaScript(
                        "tell application \"Finder\"",
                        "activate",
                        "try",
                        $"open information window of (POSIX file \"{escapedPath}\" as alias)",
                        "on error",
                        $"reveal (POSIX file \"{escapedPath}\" as alias)",
                        "end try",
                        "end tell"
                    );
                }
                else
                {
                    OpenPath(filePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error displaying properties for {filePath}: {ex.Message}");
            }
        }

        private static void RunOsaScript(params string[] scriptLines)
        {
            var psi = new ProcessStartInfo("osascript")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var line in scriptLines)
            {
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(line);
            }
            Process.Start(psi);
        }

        public static void OpenFolder(string folderPath)
        {
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", folderPath) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    Process.Start("open", $"\"{folderPath}\"");
                }
                else
                {
                    Process.Start("xdg-open", $"\"{folderPath}\"");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error launching folder {folderPath}: {ex.Message}");
            }
        }

        public static void OpenUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            try
            {
                // 1. Mobile platform callback (iOS / Android)
                if (Fujify.ViewModels.MainViewModel.PlatformOpenUrlCallback != null)
                {
                    Fujify.ViewModels.MainViewModel.PlatformOpenUrlCallback(url);
                    return;
                }

                // 2. Avalonia TopLevel Launcher API
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView && singleView.MainView != null)
                {
                    var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView);
                    if (topLevel?.Launcher != null)
                    {
                        _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
                        return;
                    }
                }
                else if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var topLevel = Avalonia.Controls.TopLevel.GetTopLevel(desktop.MainWindow);
                    if (topLevel?.Launcher != null)
                    {
                        _ = topLevel.Launcher.LaunchUriAsync(new Uri(url));
                        return;
                    }
                }

                // 3. Desktop OS fallback
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                {
                    var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                    psi.ArgumentList.Add(url);
                    Process.Start(psi);
                }
                else
                {
                    var psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
                    psi.ArgumentList.Add(url);
                    Process.Start(psi);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error opening URL {url}: {ex.Message}");
            }
        }
    }
}
