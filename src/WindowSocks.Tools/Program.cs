using System.Text.Json;
using WindowSocks.Core;

Console.InputEncoding = new System.Text.UTF8Encoding(false);
Console.OutputEncoding = new System.Text.UTF8Encoding(false);
if (args.Length == 0) { Console.WriteLine("WindowSocks.Tools probe | import-local | identity <pid> | pipe <action>\n代理資料從標準輸入讀取，不放在命令列參數。"); return; }
switch (args[0])
{
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
