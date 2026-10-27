// SPDX-License-Identifier: GPL-3.0-only
using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FanPanel;

public partial class MainWindow : Window
{
    private Backend? backend;
    private readonly bool demo;
    private bool ready, manual, closing, closeReady;
    private string theme="System";
    private readonly DispatcherTimer heartbeat=new(){Interval=TimeSpan.FromMilliseconds(500)};
    private readonly DispatcherTimer debounce=new(){Interval=TimeSpan.FromMilliseconds(180)};
    internal MainWindow(bool demo)
    {
        this.demo=demo;
        InitializeComponent();
        ApplyTheme("System");
        SystemEvents.UserPreferenceChanged+=PreferenceChanged;
        Loaded+=async(_,_)=>await Start();
        Closing+=OnClosing;
        Closed+=(_,_)=>SystemEvents.UserPreferenceChanged-=PreferenceChanged;
        heartbeat.Tick+=async(_,_)=>{ if(ready && !demo && !closing) try { await backend!.Send("ping"); } catch(Exception e) { ready=false; SetControls(); Error(e.Message); } };
        debounce.Tick+=async(_,_)=>{debounce.Stop(); if(manual) await Action("manual");};
    }
    private async Task Start()
    {
        if(demo)
        {
            ready=true; SetControls();
            ConnectionText.Text="界面预览 · 未连接硬件";
            Update(new("auto",65,-1,"由系统根据温度自动调节",new(56,2184,2113,25,25,false),false));
            return;
        }
        try
        {
            backend=new();
            backend.Message+=msg=>Dispatcher.BeginInvoke(()=>
            {
                if(msg.State!=null) Update(msg.State);
                if(msg.Error!=null) { Error(msg.Error); if(msg.State==null) {ready=false;SetControls();} }
            });
            await backend.Start(); ready=true; SetControls();
            ConnectionText.Text="已连接 · 本机风扇驱动";
            heartbeat.Start();
        }
        catch(Exception e) { ModeText.Text="暂时无法控制"; StateHint.Text="请检查下方提示"; ConnectionText.Text="连接失败"; Error(e.Message); }
    }
    private void SetControls() { AutoButton.IsEnabled=ManualButton.IsEnabled=BoostButton.IsEnabled=ready && !closing; DutySlider.IsEnabled=ready && !closing; }
    private void Update(FanState state)
    {
        manual=state.Mode=="manual";
        ModeText.Text=state.Mode switch {"manual"=>state.Effective>state.Requested?"手动 · 温度保护":"手动风量", "boost"=>"满速强冷", _=>state.Sample.Boost?"外部强冷运行中":"自动调节"};
        StateHint.Text=state.Hint;
        TemperatureText.Text=$"{state.Sample.Temperature}°";
        if(state.Sample.Temperature>=85) TemperatureText.Foreground=Brushes.OrangeRed;
        else TemperatureText.SetResourceReference(TextBlock.ForegroundProperty,"PrimaryBrush");
        Fan1Text.Text=state.Sample.Fan1Rpm.ToString("N0"); Fan2Text.Text=state.Sample.Fan2Rpm.ToString("N0");
        Fan1Bar.Value=state.Sample.Fan1Percent; Fan2Bar.Value=state.Sample.Fan2Percent;
        Duty1Text.Text=$"实际占空比 {state.Sample.Fan1Percent:0.#}%"; Duty2Text.Text=$"实际占空比 {state.Sample.Fan2Percent:0.#}%";
        ControlHint.Text=manual ? $"目标 {state.Requested}% · 当前生效 {state.Effective}% · 拖动即调整" : "选择手动后，滑块会实时调整风量。";
        AutoButton.FontWeight=state.Mode=="auto"?FontWeights.Bold:FontWeights.Normal;
        ManualButton.FontWeight=manual?FontWeights.Bold:FontWeights.Normal;
        BoostButton.FontWeight=state.Mode=="boost"?FontWeights.Bold:FontWeights.Normal;
        AutoButton.Tag=state.Mode=="auto"?"selected":null;
        ManualButton.Tag=manual?"selected":null;
        BoostButton.Tag=state.Mode=="boost"?"selected":null;
    }
    private void Error(string message) { ErrorText.Text=message; ErrorPanel.Visibility=Visibility.Visible; }
    private async Task Action(string action)
    {
        if(!ready || closing) return;
        ErrorPanel.Visibility=Visibility.Collapsed;
        if(demo)
        {
            int pct=(int)DutySlider.Value;
            Update(new(action, pct, action=="auto"?-1:action=="boost"?100:pct,"界面预览 · 未改变硬件",new(56,action=="auto"?2184:pct*58,action=="auto"?2113:pct*57,action=="auto"?25:pct,action=="auto"?25:pct,action=="boost"),false));
            return;
        }
        try { await backend!.Send(action,(int)DutySlider.Value); }
        catch(Exception e) { Error(e.Message); }
    }
    private async void AutoClick(object sender,RoutedEventArgs e) { debounce.Stop(); manual=false; await Action("auto"); }
    private async void ManualClick(object sender,RoutedEventArgs e) => await Action("manual");
    private async void BoostClick(object sender,RoutedEventArgs e) { debounce.Stop(); manual=false; await Action("boost"); }
    private void SliderChanged(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(TargetText==null) return;
        TargetText.Text=$"{(int)e.NewValue}%";
        if(manual && ready) {debounce.Stop();debounce.Start();}
    }
    private async void OnClosing(object? sender,CancelEventArgs e)
    {
        if(closeReady || demo) return;
        e.Cancel=true;
        if(closing) return;
        closing=true; SetControls(); heartbeat.Stop(); debounce.Stop();
        StateHint.Text="正在恢复原来的风扇控制…";
        try
        {
            if(backend!=null) await backend.DisposeAsync();
            closeReady=true; _=Dispatcher.BeginInvoke(Close);
        }
        catch(Exception error) { closing=false; Error(error.Message); StateHint.Text="自动恢复尚未确认"; }
    }
    private void SettingsClick(object sender,RoutedEventArgs e) { var button=(Button)sender; button.ContextMenu.PlacementTarget=button; button.ContextMenu.IsOpen=true; }
    private void ThemeClick(object sender,RoutedEventArgs e) => ApplyTheme((string)((MenuItem)sender).Tag);
    private void PreferenceChanged(object sender,UserPreferenceChangedEventArgs e) {if(theme=="System") Dispatcher.BeginInvoke(()=>ApplyTheme("System"));}
    private void ApplyTheme(string value)
    {
        theme=value;
        Application.Current.ThemeMode=value switch {"Light"=>ThemeMode.Light,"Dark"=>ThemeMode.Dark,_=>ThemeMode.System};
        bool dark=value=="Dark" || (value=="System" && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",1) is int x && x==0);
        string[] keys=["CanvasBrush","SurfaceBrush","InsetBrush","StrokeBrush","PrimaryBrush","SecondaryBrush","AccentBrush","AccentSoftBrush"];
        string[] colors=dark ? ["#171B22","#202630","#282F3B","#343E4C","#F3F5FA","#AAB6C9","#78A9FF","#24354E"] : ["#F3F5F9","#FFFFFF","#F5F7FA","#E4E7ED","#192230","#657287","#3268D4","#EDF3FF"];
        for(int i=0;i<keys.Length;i++) Application.Current.Resources[keys[i]]=new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
    }
    private void AboutClick(object sender,RoutedEventArgs e)
    {
        using var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("FanPanel.License.txt")!;
        using var reader=new StreamReader(stream);
        var box=new TextBox{Text="无界风扇 2.0\nGPL-3.0-only\n\n硬件接口研究：minortex/mech-forza-control、w568w。\n原生 WPF Fluent 界面，使用 .NET 10。\n\n"+reader.ReadToEnd(),IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(16)};
        new Window{Title="关于与开源许可",Width=570,Height=520,Content=box,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner}.ShowDialog();
    }
}
