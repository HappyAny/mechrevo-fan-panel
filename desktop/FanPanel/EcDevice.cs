// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FanPanel;

internal interface IEc : IDisposable
{
    byte Read(int address);
    void Write(int address, byte value);
}

internal static class Registers
{
    internal const int Custom = 0x727, ApExist = 0x741, Mode = 0x751;
    internal const int Relationship = 0x7c5, FanManagement = 0x7c6, Transition = 0x787;
    internal static readonly int[] Duty = Enumerable.Range(0xf20, 16).Concat(Enumerable.Range(0xf50, 16)).ToArray();
    internal static readonly int[] Saved = [Custom, ApExist, Mode, Relationship, FanManagement, Transition, ..Duty];
    internal static readonly HashSet<int> Writable = Saved.ToHashSet();
    internal static readonly HashSet<int> Readable = Saved.Concat([0x43e,0x464,0x465,0x46c,0x46b,0x75b,0x75c,0x783,0x784,0x785,0x786]).ToHashSet();
}

internal sealed class EcDevice : IEc
{
    internal const string Bios = "N.1.14MRO20";
    private readonly SafeFileHandle handle;
    internal static void CheckIdentity()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        string Get(string name) => key?.GetValue(name) is string[] values ? string.Join(" ", values) : (key?.GetValue(name)?.ToString() ?? "");
        if (!Get("SystemManufacturer").Trim().Equals("MECHREVO", StringComparison.OrdinalIgnoreCase) ||
            !Get("SystemProductName").Trim().Equals("WUJIE14XA", StringComparison.OrdinalIgnoreCase) || Get("BIOSVersion").Trim() != Bios)
            throw new InvalidOperationException("仅支持已验证的无界 14XA / BIOS N.1.14MRO20。此机器已禁用硬件控制。");
    }
    internal EcDevice()
    {
        CheckIdentity();
        handle = CreateFileW(@"\\.\ACPIDriver", 0xc0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法打开官方风扇驱动，请确认机械革命控制中心已安装。");
    }
    public byte Read(int address)
    {
        if (!Registers.Readable.Contains(address)) throw new ArgumentOutOfRangeException(nameof(address));
        uint[] request = [(uint)address, 1];
        if (!DeviceIoControl(handle, 2621482120, request, 8, out uint value, 4, out uint count, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "风扇传感器读取失败。");
        if (count != 4 || value > 255) throw new InvalidDataException("风扇驱动返回的数据无效。");
        return (byte)value;
    }
    public void Write(int address, byte value)
    {
        if (!Registers.Writable.Contains(address)) throw new ArgumentOutOfRangeException(nameof(address));
        uint[] request = [(uint)address, value];
        if (!DeviceIoControl(handle, 2621482124, request, 8, out _, 4, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error(), "风扇控制写入失败。");
        if (Read(address) != value) throw new IOException($"风扇设置未通过回读确认：{address:X4}。");
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, uint[] input, uint inputSize, out uint output, uint outputSize, out uint returned, IntPtr overlapped);
}

internal sealed record FanSample(int Temperature, int Fan1Rpm, int Fan2Rpm, double Fan1Percent, double Fan2Percent, bool Boost)
{
    internal static FanSample Read(IEc ec)
    {
        int Rpm(int high, int low)
        {
            // Repeat on high-byte rollover instead of combining two different RPM values.
            for (int attempt=0; attempt<3; attempt++) { var h=ec.Read(high); var l=ec.Read(low); if(h==ec.Read(high)) return h*256+l; }
            throw new IOException("风扇转速读数不稳定。");
        }
        var s = new FanSample(ec.Read(0x43e), Rpm(0x464,0x465), Rpm(0x46c,0x46b), ec.Read(0x75b)/2.0, ec.Read(0x75c)/2.0, (ec.Read(Registers.Mode)&64)!=0);
        if (s.Temperature is <1 or >110 || s.Fan1Rpm>15000 || s.Fan2Rpm>15000 || s.Fan1Percent>100 || s.Fan2Percent>100) throw new IOException("温度或转速读数超出可信范围。");
        return s;
    }
}
