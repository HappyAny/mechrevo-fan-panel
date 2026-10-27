// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text.Json;

namespace FanPanel;

internal static class LifecycleTest
{
    internal static async Task<int> ParentExit(string receipt)
    {
        var backend=new Backend();await backend.Start();await backend.Send("manual",75);
        for(int i=0;i<4;i++) {await Task.Delay(500);await backend.Send("ping");}
        File.WriteAllText(receipt,JsonSerializer.Serialize(new{Parent=Environment.ProcessId,Worker=backend.Pid,State=backend.Latest}));
        Environment.Exit(137); // Deliberately bypass cleanup to exercise the surviving guard.
        return 137;
    }
    internal static async Task<int> Run(string output)
    {
        var results=new List<object>();string? error=null;
        try
        {
            using var ec=new EcDevice();
            var original=Registers.Saved.ToDictionary(a=>a,a=>ec.Read(a));
            async Task Confirm(string scenario)
            {
                bool exact=false;
                for(int i=0;i<24;i++) {if(Registers.Saved.All(a=>ec.Read(a)==original[a])){exact=true;break;}await Task.Delay(250);}
                results.Add(new{Scenario=scenario,ExactRestore=exact,Sample=FanSample.Read(ec)});
                if(!exact)throw new IOException(scenario+" did not restore original fan bytes");
            }
            await using(var silent=new Backend())
            {
                await silent.Start();await silent.Send("manual",75);await Task.Delay(1000);
                if(silent.Latest?.Mode!="manual")throw new IOException("Heartbeat test did not enter manual mode");
                await Task.Delay(5000);
                await Confirm("heartbeat timeout");
            }
            await using(var crashed=new Backend())
            {
                await crashed.Start();await crashed.Send("manual",75);await Task.Delay(1000);
                if(crashed.Latest?.Mode!="manual")throw new IOException("Worker crash test did not enter manual mode");
                using var child=Process.GetProcessById(crashed.Pid);child.Kill();await child.WaitForExitAsync();
                await Confirm("worker process terminated; frontend recovery");
            }
            string receipt=output+".parent.json";
            var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("--parent-exit");start.ArgumentList.Add(receipt);
            using var parent=Process.Start(start)!;await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            if(parent.ExitCode!=137)throw new IOException("Parent crash test exited unexpectedly");
            await Confirm("parent process exited without cleanup; guard recovery");
            var info=JsonDocument.Parse(File.ReadAllText(receipt));int pid=info.RootElement.GetProperty("Worker").GetInt32();
            try {using var orphan=Process.GetProcessById(pid);await orphan.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));}
            catch(ArgumentException) { }
            results.Add(new{Scenario="orphan guard exits",Passed=true});
        }
        catch(Exception e){error=e.ToString();}
        File.WriteAllText(output,JsonSerializer.Serialize(new{Ok=error==null,Error=error,Results=results},new JsonSerializerOptions{WriteIndented=true}));return error==null?0:2;
    }
}
