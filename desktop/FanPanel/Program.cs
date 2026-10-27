// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Windows;

namespace FanPanel;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if(args.Length>0 && args[0]=="--worker") return Worker.Run(int.Parse(args[1]),long.Parse(args[2])).GetAwaiter().GetResult();
        if(args.Length>0 && args[0]=="--self-test") return SelfTests.Run(args[1]);
        if(args.Length>0 && args[0]=="--hardware-test") return HardwareTest.Run(args[1]).GetAwaiter().GetResult();
        if(args.Length>0 && args[0]=="--lifecycle-test") return LifecycleTest.Run(args[1]).GetAwaiter().GetResult();
        if(args.Length>0 && args[0]=="--parent-exit") return LifecycleTest.ParentExit(args[1]).GetAwaiter().GetResult();
        if(args.Length>0 && args[0]=="--probe")
        {
            try { using var ec=new EcDevice(); File.WriteAllText(args[1],JsonSerializer.Serialize(new { Sample=FanSample.Read(ec), Registers=Registers.Saved.ToDictionary(a=>a,a=>ec.Read(a)) })); return 0; }
            catch(Exception e) { File.WriteAllText(args[1],JsonSerializer.Serialize(new{Error=e.Message})); return 2; }
        }
        using var single=new Mutex(false,@"Local\MechrevoFanPanel-Fluent-v2",out bool created);
        if(!created) { MessageBox.Show("风扇工具已打开，请使用现有窗口。","无界风扇"); return 1; }
        var app=new App(); app.InitializeComponent();
        return app.Run(new MainWindow(args.Contains("--demo")));
    }
}
