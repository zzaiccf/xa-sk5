using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WindowSocks.Core;

public sealed class NativeSandboxFirewall : ISandboxFirewall
{
    private const string Dll="WindowSocksNative.dll";
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)] private static extern uint Core_FirewallReplace(string paths);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern uint Core_FirewallWithdraw();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern uint Core_FirewallAllow();
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern uint Core_FirewallInspect(out uint blocks,out uint permits);
    [DllImport(Dll,CallingConvention=CallingConvention.Cdecl)] private static extern uint Core_FirewallClear();
    private static void Check(uint error){if(error!=0)throw new IOException($"WFP 防護操作失敗（0x{error:X8}）：{new Win32Exception(unchecked((int)error)).Message}");}
    public void ReplaceImages(string[] images)=>Check(Core_FirewallReplace(string.Join('\0',images)+"\0\0"));
    public void WithdrawPermit()=>Check(Core_FirewallWithdraw());
    public void AllowWhileHealthy()=>Check(Core_FirewallAllow());
    public SandboxFirewallStatus Inspect(){Check(Core_FirewallInspect(out uint blocks,out uint permits));return new(blocks,permits,"已知執行檔的 IPv4／IPv6 連入與連出；不涵蓋未列出的系統路徑程序與既有連線尾端封包。");}
    public void Clear()=>Check(Core_FirewallClear());
    public void Dispose()=>WithdrawPermit(); // Persistent blocks intentionally survive service exit.

    public static string[] DiscoverImages(IEnumerable<SandboxInstanceProfile> profiles)
    {
        var images=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options=new EnumerationOptions{RecurseSubdirectories=true,IgnoreInaccessible=false,AttributesToSkip=FileAttributes.ReparsePoint};
        foreach(var profile in profiles){
            SandboxInstances.Validate(profile);
            images.Add(Path.GetFullPath(profile.LauncherPath));
            foreach(var root in new[]{profile.WorkingDirectory,profile.FileRoot}.Distinct(StringComparer.OrdinalIgnoreCase)){
                if(!Directory.Exists(root))continue;
                if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("WFP 保護目錄不可使用重新解析點。");
                foreach(var image in Directory.EnumerateFiles(root,"*.exe",options)){
                    images.Add(Path.GetFullPath(image));if(images.Count>4096)throw new IOException("沙盒執行檔超過 WFP 防護上限，未啟動遊戲。");
                }
            }
        }
        return images.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
