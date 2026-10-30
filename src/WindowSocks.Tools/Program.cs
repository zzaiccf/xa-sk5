using System.Text.Json;
using WindowSocks.Core;

if (args.Length > 0 && args[0] == "sandbox-bootstrap") {
    try { await SandboxBootstrap.Run(Guid.Parse(args[1]), args[2]); }
    catch (Exception ex) {
        try { await PipeTransport.CallAsync<bool>(Command.Create("reportSandboxLaunch", new SandboxLaunchReport(Guid.Parse(args[1]), false, ex is System.ComponentModel.Win32Exception native ? $"Windows 錯誤 {native.NativeErrorCode}" : ex.GetType().Name)), pipeName: PipeTransport.SandboxName); } catch { }
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "sandbox-bootstrap-error.txt"), DateTimeOffset.Now.ToString("O") + Environment.NewLine + ex);
        Environment.ExitCode = 1;
    }
    return;
}
Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Length == 0) { Console.WriteLine("WindowSocks.Tools probe | import-local | identity <pid> | pipe <action>\n代理資料從標準輸入讀取，不放在命令列參數。"); return; }
switch (args[0])
{
    case "sandbox-broker":
        using (var runtime = new SandboxieRuntime()) {
            while (await Console.In.ReadLineAsync() is { } requestLine) {
                if (requestLine.Length > PipeTransport.Limit) break;
                Reply reply;Guid requestId=Guid.Empty;
                try {
                    var request = JsonSerializer.Deserialize<SandboxBrokerRequest>(requestLine, Json.Options) ?? throw new InvalidDataException();
                    requestId=request.RequestId;
                    if (request.Action == "list") {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(request.Box, "^Xa_[0-9a-f]{24}$")) throw new InvalidDataException("沙盒名稱無效。");
                        reply = Reply.Success(runtime.Processes(request.Box));
                    } else if (request.Action == "query") reply = Reply.Success(runtime.Query(request.Pid));
                    else if(request.Action=="validate"&&request.Profile is{} settings){runtime.ValidateSettings(settings);reply=Reply.Success(true);}
                    else if (request.Action == "terminate" && request.Profile is { } owned) {
                        SandboxInstances.Validate(owned); _ = new System.Security.Principal.SecurityIdentifier(request.OwnerSid);
                        runtime.TerminateOwned(owned, request.OwnerSid); reply = Reply.Success(true);
                    } else throw new InvalidDataException("不支援的沙盒查詢。");
                } catch (Exception ex) when (ex is not OutOfMemoryException) { reply = Reply.Failure(ex.Message); }
                Console.WriteLine(JsonSerializer.Serialize(new SandboxBrokerReply(requestId,reply), Json.Options));
            }
        }
        break;
    case "sandbox-start":
        var launchId = Guid.Parse(args[1]);
        var launchState = await PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }));
        var launchProfile = launchState.SandboxInstances.Single(p => p.Id == launchId);
        var launchProbe = await PipeTransport.CallAsync<ProbeResult>(Command.Create("probe", new ProxyRequest(launchProfile.ProxyId)));
        if (!launchProbe.Ready) throw new IOException(launchProbe.Message);
        var launchTicket = await PipeTransport.CallAsync<SandboxLaunchTicket>(Command.Create("armSandbox", new SandboxRequest(launchId)));
        var launchInfo = new System.Diagnostics.ProcessStartInfo(Path.Combine(SandboxieRuntime.InstallDirectory, "Start.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = launchProfile.WorkingDirectory };
        foreach (string arg in new[] { "/box:" + launchProfile.BoxName, "/hide_window", Environment.ProcessPath!, "sandbox-bootstrap", launchId.ToString(), launchTicket.Token }) launchInfo.ArgumentList.Add(arg);
        using (System.Diagnostics.Process.Start(launchInfo)) { }
        Console.WriteLine("已提出沙盒啟動，請查核實際程序與窗口。"); break;
    case "sandbox-traffic":
        var trafficId = Guid.Parse(args[1]);
        var trafficIdentity = LdPlayer.Identity(Environment.ProcessId)!.Value;
        bool trafficRouted = false;
        for (int attempt = 0; attempt < 30; attempt++) {
            var trafficStates = await PipeTransport.CallAsync<SandboxInstanceStatus[]>(Command.Create("refreshSandboxes", new { }), pipeName: PipeTransport.SandboxName);
            if (trafficStates.Any(s => s.Id == trafficId && s.State == "代理執行中" && s.Processes.Contains(trafficIdentity))) { trafficRouted = true; break; }
            await Task.Delay(500);
        }
        if (!trafficRouted) throw new IOException("此沙盒測試程序尚未完成代理核驗。");
        var trafficResult = await TransparentTrafficProbe.RunAsync(false);
        await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, identity = trafficIdentity, result = trafficResult }, Json.Options)); break;
    case "sandbox-bootstrap":
        await SandboxBootstrap.Run(Guid.Parse(args[1]), args[2]); break;
    case "sandbox-processes":
        using (var sandbox = new SandboxieRuntime()) Console.WriteLine(JsonSerializer.Serialize(sandbox.Processes(args[1]), Json.Options)); break;
    case "sandbox-prepare":
        var sandboxProfile = JsonSerializer.Deserialize<SandboxInstanceProfile>(await Console.In.ReadToEndAsync(), Json.Options) ?? throw new InvalidDataException();
        await SandboxPreparation.Prepare(sandboxProfile.SourceLauncherPath, sandboxProfile, new Progress<string>(s => Console.Error.WriteLine(s)), default);
        Console.WriteLine(JsonSerializer.Serialize(sandboxProfile, Json.Options)); break;
    case "sandbox-config-command":
        Console.WriteLine(await SandboxieRuntime.Command(args[1], args.Skip(2), default)); break;
    case "vmware-discover":
        Console.WriteLine(JsonSerializer.Serialize((await VmwareDiscovery.Running()).Concat(VmwareDiscovery.Registered()).DistinctBy(v => v.Machine.Path, StringComparer.OrdinalIgnoreCase), Json.Options)); break;
    case "check-update":
        using (var releases = new ReleaseClient()) {
            var latest = await releases.CheckAsync(ReleaseClient.SourceUri(args[1]), default);
            Console.WriteLine(JsonSerializer.Serialize(new { current = AppReleasePackage.CurrentVersion, available = latest.Version, newer = AppReleasePackage.IsNewer(latest.Version, AppReleasePackage.CurrentVersion), latest.Portable.Size, latest.Portable.Sha256 }, Json.Options));
        }
        break;
    case "download-update":
        using (var releases = new ReleaseClient()) {
            var source = ReleaseClient.SourceUri(args[1]); var latest = await releases.CheckAsync(source, default);
            await releases.DownloadAsync(source, latest, args[2], null, default);
            Console.WriteLine($"Downloaded and SHA-256 verified: {latest.Version}; {latest.Portable.Size} bytes.");
        }
        break;
    case "verify-release":
        var releaseFiles = AppReleasePackage.VerifyPayload(args[1], args[2], installed: args.Contains("--installed"));
        Console.WriteLine($"Release {args[2]} verified: {releaseFiles.Length} files; all component versions match."); break;
    case "probe-idle-local":
        int idleSeconds = args.Length > 1 ? int.Parse(args[1]) : 150;
        var idleStore = new ConfigStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var idleConfig = idleStore.Load();
        if (idleConfig.Proxies.Count != 1) throw new InvalidOperationException("閒置對照僅接受一條已保存的代理。");
        Console.WriteLine(JsonSerializer.Serialize(new { stage = "starting", destination = "tcpbin.com:4242", idleSeconds, at = DateTimeOffset.Now }, Json.Options));
        Console.WriteLine(JsonSerializer.Serialize(await IdleTcpProbe.RunAsync(idleConfig.Proxies[0], idleSeconds), Json.Options));
        break;
    case "probe-dns-local":
        var dnsStore = new ConfigStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var dnsConfig = dnsStore.Load();
        foreach (var dnsProfile in dnsConfig.Proxies)
            foreach (string resolver in new[] { "1.1.1.1", "8.8.8.8", "9.9.9.9" })
                Console.WriteLine(JsonSerializer.Serialize(await DnsConnectivityProbe.RunAsync(dnsProfile, resolver), Json.Options));
        break;
    case "probe-target-local":
        if (args.Length != 3 || !int.TryParse(args[2], out int targetPort)) throw new InvalidOperationException("用法：probe-target-local IPv4 連接埠。帳密從本機 DPAPI 設定讀取。");
        var targetStore = new ConfigStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var targetConfig = targetStore.Load();
        if (targetConfig.Proxies.Count == 0) throw new InvalidOperationException("沒有已保存的本機代理。");
        foreach (var targetProfile in targetConfig.Proxies)
            Console.WriteLine(JsonSerializer.Serialize(await TargetConnectivityProbe.RunAsync(targetProfile, new(targetProfile.Id, args[1], targetPort, 5)), Json.Options));
        break;
    case "traffic-client":
        Console.WriteLine(JsonSerializer.Serialize(new { ready = true, pid = Environment.ProcessId }, Json.Options));
        if (await Console.In.ReadLineAsync() != "start") return;
        Console.WriteLine(JsonSerializer.Serialize(await TransparentTrafficProbe.RunAsync(args.Contains("--dns-only")), Json.Options));
        _ = await Console.In.ReadLineAsync(); break;
    case "migrate-local":
        var migrationStore = new ConfigStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var migrationConfig = migrationStore.Load();
        if (migrationConfig.Proxies.Count == 0) throw new InvalidOperationException("沒有已保存的本機代理。");
        var migrationText = string.Join('\n', migrationConfig.Proxies.Select(p => $"socks5://{Uri.EscapeDataString(p.Username)}:{Uri.EscapeDataString(p.Password)}@{p.Host}:{p.Port}"));
        var migrationCount = await PipeTransport.CallAsync<int>(Command.Create("import", new ImportRequest(migrationText)));
        Console.WriteLine($"已將 {migrationCount} 組本機代理移交服務（重複資料不會新增）；未啟用窗口攔截。"); break;
    case "probe-service":
        var serviceSnapshot = await PipeTransport.CallAsync<Snapshot>(Command.Create("snapshot", new { }));
        foreach (var serviceProxy in serviceSnapshot.Proxies) {
            var serviceProbe = await PipeTransport.CallAsync<ProbeResult>(Command.Create("probe", new ProxyRequest(serviceProxy.Id)));
            Console.WriteLine(JsonSerializer.Serialize(new { serviceProxy.Endpoint, serviceProbe.StatusLabel, serviceProbe.TcpExit, serviceProbe.UdpExit, serviceProbe.LatencyMs, serviceProbe.ConnectLatencyMs, serviceProbe.UdpLatencyMs, serviceProbe.Location, serviceProbe.LocationMessage, serviceProbe.At }, Json.Options));
        }
        break;
    case "verify-package":
        var entries = ComponentPackage.VerifyManifest(args.Length > 1 ? args[1] : AppContext.BaseDirectory);
        Console.WriteLine($"Package integrity verified: {entries.Length} files. No service or driver was started."); break;
    case "probe":
        var profile = ProxyImport.Parse(await Console.In.ReadToEndAsync()).Single();
        var result = await ProxyProbe.RunAsync(profile);
        Console.WriteLine(JsonSerializer.Serialize(new { endpoint = profile.Endpoint, result.Reachable, result.Authenticated, result.Tcp, result.Udp, result.TcpExit, result.UdpExit, result.LatencyMs, result.Message, result.At }, new JsonSerializerOptions(Json.Options) { WriteIndented = true }));
        Environment.ExitCode = result.Ready ? 0 : 2; break;
    case "probe-local":
        var localStore = new ConfigStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var localConfig = localStore.Load();
        foreach (var proxy in localConfig.Proxies) {
            var probe = await ProxyProbe.RunAsync(proxy); probe = await IpLocationLookup.EnrichAsync(proxy, probe);
            localConfig.LastProbes.RemoveAll(p => p.ProxyId == proxy.Id); localConfig.LastProbes.Add(probe);
            Console.WriteLine(JsonSerializer.Serialize(new { proxy.Endpoint, probe.StatusLabel, probe.TcpExit, probe.UdpExit, probe.LatencyMs, probe.ConnectLatencyMs, probe.UdpLatencyMs, probe.Location, probe.LocationMessage }, Json.Options));
        }
        localStore.Save(localConfig); break;
    case "import-local":
        var imported = ProxyImport.Parse(await Console.In.ReadToEndAsync());
        var store = new ConfigStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowSocks"), System.Security.Cryptography.DataProtectionScope.CurrentUser);
        var config = store.Load();
        foreach (var p in imported) if (!config.Proxies.Any(x => x.Host == p.Host && x.Port == p.Port && x.Username == p.Username && x.Password == p.Password)) config.Proxies.Add(p);
        store.Save(config); Console.WriteLine($"已加密保存 {imported.Count} 組代理；未啟用系統攔截。"); break;
    case "identity": Console.WriteLine(JsonSerializer.Serialize(LdPlayer.Identity(int.Parse(args[1])), Json.Options)); break;
    case "pipe":
        var command = args.Contains("--stdin") ? new Command(args[1], JsonSerializer.Deserialize<JsonElement>(await Console.In.ReadToEndAsync(), Json.Options)) : Command.Create(args[1], new { });
        var response = await PipeTransport.CallAsync<JsonElement>(command); Console.WriteLine(response.GetRawText()); break;
}
