using Microsoft.AspNetCore.SignalR;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VoidServer.Hubs;

public class StoredUser
{
    public string Username     { get; set; } = "";
    public string Nickname     { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string AvatarColor  { get; set; } = "#5865F2";
    public string Initials     { get; set; } = "?";
    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public List<string> Friends        { get; set; } = new();
    public List<string> FriendRequests { get; set; } = new();
}

public class StoredMessage
{
    public string From     { get; set; } = "";
    public string To       { get; set; } = "";
    public string Content  { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.UtcNow;
}

public class ChatHub : Hub
{
    private static readonly ConcurrentDictionary<string, string> _online = new();
    private static IHubContext<ChatHub>? _hubContext;
    private static readonly string _usersDir    = Path.Combine("db", "users");
    private static readonly string _msgsDir     = Path.Combine("db", "messages");
    private static readonly string _channelsDir = Path.Combine("db", "channels");
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    // ── Helpers estáticos para as rotas HTTP admin ──
    public static int OnlineCount => _online.Count(k => k.Key != "__admin__");
    public static string[] OnlineUsers => _online.Keys.Where(k => k != "__admin__").ToArray();
    public static bool IsOnline(string username) => _online.ContainsKey(username.ToLower());
    public static async Task KickUser(string username)
    {
        if (_hubContext == null) return;
        if (_online.TryGetValue(username, out var cid))
            await _hubContext.Clients.Client(cid).SendAsync("Kicked", "Desconectado pelo admin.");
    }

    public ChatHub(IHubContext<ChatHub> hubContext)
    {
        _hubContext = hubContext;
    }

    static ChatHub()
    {
        Directory.CreateDirectory(_usersDir);
        Directory.CreateDirectory(_msgsDir);
        Directory.CreateDirectory(_channelsDir);
    }

