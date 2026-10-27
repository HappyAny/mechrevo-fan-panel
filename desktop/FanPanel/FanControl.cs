// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace FanPanel;

internal sealed record RecoveryData(string Bios, DateTimeOffset Boot, Dictionary<int, byte> Values);
internal sealed record FanState(string Mode, int Requested, int Effective, string Hint, FanSample Sample, bool Owned);

internal sealed class FanControl : IDisposable
{
    private readonly IEc ec;
    private readonly string? journal;
    private RecoveryData? saved;
    private bool hot;
    private int floor=20;
    private long appliedAt;
    private int effective=-1;
    internal string Mode { get; private set; } = "auto";
    internal int Requested { get; private set; } = 65;
    internal string Hint { get; private set; } = "由系统根据温度自动调节";
    internal bool Owned => saved != null;
    internal static DateTimeOffset BootTime => DateTimeOffset.UtcNow-TimeSpan.FromMilliseconds(Environment.TickCount64);
    private static string? journalPath;
    internal static string JournalPath => journalPath ??= SelectJournalPath();
    private static string SelectJournalPath()
    {
        string[] folders=[Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MechrevoFanPanel"),Path.Combine(AppContext.BaseDirectory,"state")];
        foreach(var folder in folders)
        {
            var existing=Path.Combine(folder,"recovery-v2.json");
            if(File.Exists(existing)) return existing;
        }
        foreach(var folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string probe=Path.Combine(folder,".write-check-"+Guid.NewGuid());
                using(var file=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)) file.WriteByte(0);
                return Path.Combine(folder,"recovery-v2.json");
            }
            catch(UnauthorizedAccessException) { }
            catch(IOException) { }
        }
        throw new IOException("无法保存恢复记录。请把程序放到可写文件夹后重试。");
    }
    internal FanControl(IEc ec, string? journal=null) { this.ec=ec; this.journal=journal; }
    internal static int ThermalFloor(int temp) => temp>=85 ? 100 : temp>=80 ? 80 : temp>=75 ? 65 : temp>=65 ? 40 : 20;
    private void Bits(int address, int mask, int value) => ec.Write(address, (byte)((ec.Read(address)&~mask)|(value&mask)));
    private bool TableActive() => (ec.Read(Registers.Custom)&64)!=0 && (ec.Read(Registers.ApExist)&1)!=0 && (ec.Read(Registers.FanManagement)&4)!=0;
    private void Begin()
    {
        if (saved!=null) return;
        if (TableActive()) throw new InvalidOperationException("其他程序正在使用自定义风扇表，请先退出该程序并恢复自动。");
        var data = new RecoveryData(EcDevice.Bios, BootTime, Registers.Saved.ToDictionary(a=>a,a=>ec.Read(a)));
        if (journal!=null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
            var tmp=journal+".pending";
            File.WriteAllText(tmp,JsonSerializer.Serialize(data));
            File.Move(tmp,journal,true);
        }
        saved=data;
    }
    internal void Manual(int percent)
    {
        if (percent is <20 or >100) throw new ArgumentOutOfRangeException(nameof(percent),"百分比必须在 20–100 之间。");
        // This shared custom gate also activates AP power overrides. Refuse to
        // activate it if any are present; do not write CPU power registers.
        foreach (var a in new[]{0x783,0x784,0x785,0x786}) if(ec.Read(a)!=0) throw new InvalidOperationException("检测到额外的功率或温度覆盖设置，暂不能启用手动风量。");
        Begin();
        try
        {
            Requested=percent; Mode="manual";
            ApplyManual(FanSample.Read(ec));
        }
        catch { Restore(); throw; }
    }
    private void ApplyManual(FanSample s)
    {
        if(s.Temperature>=85) hot=true;
        if(hot && s.Temperature<=78) { hot=false; floor=ThermalFloor(s.Temperature); }
        int candidate=ThermalFloor(s.Temperature);
        int release=floor switch {100=>82,80=>77,65=>72,40=>62,_=>0};
        if(candidate>=floor || s.Temperature<=release) floor=candidate;
        int next = Math.Max(Requested, hot ? 100 : floor);
        Hint = hot ? "高温保护中 · 降至 78°C 后恢复手动风量" : next>Requested ? $"温度保护 · 当前最低风量 {next}%" : "双风扇联动 · 设定值为占空比";
        if(next==effective && s.Boost==hot) return;
        Bits(Registers.FanManagement,4,0);
        foreach(var a in Registers.Duty) ec.Write(a,(byte)(next*2));
        Bits(Registers.Relationship,128,0);
        ec.Write(Registers.Transition,0x81);
        Bits(Registers.ApExist,1,1);
        Bits(Registers.Custom,64,64);
        Bits(Registers.Mode,64,hot?64:0);
        Bits(Registers.FanManagement,4,4);
        if(!TableActive()) throw new IOException("硬件未确认手动控制状态。");
        effective=next; appliedAt=Environment.TickCount64;
    }
    internal void Boost()
    {
        if(Owned) Restore();
        Begin();
        try { Bits(Registers.Mode,64,64); Mode="boost"; effective=100; Hint="双风扇满速散热"; }
        catch { Restore(); throw; }
    }
    internal FanState Tick()
    {
        var sample=FanSample.Read(ec);
        if(Mode=="manual")
        {
            if(!TableActive() || (ec.Read(Registers.Mode)&0x90)!=(saved!.Values[Registers.Mode]&0x90))
                throw new IOException("外部控制改变了风扇模式，已停止手动控制。");
            foreach(var a in Registers.Duty) if(ec.Read(a)!=effective*2) throw new IOException("风扇表被其他程序修改，已停止手动控制。");
            ApplyManual(sample);
            if(Environment.TickCount64-appliedAt>15000 && (sample.Fan1Rpm<500 || sample.Fan2Rpm<500))
                throw new IOException("检测到风扇未正常转动，已退出手动控制。");
            if(Environment.TickCount64-appliedAt>20000 && (Math.Abs(sample.Fan1Percent-effective)>3 || Math.Abs(sample.Fan2Percent-effective)>3))
                throw new IOException("实际风量未达到设定值，已退出手动控制。");
        }
        return new(Mode,Requested,effective,Hint,sample,Owned);
    }
    internal void Restore()
    {
        if(saved==null) { Mode="auto"; effective=-1; Hint="由系统根据温度自动调节"; return; }
        try
        {
            RestoreValues(ec,saved);
            if(journal!=null && File.Exists(journal)) File.Delete(journal);
            saved=null; Mode="auto"; effective=-1; hot=false; floor=20; Hint="已交还原来的自动控制";
        }
        catch
        {
            // A recovery failure must never silently leave a low fixed duty.
            try { Bits(Registers.Mode,64,64); } catch { }
            throw new IOException("自动恢复未确认，已尝试开启强冷。请正常重启电脑；恢复记录已保留。");
        }
    }
    private static void ValidateRecovery(RecoveryData data)
    {
        if(data.Bios!=EcDevice.Bios || data.Values.Count!=Registers.Saved.Length || Registers.Saved.Any(a=>!data.Values.ContainsKey(a))) throw new InvalidDataException("恢复记录无效。");
        if((data.Values[Registers.Custom]&64)!=0 && (data.Values[Registers.ApExist]&1)!=0 && (data.Values[Registers.FanManagement]&4)!=0) throw new InvalidDataException("恢复记录包含外部手动控制状态。");
    }
    internal static void RestoreValues(IEc ec, RecoveryData data)
    {
        ValidateRecovery(data);
        void Bits(int a,int mask,int value) => ec.Write(a,(byte)((ec.Read(a)&~mask)|(value&mask)));
        Bits(Registers.FanManagement,4,0);
        Bits(Registers.Custom,64,0);
        foreach(var a in Registers.Duty) ec.Write(a,data.Values[a]);
        ec.Write(Registers.Transition,data.Values[Registers.Transition]);
        Bits(Registers.Relationship,128,data.Values[Registers.Relationship]);
        Bits(Registers.ApExist,1,data.Values[Registers.ApExist]);
        Bits(Registers.Mode,64,data.Values[Registers.Mode]);
        Bits(Registers.Custom,64,data.Values[Registers.Custom]);
        Bits(Registers.FanManagement,4,data.Values[Registers.FanManagement]);
    }
    internal static bool Recover(IEc ec,string journal)
    {
        if(!File.Exists(journal)) return false;
        if(new FileInfo(journal).Length>8192) throw new InvalidDataException("恢复记录过大。");
        var data=JsonSerializer.Deserialize<RecoveryData>(File.ReadAllText(journal)) ?? throw new InvalidDataException("恢复记录为空。");
        ValidateRecovery(data);
        // A new boot resets EC state. Never replay a previous boot's RAM bytes.
        if(Math.Abs((data.Boot-BootTime).TotalSeconds)<60) RestoreValues(ec,data);
        File.Delete(journal);
        return true;
    }
    public void Dispose() => Restore();
}
