namespace WindowSocks.Core;

// Null password keeps the saved secret. Empty explicitly clears it.
public sealed record EditProxyRequest(Guid ProxyId, string Name, string Host, int Port, string Username, string? Password, DateOnly? ExpiresOn);

public static class ProxyEditing
{
    public static ProxyProfile Prepare(IReadOnlyCollection<ProxyProfile> profiles, EditProxyRequest request)
    {
        var current = profiles.FirstOrDefault(p => p.Id == request.ProxyId) ?? throw new InvalidOperationException("找不到節點，請重新整理。");
        if (request.Name is null || request.Host is null || request.Username is null || request.Name.Length > 200 || request.Name.Any(char.IsControl))
            throw new InvalidOperationException("節點名稱不可超過 200 字或包含控制字元。");
        var next = current with { Name = request.Name.Trim(), Host = request.Host.Trim(), Port = request.Port,
            Username = request.Username, Password = request.Password ?? current.Password, ExpiresOn = request.ExpiresOn };
        try { ProxyImport.Validate(next); } catch (FormatException) { throw new InvalidOperationException("請確認連入 IP、端口與帳密格式；端口須為 1–65535。"); }
        if (next.Name.Length == 0) next.Name = next.Endpoint;
        if (profiles.Any(p => p.Id != next.Id && ProxyImport.SameProxy(p, next))) throw new InvalidOperationException("清單已有相同節點，請直接使用原節點。");
        return next;
    }
}
