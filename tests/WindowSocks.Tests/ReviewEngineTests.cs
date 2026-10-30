using System.Reflection;
using System.Security.Principal;
using WindowSocks.Core;
using WindowSocks.Service;
using Xunit;

namespace WindowSocks.Tests;

public sealed class ReviewEngineTests
{
    [Fact] public void P03_CaptureRecoveryWithdrawsDynamicPermitBeforeReplacement()
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-wfp-unit-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var engine=new Engine();var firewall=new Firewall(engine.Calls);
        try {
            File.WriteAllText(Path.Combine(root,"owner.sid"),WindowsIdentity.GetCurrent().User!.Value);
            using var controller=new Controller(root,sandboxRuntime:new Runtime(),engineLifecycle:engine,sandboxFirewall:firewall);
            engine.Calls.Clear();typeof(Controller).GetField("started",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,true);
            typeof(Controller).GetMethod("EnsureEngine",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,null);
            Assert.Equal(new[]{"withdraw","replace","permit"},engine.Calls);
        } finally {Directory.Delete(root,true);}
    }
    [Fact] public void P03_FailedReplacementLeavesPersistentProtectionWithoutPermit()
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-wfp-failure-"+Guid.NewGuid());Directory.CreateDirectory(root);
        var engine=new Engine{ReplaceResult=false};var firewall=new Firewall(engine.Calls);
        try {
            File.WriteAllText(Path.Combine(root,"owner.sid"),WindowsIdentity.GetCurrent().User!.Value);
            using var controller=new Controller(root,sandboxRuntime:new Runtime(),engineLifecycle:engine,sandboxFirewall:firewall);
            engine.Calls.Clear();typeof(Controller).GetField("started",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(controller,true);
            Assert.Throws<TargetInvocationException>(()=>typeof(Controller).GetMethod("EnsureEngine",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(controller,null));
            Assert.Equal(new[]{"withdraw","replace"},engine.Calls);
        } finally {Directory.Delete(root,true);}
    }
    private sealed class Firewall(List<string> calls):ISandboxFirewall
    {
        public void ReplaceImages(string[] images)=>calls.Add("blocks");
        public void WithdrawPermit()=>calls.Add("withdraw");
        public void AllowWhileHealthy()=>calls.Add("permit");
        public SandboxFirewallStatus Inspect()=>new(0,0,"");
        public void Clear()=>throw new Exception("persistent blocks must survive service disposal");
        public void Dispose()=>WithdrawPermit();
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void R8_RecoveryWithoutLiveSandboxIsImmediateAndReplacesCaptureBeforeClosing(bool savedBox)
    {
        string root = Path.Combine(Path.GetTempPath(), "xa-review-engine-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        var engine = new Engine();
        try {
            File.WriteAllText(Path.Combine(root, "owner.sid"), WindowsIdentity.GetCurrent().User!.Value);
            new ConfigStore(root).Save(new() { SandboxInstances = savedBox ? [SandboxInstances.Create("inactive", @"C:\Fixture\Launcher.exe", Guid.NewGuid())] : [] });
            using var controller = new Controller(root, sandboxRuntime: new Runtime(), engineLifecycle: engine);
            typeof(Controller).GetField("started", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, true);
            var error = Record.Exception(() => typeof(Controller).GetMethod("EnsureEngine", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(controller, null));
            Assert.Null(error);
            Assert.Equal(new[] { "replace" }, engine.Calls);
        } finally { Directory.Delete(root, true); }
    }
    private sealed class Engine : IEngineLifecycle
    {
        public bool ReplaceResult=true;
        public List<string> Calls = [];
        public bool Present => true; public bool DriverPresent => true; public bool Compatible => true; public bool Healthy => false; public uint Error => 0;
        public bool Start(string path) { Calls.Add("start"); return true; }
        public void Stop() => Calls.Add("close");
        public bool ReplaceCapture(string path) { Calls.Add("replace"); return ReplaceResult; }
    }
    private sealed class Runtime : ISandboxRuntime
    {
        public bool IdentityAvailable => true;
        public void VerifyCompatibility() { }
        public SandboxProcess[] Processes(string box) => [];
        public SandboxProcess? Query(int pid) => null;
        public void ValidateSettings(SandboxInstanceProfile profile) { }
        public void TerminateOwned(SandboxInstanceProfile profile, string ownerSid) { }
        public void Dispose() { }
    }
}
