using System.Reflection;
using WindowSocks.Core;
using Xunit;
namespace WindowSocks.Tests;
public sealed class P0UpdateRegressionTests
{
    [Fact] public void P08_JobChangedAfterHandoffFailsAuthentication()
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-p0-job-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try {
            string file=Path.Combine(root,"job.json");var job=new UpdateJob("",UpdatePaths.Installed,AppReleasePackage.CurrentVersion,Path.Combine(UpdatePaths.Installed,"WindowSocks.exe"),0,0,true);
            File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(job,Json.Options));string hash=AppReleasePackage.Hash(file);
            Assert.Equal(job,ProtectedUpdateRunner.ReadAuthenticatedJob(file,hash));
            File.WriteAllText(file,System.Text.Json.JsonSerializer.Serialize(job with{Payload=@"C:\attacker"},Json.Options));
            Assert.Throws<IOException>(()=>ProtectedUpdateRunner.ReadAuthenticatedJob(file,hash));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public async Task P08_ElevatedMetadataIsFetchedOnlyFromEmbeddedOfficialAuthority()
    {
        var seen=new List<string>();string version=AppReleasePackage.CurrentVersion;
        using var client=new ReleaseClient(new MetadataHandler(uri=>{
            seen.Add(uri.AbsoluteUri);
            return System.Text.Json.JsonSerializer.Serialize(new{draft=false,prerelease=false,tag_name="v"+version,published_at="2026-10-30T00:00:00Z",body="fixture",assets=new[]{new{name="xa-sk5-"+version+"-win-x64.zip",state="uploaded",size=3,digest="sha256:"+new string('a',64),browser_download_url="https://github.com/zzaiccf/xa-sk5/releases/download/v"+version+"/xa-sk5-"+version+"-win-x64.zip"}}});
        }));
        var result=await ProtectedUpdateRunner.ResolveOfficial(version,default,client);
        Assert.Equal(new[]{ReleaseClient.DefaultSource},seen);Assert.Equal(ReleaseClient.DefaultSource,result.Source);Assert.Equal(new string('a',64),result.Release.Portable.Sha256,StringComparer.OrdinalIgnoreCase);
    }
    private sealed class MetadataHandler(Func<Uri,string> response):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new StringContent(response(request.RequestUri!))});
    }
    [Fact] public void P08_UserWritableHelperCannotBeElevated()
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-p0-helper-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try {Assert.Throws<IOException>(()=>ProtectedUpdateRunner.RequireProtected(root));}
        finally{Directory.Delete(root,true);}
    }
    [Fact] public void P08_ChangedArchiveIsRejectedBeforeExtraction()
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-p0-zip-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try {
            string zip=Path.Combine(root,"update.zip"),destination=Path.Combine(root,"payload");File.WriteAllText(zip,"trusted");
            var asset=new ReleaseAsset("update.zip",new FileInfo(zip).Length,AppReleasePackage.Hash(zip));File.WriteAllText(zip,"changed");
            Assert.Throws<InvalidDataException>(()=>AppReleasePackage.ExtractVerified(zip,asset,destination,AppReleasePackage.CurrentVersion));Assert.False(Directory.Exists(destination));
        }finally{Directory.Delete(root,true);}
    }
    [Fact] public void P08_JobCannotRedirectElevatedWritesOrSupplyOfflineFiles()
    {
        var job=new UpdateJob("",UpdatePaths.Installed,AppReleasePackage.CurrentVersion,Path.Combine(UpdatePaths.Installed,"WindowSocks.exe"),0,0,true);
        ProtectedUpdateRunner.ValidateJob(job);
        Assert.Throws<IOException>(()=>ProtectedUpdateRunner.ValidateJob(job with{Target=Path.GetTempPath()}));
        Assert.Throws<IOException>(()=>ProtectedUpdateRunner.ValidateJob(job with{Payload=@"C:\attacker"}));
        Assert.Throws<IOException>(()=>ProtectedUpdateRunner.ValidateJob(job with{Recover=true}));
        Assert.Throws<IOException>(()=>ProtectedUpdateRunner.ReadTicket(new(Path.Combine(Path.GetTempPath(),"job.json"),new string('a',64)),@"C:\attacker.exe"));
    }
    [Fact] public void P08_ElevatedJobCannotTrustUserSuppliedPayload()
    {
        string root=Path.Combine(Path.GetTempPath(),"xa-p0-update-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root,"WindowSocks.exe"),"fixture");
            var job=new UpdateJob(Path.Combine(root,"attacker-payload"),root,AppReleasePackage.CurrentVersion,Path.Combine(root,"WindowSocks.exe"),0,0,true,false);
            var validate=typeof(WindowSocks.Updater.Program).GetMethod("ValidateJob",BindingFlags.Static|BindingFlags.NonPublic)!;
            var error=Assert.Throws<TargetInvocationException>(()=>validate.Invoke(null,[job]));
            Assert.IsAssignableFrom<IOException>(error.InnerException);
        } finally {Directory.Delete(root,true);}
    }
}
