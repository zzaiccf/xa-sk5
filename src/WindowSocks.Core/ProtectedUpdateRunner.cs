using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text.Json;

namespace WindowSocks.Core;

public sealed record UpdateRunnerTicket(string JobFile,string JobHash)
{
    public string Directory=>Path.GetDirectoryName(JobFile)!;
    public string Executable=>Path.Combine(Directory,"helper","WindowSocks.Updater.exe");
}

public static class ProtectedUpdateRunner
{
    public static string Root=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"WindowSocks.UpdateRunners");
    private static readonly string[] owners=["S-1-5-18","S-1-5-32-544","S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];
    public static void ValidateJob(UpdateJob job)
    {
        if(!job.IncludeService||job.PauseService||job.Payload.Length!=0||job.Recover||job.Download?.LocalArchive is not null)
            throw new IOException("提權更新只接受官方線上套件；不接受使用者 payload、離線包或回復指令。");
        if(!Path.GetFullPath(job.Target).TrimEnd('\\').Equals(UpdatePaths.Installed,StringComparison.OrdinalIgnoreCase)||
            !Path.GetFullPath(job.RestartExe).Equals(Path.Combine(UpdatePaths.Installed,"WindowSocks.exe"),StringComparison.OrdinalIgnoreCase)&&
            !Path.GetFullPath(job.RestartExe).Equals(Path.Combine(UpdatePaths.Installed,"xa sk5.exe"),StringComparison.OrdinalIgnoreCase))
            throw new IOException("提權更新只可修改已安裝的受保護程式目錄；可攜版請另行更新。");
        _=AppReleasePackage.ParseVersion(job.Version);
    }
    public static void RequireProtected(string path)
    {
        AppReleasePackage.RejectLinks(path);bool leaf=true;
        for(string? p=Path.GetFullPath(path);p is not null;p=Path.GetDirectoryName(p)) {
            FileSystemSecurity acl=Directory.Exists(p)?new DirectoryInfo(p).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner):new FileInfo(p).GetAccessControl(AccessControlSections.Access|AccessControlSections.Owner);
            if(!owners.Contains(acl.GetOwner(typeof(SecurityIdentifier))!.Value))throw new IOException("更新執行路徑不是受信任系統擁有："+p);
            var dangerous=FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles;
            if(leaf)dangerous|=FileSystemRights.Write;
            foreach(FileSystemAccessRule rule in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
                if(rule.AccessControlType==AccessControlType.Allow&&(rule.PropagationFlags&PropagationFlags.InheritOnly)==0&&!owners.Contains(rule.IdentityReference.Value)&&(rule.FileSystemRights&dangerous)!=0)
                    throw new IOException("更新執行路徑可由一般使用者修改："+p);
            leaf=false;
        }
    }
    private static void CreateProtected(string path)
    {
        if(Directory.Exists(path)){RequireProtected(path);return;}
        var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(new SecurityIdentifier("S-1-5-32-544"));
        foreach(string sid in owners.Take(2))acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-5-32-545"),FileSystemRights.ReadAndExecute,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).Create(acl);RequireProtected(path);
    }
    // Called only by the installed service over its owner-authorized management pipe.
    // The caller never supplies a source executable, staging path or destination root.
    public static UpdateRunnerTicket Prepare(UpdateJob job,string ownerSid)
    {
        ValidateJob(job);RequireProtected(UpdatePaths.Installed);
        foreach(string file in Directory.EnumerateFiles(UpdatePaths.Installed))RequireProtected(file);
        foreach(string file in new[]{"WindowSocks.Core.dll","WindowSocks.Updater.dll"})
            if(System.Reflection.AssemblyName.GetAssemblyName(Path.Combine(UpdatePaths.Installed,file)).Version!.ToString(3)!=AppReleasePackage.CurrentVersion)
                throw new IOException("受保護更新器與服務版本不一致，請先使用可信安裝程式修復。");
        CreateProtected(Root);string work=Path.Combine(Root,Guid.NewGuid().ToString("N"));CreateProtected(work);
        string helper=Path.Combine(work,"helper");CreateProtected(helper);UpdateHelperPackage.Stage(UpdatePaths.Installed,helper);
        string fileName=Path.Combine(work,"job.json");File.WriteAllText(fileName,JsonSerializer.Serialize(job,Json.Options));
        foreach(string name in new[]{"cancel","ready"}){
            string signal=Path.Combine(work,name);File.WriteAllText(signal,"");
            var acl=new FileInfo(signal).GetAccessControl();acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(ownerSid),FileSystemRights.WriteData|FileSystemRights.ReadData,AccessControlType.Allow));new FileInfo(signal).SetAccessControl(acl);
        }
        return new(fileName,AppReleasePackage.Hash(fileName));
    }
    public static UpdateJob ReadTicket(UpdateRunnerTicket ticket,string executable)
    {
        if(!SandboxInstances.Within(ticket.JobFile,Root)||!Path.GetFileName(ticket.JobFile).Equals("job.json",StringComparison.Ordinal)||
            !Path.GetFullPath(executable).Equals(ticket.Executable,StringComparison.OrdinalIgnoreCase)||!AppReleasePackage.ValidHash(ticket.JobHash))throw new IOException("受保護更新票據無效。");
        RequireProtected(ticket.Directory);RequireProtected(ticket.JobFile);RequireProtected(ticket.Executable);
        foreach(string dependency in Directory.EnumerateFiles(Path.GetDirectoryName(ticket.Executable)!))RequireProtected(dependency);
        return ReadAuthenticatedJob(ticket.JobFile,ticket.JobHash);
    }
    public static UpdateJob ReadAuthenticatedJob(string jobFile,string hash)
    {
        using var file=new FileStream(jobFile,FileMode.Open,FileAccess.Read,FileShare.None);
        if(file.Length>128*1024||!Convert.ToHexString(SHA256.HashData(file)).Equals(hash,StringComparison.OrdinalIgnoreCase))throw new IOException("更新工作資料已改變，拒絕提權。");
        file.Position=0;var job=JsonSerializer.Deserialize<UpdateJob>(file,Json.Options)??throw new IOException("更新工作無效。");ValidateJob(job);return job;
    }
    public static async Task<UpdateDownload> ResolveOfficial(string version,CancellationToken ct,ReleaseClient? client=null)
    {
        bool owns=client is null;client??=new();
        try {
            var release=await client.CheckAsync(ReleaseClient.SourceUri(ReleaseClient.DefaultSource),ct);
            if(release.Version!=version)throw new IOException("官方發布版本已改變，請重新檢查更新。");
            return new(release,ReleaseClient.DefaultSource);
        }finally{if(owns)client.Dispose();}
    }
}
