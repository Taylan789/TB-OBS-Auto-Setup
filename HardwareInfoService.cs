using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace TB_OBS_AutoSetup;

public sealed class HardwareInfoService
{
    public string Cpu => Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "Bilinmiyor";
    public string Ram => $"{GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d:0.0} GB kullanılabilir sistem belleği";
    public string Os => RuntimeInformation.OSDescription;
    public string Architecture => RuntimeInformation.OSArchitecture.ToString();

    public bool IsObsRunning => Process.GetProcessesByName("obs64").Length > 0 || Process.GetProcessesByName("obs32").Length > 0;

    public string FindObsPath()
    {
        string[] candidates =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "obs-studio", "bin", "64bit", "obs64.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "obs-studio", "bin", "64bit", "obs64.exe")
        };
        foreach (var p in candidates) if (File.Exists(p)) return p;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\OBS Studio");
            var install = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(install))
            {
                var exe = Path.Combine(install, "bin", "64bit", "obs64.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        catch { }
        return "";
    }

    public string NetworkSummary()
    {
        var active = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.Name)
            .Take(3)
            .ToArray();
        return active.Length == 0 ? "Aktif ağ bağlantısı bulunamadı." : string.Join(", ", active);
    }

    public string Summary()
    {
        string obs = IsObsRunning ? "Çalışıyor" : "Çalışmıyor";
        string path = FindObsPath();
        return $"İşletim sistemi: {Os}\nMimari: {Architecture}\nİşlemci: {Cpu}\nRAM: {Ram}\nOBS: {obs}\nOBS yolu: {(string.IsNullOrEmpty(path) ? "Bulunamadı" : path)}\nAğ: {NetworkSummary()}";
    }

    public bool LaunchObs()
    {
        string path = FindObsPath();
        if (string.IsNullOrWhiteSpace(path)) return false;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return true;
    }
}
