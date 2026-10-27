// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FanPanel;

internal sealed record Command(string Action, int Percent=65);
internal sealed record WireMessage(FanState? State=null, string? Error=null, bool Ready=false, bool Restored=false);

internal static class Worker
{
    internal static Mutex Acquire()
    {
        var mutex=new Mutex(false,@"Local\MechrevoFanPanel-v1",out var created);
        if(!created) { mutex.Dispose(); throw new InvalidOperationException("风扇控制工具已在运行。请先关闭旧窗口。"); }
        return mutex;
    }
    internal static async Task<int> Run(int parentId, long parentStart)
    {
        using var input=new StreamReader(Console.OpenStandardInput(),Encoding.UTF8);
        using var output=new StreamWriter(Console.OpenStandardOutput(),new UTF8Encoding(false)){AutoFlush=true};
        async Task Send(WireMessage msg) => await output.WriteLineAsync(JsonSerializer.Serialize(msg));
        try
        {
            using var parent=Process.GetProcessById(parentId);
            if(parent.StartTime.ToUniversalTime().Ticks!=parentStart) throw new InvalidOperationException("控制窗口已退出。");
            using var mutex=Acquire();
            using var ec=new EcDevice();
            FanControl.Recover(ec,FanControl.JournalPath);
            using var ctrl=new FanControl(ec,FanControl.JournalPath);
            await Send(new(ctrl.Tick(),Ready:true));
            var line=input.ReadLineAsync();
            long lastHeartbeat=Environment.TickCount64, lastOutput=0;
            try
            {
                while(!parent.HasExited && Environment.TickCount64-lastHeartbeat<4000)
                {
                    await Task.WhenAny(line,Task.Delay(250));
                    if(line.IsCompleted)
                    {
                        var text=await line;
                        if(text==null) break;
                        if(text.Length>1024) throw new InvalidDataException("控制消息过长。");
                        var cmd=JsonSerializer.Deserialize<Command>(text) ?? throw new InvalidDataException("控制消息为空。");
                        lastHeartbeat=Environment.TickCount64;
                        if(cmd.Action=="quit") break;
                        try
                        {
                            switch(cmd.Action)
                            {
                                case "manual": ctrl.Manual(cmd.Percent); break;
                                case "boost": ctrl.Boost(); break;
                                case "auto": ctrl.Restore(); break;
                                case "ping": break;
                                default: throw new InvalidDataException("不支持该控制命令。");
                            }
                        }
                        catch(Exception e) { ctrl.Restore(); await Send(new(ctrl.Tick(),e.Message)); }
                        line=input.ReadLineAsync();
                    }
                    if(Environment.TickCount64-lastOutput>=450)
                    {
                        try { await Send(new(ctrl.Tick())); }
                        catch(Exception e) { ctrl.Restore(); await Send(new(Error:e.Message)); }
                        lastOutput=Environment.TickCount64;
                    }
                }
            }
            finally { ctrl.Restore(); }
            try { await Send(new(ctrl.Tick(),Restored:true)); } catch(IOException) { }
            return 0;
        }
        catch(Exception e) { try { await Send(new(Error:e.Message)); } catch { } return 2; }
    }
    internal static void EmergencyRecover()
    {
        using var mutex=Acquire();
        using var ec=new EcDevice();
        FanControl.Recover(ec,FanControl.JournalPath);
    }
}

internal sealed class Backend : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim writeLock=new(1,1);
    private Task? reader;
    private readonly TaskCompletionSource<bool> ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal event Action<WireMessage>? Message;
    internal FanState? Latest { get; private set; }
    internal bool Restored { get; private set; }
    internal bool Closing { get; private set; }
    private bool disposed;
    internal int Pid => process.Id;
    internal bool IsAlive => !process.HasExited;
    internal Backend()
    {
        var parent=Process.GetCurrentProcess();
        var start=new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8 };
        start.ArgumentList.Add("--worker"); start.ArgumentList.Add(parent.Id.ToString()); start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
        process=new Process{StartInfo=start};
    }
    internal async Task Start()
    {
        process.Start();
        _=process.StandardError.ReadToEndAsync();
        reader=Read();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }
    private async Task Read()
    {
        try
        {
            while(await process.StandardOutput.ReadLineAsync() is {} line)
            {
                if(line.Length>16384) throw new IOException("控制后台返回的数据过长。");
                var msg=JsonSerializer.Deserialize<WireMessage>(line) ?? throw new IOException("后台状态为空。");
                if(msg.State!=null) Latest=msg.State;
                if(msg.Ready) ready.TrySetResult(true);
                if(msg.Error!=null) ready.TrySetException(new IOException(msg.Error));
                if(msg.Restored) Restored=true;
                Message?.Invoke(msg);
            }
            await process.WaitForExitAsync();
            if(!Restored)
            {
                try { Worker.EmergencyRecover(); Restored=true; }
                catch(Exception e) { Message?.Invoke(new(Error:"后台异常退出；"+e.Message)); }
            }
            ready.TrySetException(new IOException("控制后台未能启动。"));
            if(!Closing) Message?.Invoke(new(Error:Restored?"控制后台已退出，已执行自动恢复。请重新打开工具。":"控制后台已退出，自动恢复未确认。请正常重启电脑。"));
        }
        catch(Exception e)
        {
            ready.TrySetException(e);
            try { process.StandardInput.Close(); } catch { }
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
                if(!Restored) {Worker.EmergencyRecover();Restored=true;}
            }
            catch(Exception recovery) { Message?.Invoke(new(Error:"自动恢复未确认："+recovery.Message)); }
            Message?.Invoke(new(Error:e.Message));
        }
    }
    internal async Task Send(string action,int percent=65)
    {
        await writeLock.WaitAsync();
        try
        {
            if(process.HasExited) throw new IOException("控制后台已退出。");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new Command(action,percent)));
            await process.StandardInput.FlushAsync();
        }
        finally { writeLock.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed) return;
        Closing=true;
        try { if(!process.HasExited) { await Send("quit"); process.StandardInput.Close(); } } catch { }
        if(reader!=null)
        {
            try { await reader.WaitAsync(TimeSpan.FromSeconds(8)); }
            catch(TimeoutException) { throw new IOException("控制后台尚未确认恢复，请暂时保留窗口。"); }
        }
        if(!Restored && reader!=null) throw new IOException("自动恢复未确认，请正常重启电脑。");
        process.Dispose();
        disposed=true;
    }
}
