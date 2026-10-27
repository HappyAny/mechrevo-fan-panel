// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace FanPanel;

internal sealed class FakeEc : IEc
{
    internal Dictionary<int,byte> Values=Registers.Readable.ToDictionary(x=>x,x=>(byte)0);
    internal List<(int Address,byte Value)> Writes=[];
    internal int FailAt=-1;
    internal FakeEc()
    {
        Values[Registers.ApExist]=129; Values[Registers.Mode]=16; Values[Registers.FanManagement]=4;
        Values[Registers.Relationship]=32; Values[0x43e]=56;
        Values[0x464]=Values[0x46c]=10; Values[0x75b]=Values[0x75c]=50;
        foreach(var a in Registers.Duty) Values[a]=(byte)(a%200);
    }
    public byte Read(int a)=>Values[a];
    public void Write(int a,byte v)
    {
        if(Writes.Count==FailAt) {FailAt=-1;throw new IOException("Injected I/O fault");}
        if(!Registers.Writable.Contains(a)) throw new Exception("Unexpected write");
        Writes.Add((a,v));Values[a]=v;
    }
    public void Dispose(){}
}
internal static class SelfTests
{
    internal static int Run(string output)
    {
        var passed=new List<string>();
        void Check(bool ok,string name) {if(!ok) throw new Exception(name);passed.Add(name);}
        void Reject(System.Action action,string name) {try{action();}catch{passed.Add(name);return;}throw new Exception(name);}
        try
        {
            var ec=new FakeEc(); var initial=new Dictionary<int,byte>(ec.Values);
            var ctrl=new FanControl(ec);
            ctrl.Manual(61);
            Check(Registers.Duty.All(a=>ec.Values[a]==122),"61 percent writes both 16-point duty tables");
            Check((ec.Values[Registers.Mode]&0x90)==16,"base performance mode is preserved");
            Check(ec.Values[Registers.ApExist]==129 && ec.Values[Registers.Relationship]==32,"unrelated register bits are preserved");
            Check(ec.Writes[0]==(Registers.FanManagement,(byte)0),"table lookup is disabled before any table write");
            Check(ec.Writes[^1]==(Registers.FanManagement,(byte)4),"table lookup is enabled after all table writes");
            Check(!ec.Writes.Any(x=>x.Address is >=0x783 and <=0x786),"CPU power and temperature overrides are never written");
            ctrl.Manual(62);Check(Registers.Duty.All(a=>ec.Values[a]==124),"one percent increments change duty by two");
            ec.Values[0x43e]=86; var hot=ctrl.Tick(); Check(hot.Effective==100 && (ec.Values[Registers.Mode]&64)!=0,"85 C forces boost and 100 percent");
            ec.Values[0x43e]=82;Check(ctrl.Tick().Effective==100,"thermal boost has hysteresis");
            ec.Values[0x43e]=78;Check(ctrl.Tick().Effective==65 && (ec.Values[Registers.Mode]&64)==0,"78 C releases boost but retains thermal floor");
            ec.Values[0x43e]=56;Check(ctrl.Tick().Effective==62,"cooling restores requested percentage");
            ctrl.Restore();Check(Registers.Saved.All(a=>ec.Values[a]==initial[a]),"all saved fan bytes restore exactly");
            int count=ec.Writes.Count;ctrl.Restore();Check(ec.Writes.Count==count,"unowned restore performs no writes");
            Reject(()=>ctrl.Manual(19),"below-range request is rejected");Reject(()=>ctrl.Manual(101),"above-range request is rejected");
            Check(ec.Writes.Count==count,"invalid range does not mutate hardware");
            ec.Values[0x783]=45;Reject(()=>ctrl.Manual(65),"nonzero shared power override blocks manual mode");Check(ec.Writes.Count==count,"blocked power override does not mutate hardware");ec.Values[0x783]=0;
            ec.Values[Registers.Custom]=64;Reject(()=>ctrl.Manual(65),"external table ownership is rejected");ec.Values[Registers.Custom]=0;
            ec.FailAt=ec.Writes.Count+7;Reject(()=>ctrl.Manual(65),"partial table failure is reported");Check(Registers.Saved.All(a=>ec.Values[a]==initial[a]),"partial table failure rolls back every saved byte");
            ctrl.Boost();Check((ec.Values[Registers.Mode]&64)!=0,"boost sets only fan boost bit");ctrl.Restore();
            ctrl.Manual(65);ec.Values[Registers.Mode]=0;Reject(()=>ctrl.Tick(),"external performance change ends manual ownership");ctrl.Restore();Check((ec.Values[Registers.Mode]&0x90)==0,"restore preserves external performance change");
            var bad=new RecoveryData(EcDevice.Bios,FanControl.BootTime,new(){[0x783]=100});Reject(()=>FanControl.RestoreValues(ec,bad),"recovery rejects arbitrary write addresses");
            Check(FanControl.ThermalFloor(64)==20 && FanControl.ThermalFloor(65)==40 && FanControl.ThermalFloor(75)==65 && FanControl.ThermalFloor(80)==80,"temperature thresholds enforce expected floors");
            string journal=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!,"fan-test-"+Guid.NewGuid()+".json");
            var other=new FakeEc();var c2=new FanControl(other,journal);c2.Manual(73);Check(File.Exists(journal),"durable recovery snapshot exists before manual use");
            Check(FanControl.Recover(other,journal) && !File.Exists(journal),"same-boot recovery restores and removes journal");
            Check(Registers.Saved.All(a=>other.Values[a]==initial[a]),"same-boot recovery restores all original bytes");
            File.WriteAllText(output,JsonSerializer.Serialize(new{Ok=true,Count=passed.Count,Passed=passed},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception e){File.WriteAllText(output,JsonSerializer.Serialize(new{Ok=false,Count=passed.Count,Passed=passed,Error=e.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}