    public override async Task OnConnectedAsync()
    {
        var username = Context.GetHttpContext()?.Request.Query["username"].ToString();
        if (!string.IsNullOrEmpty(username))
        {
            _online[username.ToLower()] = Context.ConnectionId;
            await Clients.Others.SendAsync("UserStatusChanged", username, true);
            Log($"ONLINE {username}");
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? ex)
    {
        var user = _online.FirstOrDefault(x => x.Value == Context.ConnectionId);
        if (user.Key != null)
        {
            _online.TryRemove(user.Key, out _);
            await Clients.Others.SendAsync("UserStatusChanged", user.Key, false);
            Log($"OFFLINE {user.Key}");
        }
        await base.OnDisconnectedAsync(ex);
    }

    public async Task<string> AuthenticateUser(string username, string password, bool isRegister)
    {
        try
        {
            username = username.Trim().ToLower();
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return "error";
            var path = UserPath(username);
            if (isRegister)
            {
                if (File.Exists(path)) return "user_exists";
                var user = new StoredUser { Username = username, Nickname = username, PasswordHash = HashPassword(password), AvatarColor = RandColor(username), Initials = username[0].ToString().ToUpper(), CreatedAt = DateTime.UtcNow };
                await Write(path, user);
                Log($"REGISTER {username}");
                return "ok";
            }
            else
            {
                if (!File.Exists(path)) return "invalid_credentials";
                var user = await Read<StoredUser>(path);
                if (user == null || !VerifyPassword(password, user.PasswordHash)) return "invalid_credentials";
                Log($"LOGIN {username}");
                return "ok";
            }
        }
        catch (Exception e) { Log($"AUTH ERROR {e.Message}"); return "error"; }
    }

    public async Task<object?> GetUserProfile(string username)
    {
        var user = await Read<StoredUser>(UserPath(username.ToLower()));
        if (user == null) return null;
        return new { user.Username, user.Nickname, user.AvatarColor, user.Initials, Friends = user.Friends.ToArray() };
    }

    public async Task SendFriendRequest(string targetUsername)
    {
        var from = Caller();
        if (from == null) return;
        targetUsername = targetUsername.Trim().ToLower();
        var targetPath = UserPath(targetUsername);
        if (!File.Exists(targetPath)) { await Clients.Caller.SendAsync("FriendRequestFailed", "Usuario nao encontrado."); return; }
        var target = await Read<StoredUser>(targetPath);
        if (target == null) return;
        if (target.Friends.Contains(from)) { await Clients.Caller.SendAsync("FriendRequestFailed", "Ja sao amigos."); return; }
        if (target.FriendRequests.Contains(from)) { await Clients.Caller.SendAsync("FriendRequestFailed", "Pedido ja enviado."); return; }
        target.FriendRequests.Add(from);
        await Write(targetPath, target);
        if (_online.TryGetValue(targetUsername, out var cid)) await Clients.Client(cid).SendAsync("ReceiveFriendRequest", from);
        await Clients.Caller.SendAsync("FriendRequestSent", targetUsername);
        Log($"FRIEND_REQ {from} -> {targetUsername}");
    }

    public async Task AcceptFriendRequest(string requesterUsername)
    {
        var me = Caller();
        if (me == null) return;
        requesterUsername = requesterUsername.Trim().ToLower();
        var mePath = UserPath(me);
        var themPath = UserPath(requesterUsername);
        var meUser = await Read<StoredUser>(mePath);
        var themUser = await Read<StoredUser>(themPath);
        if (meUser == null || themUser == null) return;
        meUser.FriendRequests.Remove(requesterUsername);
        if (!meUser.Friends.Contains(requesterUsername)) meUser.Friends.Add(requesterUsername);
        if (!themUser.Friends.Contains(me)) themUser.Friends.Add(me);
        await Write(mePath, meUser);
        await Write(themPath, themUser);
        if (_online.TryGetValue(requesterUsername, out var cid)) await Clients.Client(cid).SendAsync("FriendAccepted", me);
        await Clients.Caller.SendAsync("FriendAccepted", requesterUsername);
        Log($"FRIENDS {me} <-> {requesterUsername}");
    }

    public async Task DeclineFriendRequest(string requesterUsername)
    {
        var me = Caller();
        if (me == null) return;
        var user = await Read<StoredUser>(UserPath(me));
        if (user == null) return;
        user.FriendRequests.Remove(requesterUsername.ToLower());
        await Write(UserPath(me), user);
    }

    public async Task<List<string>> GetPendingRequests(string username)
    {
        var user = await Read<StoredUser>(UserPath(username.ToLower()));
        return user?.FriendRequests ?? new();
    }

    public async Task SendPrivateMessage(string fromNickname, string targetUsername, string content)
    {
        var from = Caller();
        if (from == null || string.IsNullOrWhiteSpace(content)) return;
        targetUsername = targetUsername.Trim().ToLower();
        content = content[..Math.Min(content.Length, 2000)];
        var msg = new StoredMessage { From = from, To = targetUsername, Content = content, SentAt = DateTime.UtcNow };
        await AppendMsg(from, targetUsername, msg);
        var nick = string.IsNullOrEmpty(fromNickname) ? from : fromNickname;
        var payload = new { Author = new { Nickname = nick, Initials = nick[0].ToString().ToUpper() }, Content = content, Timestamp = msg.SentAt };
        if (_online.TryGetValue(targetUsername, out var cid)) await Clients.Client(cid).SendAsync("ReceivePrivateMessage", payload, from);
        await Clients.Caller.SendAsync("ReceivePrivateMessage", payload, from);
        Log($"DM {from} -> {targetUsername}");
    }

    public async Task<List<object>> GetChatHistory(string targetUsername)
    {
        var me = Caller();
        if (me == null) return new();
        var msgs = await LoadMsgs(me, targetUsername.ToLower());
        return msgs.TakeLast(50).Select(m => (object)new { Author = new { Nickname = m.From, Initials = m.From[0].ToString().ToUpper() }, Content = m.Content, Timestamp = m.SentAt }).ToList();
    }

    public async Task JoinChannel(string serverId, string channelId)
    {
        var g = $"{serverId}_{channelId}".Replace(" ", "_")[..Math.Min($"{serverId}_{channelId}".Length, 64)];
        await Groups.AddToGroupAsync(Context.ConnectionId, g);
        Log($"JOIN {Caller()} -> {g}");
    }

    public async Task SendMessage(string username, string message, string color, string badge, string serverId, string channelId)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        message = message[..Math.Min(message.Length, 2000)];
        var g = $"{serverId}_{channelId}".Replace(" ", "_")[..Math.Min($"{serverId}_{channelId}".Length, 64)];
        var payload = new { Author = new { Nickname = username, Initials = username.Length > 0 ? username[0].ToString().ToUpper() : "?" }, Content = message, Timestamp = DateTime.UtcNow };
        await Clients.Group(g).SendAsync("ReceiveMessage", payload);
        Log($"MSG {username} -> {g}");
    }

