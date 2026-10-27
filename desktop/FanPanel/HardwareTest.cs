// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;

namespace FanPanel;

internal static class HardwareTest
{
    internal static async Task<int> Run(string output)
    {
        var rows=new List<object>();
        string? error=null;
        bool exact=false;
        Backend? backend=null;
        Dictionary<int,byte>? before=null, power=null;
        try
        {
            using var ec=new EcDevice();
            before=Registers.Saved.ToDictionary(a=>a,a=>ec.Read(a));
            power=new[]{0x783,0x784,0x785,0x786}.ToDictionary(a=>a,a=>ec.Read(a));
            rows.Add(new{Phase="before",Sample=FanSample.Read(ec)});
            backend=new();
            string? commandError=null;
            backend.Message+=m=>{if(m.Error!=null)commandError=m.Error;};
            await backend.Start();
            foreach(int percent in new[]{80,60,45,81})
            {
                await backend.Send("manual",percent);
                int stable=0;
                for(int i=0;i<60;i++)
                {
                    await Task.Delay(500);await backend.Send("ping");
                    if(commandError!=null) throw new IOException(commandError);
                    if(i%4==3) rows.Add(new{Phase=$"target-{percent}",State=backend.Latest});
                    var current=backend.Latest;
                    bool settled=current?.Mode=="manual" && Math.Abs(current.Sample.Fan1Percent-current.Effective)<=1 && Math.Abs(current.Sample.Fan2Percent-current.Effective)<=1;
                    stable=settled?stable+1:0;
                    if(i>=15 && stable>=4) break;
                }
                var state=backend.Latest!;
                if(state.Mode!="manual" || Math.Abs(state.Sample.Fan1Percent-state.Effective)>3 || Math.Abs(state.Sample.Fan2Percent-state.Effective)>3) throw new IOException($"{percent}% actual duty did not settle");
            }
            await backend.Send("auto");
            for(int i=0;i<16;i++){await Task.Delay(500);await backend.Send("ping");if(i%4==3)rows.Add(new{Phase="auto",State=backend.Latest});}
            await backend.DisposeAsync();
            exact=Registers.Saved.All(a=>ec.Read(a)==before[a]) && power.All(pair=>ec.Read(pair.Key)==pair.Value);
            if(!exact)throw new IOException("Saved registers did not restore exactly");
        }
        catch(Exception e){error=e.ToString();}
        finally
        {
            if(backend!=null)try{await backend.DisposeAsync();}catch(Exception e){error+="\n"+e;}
            if(before!=null && power!=null)try
            {
                using var finalEc=new EcDevice();
                exact=Registers.Saved.All(a=>finalEc.Read(a)==before[a]) && power.All(pair=>finalEc.Read(pair.Key)==pair.Value);
                rows.Add(new{Phase="final",Sample=FanSample.Read(finalEc)});
            }catch(Exception e){error+="\n"+e;}
        }
        File.WriteAllText(output,JsonSerializer.Serialize(new{Ok=error==null,ExactRestore=exact,Error=error,Samples=rows},new JsonSerializerOptions{WriteIndented=true}));
        return error==null?0:2;
    }
}
