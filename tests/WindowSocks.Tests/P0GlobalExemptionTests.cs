using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;
namespace WindowSocks.Tests;
public sealed class P0GlobalExemptionTests
{
    [Theory]
    [InlineData("Emulator",0)][InlineData("Emulator",1)][InlineData("Emulator",2)]
    [InlineData("Overflow",0)][InlineData("Overflow",1)][InlineData("Overflow",2)]
    [InlineData("GameGroup",0)][InlineData("GameGroup",1)][InlineData("GameGroup",2)]
    public void P06_ApplyRulesExemptsOnlyStartupGuard(string type,int global)
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-p0-global-"+Guid.NewGuid());Directory.CreateDirectory(root);
        string exe=Path.Combine(Environment.SystemDirectory,"ping.exe");
        using var child=Process.Start(new ProcessStartInfo(exe,"-n 60 127.0.0.1"){UseShellExecute=false,CreateNoWindow=true})!;
        Controller? controller=null;
        try {
            var p=LdPlayer.Identity(child.Id)!.Value;ProxyProfile proxy=new(){Host="127.0.0.1",Port=1080};
            var config=new SavedConfig{Proxies=[proxy],GlobalProxy=new(){Enabled=global!=0,ProxyId=proxy.Id,ResolvedHost="127.0.0.1",BypassLan=false}};
            var target=new TargetInstance("fixture",type,type=="Emulator"?"LDPlayer":"Process",exe,0,true,[p]);
            if(type=="Overflow")config.AutoProxy=new(){Enabled=true,Configured=true,Kind="Process",Pattern="ping.exe"};
            if(type=="GameGroup"){
                var group=new GameLaunchGroup{Root=p,LauncherPath=exe,ClientPath=exe,Members=[new(p,exe)]};config.GameGroups=[group];config.AutoExcludedTargets=[group.Key];
            }
            File.WriteAllText(Path.Combine(root,"owner.sid"),WindowsIdentity.GetCurrent().User!.Value);new ConfigStore(root).Save(config);controller=new(root);
            T Field<T>(string name)=>(T)typeof(Controller).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(controller)!;
            Field<Dictionary<string,TargetInstance>>("targets")[target.Key]=target;
            if(type=="Overflow")Field<HashSet<string>>("automaticUnassigned").Add(target.Key);
            if(global==1)Field<Dictionary<Guid,ProbeResult>>("probes")[proxy.Id]=new(proxy.Id,true,true,true,true,"192.0.2.1","192.0.2.1",1,"fixture",DateTimeOffset.UtcNow);
            typeof(Controller).GetMethod("ApplyRules",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,null);
            Assert.Equal(global==0?0:global==1?1:2,NativeEngine.Core_RouteForTest((uint)p.Pid,(ulong)p.CreatedFileTime,0,out _));
            Assert.Equal(global==0?0:2,NativeEngine.Core_RouteForTest((uint)p.Pid,(ulong)p.CreatedFileTime,1,out _));
        } finally {controller?.Dispose();NativeEngine.Core_ReplacePolicy([],0,"\0\0");if(!child.HasExited)child.Kill();Directory.Delete(root,true);}
    }
}