    private string? Caller() => _online.FirstOrDefault(x => x.Value == Context.ConnectionId).Key;
    private static string UserPath(string u) => Path.Combine(_usersDir, $"{u}.json");
    private static string MsgPath(string a, string b) { var p = string.Compare(a, b) < 0 ? $"{a}_{b}" : $"{b}_{a}"; return Path.Combine(_msgsDir, $"{p}.json"); }
    // ── Segurança: SHA-256 + salt aleatório (compatível com SecurityService.cs do cliente) ──
    private static string HashPassword(string password)
    {
        var saltBytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToHexString(saltBytes).ToLower();
        var hash = Sha256Hex(salt + password);
        return $"{salt}${hash}";
    }
    private static bool VerifyPassword(string password, string stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length == 2)
        {
            var actual = Sha256Hex(parts[0] + password);
            // timing-safe compare
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(actual),
                System.Text.Encoding.UTF8.GetBytes(parts[1]));
        }
        // Legado: void_2026 pepper sem salt
        var legacy = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password + "void_2026"))).ToLower();
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(legacy),
            System.Text.Encoding.UTF8.GetBytes(stored));
    }
    private static string Sha256Hex(string input)
    {
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input))).ToLower();
    }
    private static string RandColor(string n) { var c = new[] { "#5865F2","#57F287","#FEE75C","#EB459E","#ED4245","#00C9A7","#8B5CF6" }; return c[Math.Abs(n.GetHashCode()) % c.Length]; }
    private static async Task<T?> Read<T>(string path) where T : class { if (!File.Exists(path)) return null; return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), _json); }
    private static async Task Write<T>(string path, T obj) => await File.WriteAllTextAsync(path, JsonSerializer.Serialize(obj, _json));
    private static async Task AppendMsg(string a, string b, StoredMessage m) { var path = MsgPath(a, b); var list = await LoadMsgs(a, b); list.Add(m); if (list.Count > 500) list = list.TakeLast(500).ToList(); await Write(path, list); }
    private static async Task<List<StoredMessage>> LoadMsgs(string a, string b) { var path = MsgPath(a, b); if (!File.Exists(path)) return new(); return await Read<List<StoredMessage>>(path) ?? new(); }
    private static void Log(string m) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");

    // ══════════════════════════════════════════════════════════════════════
    // VOZ — Sinalização de chamadas (relay P2P via SignalR)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>Inicia uma chamada de voz para outro usuário online.</summary>
    public async Task StartVoiceCall(string targetUsername)
    {
        var caller = Caller();
        if (caller == null) return;
        targetUsername = targetUsername.Trim().ToLower();
        if (!_online.TryGetValue(targetUsername, out var targetCid))
        { await Clients.Caller.SendAsync("VoiceCallError", "Usuário não está online."); return; }
        await Clients.Client(targetCid).SendAsync("VoiceCallIncoming", caller);
        Log($"VOICE_CALL {caller} -> {targetUsername}");
    }

    /// <summary>Aceita uma chamada de voz pendente.</summary>
    public async Task AcceptVoiceCall(string callerUsername)
    {
        var me = Caller();
        if (me == null) return;
        callerUsername = callerUsername.Trim().ToLower();
        if (_online.TryGetValue(callerUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceCallAccepted", me);
        Log($"VOICE_ACCEPT {me} <- {callerUsername}");
    }

    /// <summary>Recusa uma chamada de voz.</summary>
    public async Task DeclineVoiceCall(string callerUsername)
    {
        var me = Caller();
        if (me == null) return;
        callerUsername = callerUsername.Trim().ToLower();
        if (_online.TryGetValue(callerUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceCallDeclined", me);
        Log($"VOICE_DECLINE {me} -x {callerUsername}");
    }

    /// <summary>Encerra uma chamada de voz ativa.</summary>
    public async Task EndVoiceCall(string peerUsername)
    {
        var me = Caller();
        if (me == null) return;
        peerUsername = peerUsername.Trim().ToLower();
        if (_online.TryGetValue(peerUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceCallEnded", me);
        Log($"VOICE_END {me} -| {peerUsername}");
    }

    /// <summary>Retransmite chunk de áudio PCM para o peer da chamada.</summary>
    public async Task SendVoiceAudio(string targetUsername, byte[] audioChunk)
    {
        var sender = Caller();
        if (sender == null || audioChunk == null || audioChunk.Length == 0) return;
        targetUsername = targetUsername.Trim().ToLower();
        if (_online.TryGetValue(targetUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceAudioChunk", sender, audioChunk);
    }

    // ── WebRTC signaling (para o cliente web) ──────────────────────────

    public async Task SendVoiceOffer(string targetUsername, string sdp)
    {
        var from = Caller();
        if (from == null) return;
        targetUsername = targetUsername.Trim().ToLower();
        if (_online.TryGetValue(targetUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceOffer", from, sdp);
    }

    public async Task SendVoiceAnswer(string targetUsername, string sdp)
    {
        var from = Caller();
        if (from == null) return;
        targetUsername = targetUsername.Trim().ToLower();
        if (_online.TryGetValue(targetUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceAnswer", from, sdp);
    }

    public async Task SendVoiceIce(string targetUsername, string candidateJson)
    {
        var from = Caller();
        if (from == null) return;
        targetUsername = targetUsername.Trim().ToLower();
        if (_online.TryGetValue(targetUsername, out var cid))
            await Clients.Client(cid).SendAsync("VoiceIce", from, candidateJson);
    }

}