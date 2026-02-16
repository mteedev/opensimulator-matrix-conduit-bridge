/*
 * Copyright (c) Fiona Sweet <fiona@pobox.holoneon.com>
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using MySqlConnector;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;

public class BridgeService
{
    private readonly string _connectionString;
    private readonly string _matrixBase;
    private readonly string _homeserver;
    private readonly string _asToken;
    private readonly HttpClient _http;
    private readonly string _avatarBaseUrl; // e.g. https://holoneon.com/avpic/"
    private readonly string _robustUrl;
    private readonly string _osBridgeSecret;
    const string ZERO_UUID = "00000000-0000-0000-0000-000000000000";

    public BridgeService(
        string connectionString,
        string matrixBase,
        string homeserver,
        string asToken,
        string avatarBaseUrl,
        string robustUrl,
        string osBridgeSecret)
    {
        _connectionString = connectionString;
        _matrixBase = matrixBase;
        _homeserver = homeserver;
        _asToken = asToken;
        _avatarBaseUrl = avatarBaseUrl;
        _robustUrl = robustUrl;
	_osBridgeSecret = osBridgeSecret;

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _asToken);
    }

    private async Task<string?> GetRoomIdFromAliasAsync(string aliasLocalPart)
    {
        var alias = $"#${aliasLocalPart}:{_homeserver}".Replace("$", "");
    
        var response = await _http.GetAsync(
            $"{_matrixBase}/_matrix/client/v3/directory/room/{Uri.EscapeDataString(alias)}");
    
        if (!response.IsSuccessStatusCode)
            return null;
    
        var json = await response.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(json);
    
        return doc.RootElement.GetProperty("room_id").GetString();
    }

    public async Task<string> EnableBridgeAsync(
        string groupUuid,
        string groupName,
        string founderAvatarUuid)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();

        // Check if already enabled
        var checkCmd = new MySqlCommand(
            "SELECT room_id FROM group_bridge_state WHERE group_uuid=@g AND enabled=1",
            conn);

        checkCmd.Parameters.AddWithValue("@g", groupUuid);

        var existing = await checkCmd.ExecuteScalarAsync();
        if (existing != null)
            return existing?.ToString() ?? "";

        // Create Matrix room
        var alias = $"os_{groupUuid.Replace("-", "").Substring(0,8)}";

        var createPayload = new
        {
            name = $"OpenSim | {groupName}",
            topic = $"Bridged OpenSimulator group chat\nGroup UUID: {groupUuid}",
            preset = "private_chat",
            room_alias_name = alias,
            visibility = "private"
        };

        var createContent = new StringContent(
            JsonSerializer.Serialize(createPayload),
            Encoding.UTF8,
            "application/json");

        var existingRoomId = await GetRoomIdFromAliasAsync(alias);
        if (existingRoomId != null)
        {
            var updateCmd = new MySqlCommand(
                @"INSERT INTO group_bridge_state
                  (group_uuid, enabled, room_id, enabled_by, enabled_at)
                  VALUES (@g, 1, @r, @f, NOW())
                  ON DUPLICATE KEY UPDATE
                    enabled=1,
                    room_id=@r,
                    enabled_by=@f,
                    enabled_at=NOW();",
                conn);
        
            updateCmd.Parameters.AddWithValue("@g", groupUuid);
            updateCmd.Parameters.AddWithValue("@r", existingRoomId);
            updateCmd.Parameters.AddWithValue("@f", founderAvatarUuid);
        
            await updateCmd.ExecuteNonQueryAsync();
        
            return existingRoomId;
        }
        
        var createResp = await _http.PostAsync(
            $"{_matrixBase}/_matrix/client/v3/createRoom",
            createContent);

        var createJson = await createResp.Content.ReadAsStringAsync();

        if (!createResp.IsSuccessStatusCode)
            throw new Exception($"Room creation failed: {createJson}");

        var doc = JsonDocument.Parse(createJson);
        var roomId = doc.RootElement.GetProperty("room_id").GetString()
            ?? throw new Exception("Room ID missing in response");

        // Ensure founder puppet joins
        var founderMxid = $"@os_{founderAvatarUuid.Replace("-", "")}:{_homeserver}";

        await EnsureUserExistsAsync(founderAvatarUuid);

        await _http.PostAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/join?user_id={Uri.EscapeDataString(founderMxid)}",
            null);

        // Set power levels

        var botMxid = $"@opensim_bot:{_homeserver}";

        var powerPayload = new
        {
            users = new Dictionary<string, int>
            {
                { botMxid, 100 },
                { founderMxid, 100 }
            },
            state_default = 50, //change to 100 to allow founders only, 50 to allow officers
            users_default = 0,
            events_default = 0,
            invite = 50,
            kick = 50,
            ban = 75,
            redact = 50
        };

        var powerContent = new StringContent(
            JsonSerializer.Serialize(powerPayload),
            Encoding.UTF8,
            "application/json");

        await _http.PutAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/state/m.room.power_levels",
            powerContent);

        // Store mapping
        var insertCmd = new MySqlCommand(
            @"INSERT INTO group_bridge_state
              (group_uuid, enabled, room_id, enabled_by, enabled_at)
              VALUES (@g, 1, @r, @f, NOW())
              ON DUPLICATE KEY UPDATE
                enabled=1,
                room_id=@r,
                enabled_by=@f,
                enabled_at=NOW();",
            conn);

        insertCmd.Parameters.AddWithValue("@g", groupUuid);
        insertCmd.Parameters.AddWithValue("@r", roomId);
        insertCmd.Parameters.AddWithValue("@f", founderAvatarUuid);

        await insertCmd.ExecuteNonQueryAsync();

        return roomId;
    }

    private async Task EnsureUserExistsAsync(string avatarUuid)
    {
        var localpart = $"os_{avatarUuid.Replace("-", "")}";
    
        var payload = new
        {
            type = "m.login.application_service",
            username = localpart
        };
    
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
    
        var response = await _http.PostAsync(
            $"{_matrixBase}/_matrix/client/v3/register?kind=user",
            content);
    
        var body = await response.Content.ReadAsStringAsync();
    
        if (!response.IsSuccessStatusCode)
        {
            // This is normal if the puppet already exists
            if (!body.Contains("M_USER_IN_USE"))
            {
                throw new Exception($"User registration failed: {body}");
            }
        }
    }
    
    private async Task EnsureUserJoinedAsync(string roomId, string userId)
    {
        // Invite puppet
        var invitePayload = new
        {
            user_id = userId
        };
    
        var inviteResp = await _http.PostAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/invite",
            new StringContent(
                JsonSerializer.Serialize(invitePayload),
                Encoding.UTF8,
                "application/json"));
    
        var inviteBody = await inviteResp.Content.ReadAsStringAsync();
    
        if (!inviteResp.IsSuccessStatusCode &&
            !inviteBody.Contains("M_USER_IN_USE") &&
            !inviteBody.Contains("already invited"))
        {
            // ignore if already invited
        }
    
        // Join puppet
        var joinResp = await _http.PostAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/join?user_id={Uri.EscapeDataString(userId)}",
            null);
    
        var joinBody = await joinResp.Content.ReadAsStringAsync();
    
        if (!joinResp.IsSuccessStatusCode &&
            !joinBody.Contains("M_ALREADY_JOINED"))
        {
            throw new Exception($"Join failed: {joinBody}");
        }
    }

    private async Task EnsurePuppetAvatarAsync(
        string puppetMxid,
        string senderUuid,
        bool force = false) //force to reset
    {
        if (string.IsNullOrEmpty(_avatarBaseUrl))
            return;
    
        // Check if avatar already set
        var profileUrl =
            $"{_matrixBase}/_matrix/client/v3/profile/{Uri.EscapeDataString(puppetMxid)}/avatar_url" +
            $"?user_id={Uri.EscapeDataString(puppetMxid)}";
    
        var profileResp = await _http.GetAsync(profileUrl);
        if (profileResp.IsSuccessStatusCode)
        {
            var body = await profileResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            if (!force &&
                   doc.RootElement.TryGetProperty("avatar_url", out var el) &&
                   !string.IsNullOrEmpty(el.GetString()))
            {
                return; // already set and not forcing
            }
        }
    
        // Build avatar source URL
        var srcUrl = $"{_avatarBaseUrl}{senderUuid}.png";
    
        // Download image
        var imgResp = await _http.GetAsync(srcUrl);
        if (!imgResp.IsSuccessStatusCode)
            return;
    
        var imgBytes = await imgResp.Content.ReadAsByteArrayAsync();
    
        // Upload to Synapse media
        var uploadUrl =
            $"{_matrixBase}/_matrix/media/v3/upload?user_id={Uri.EscapeDataString(puppetMxid)}";
    
        using var uploadContent = new ByteArrayContent(imgBytes);
        uploadContent.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
    
        var uploadResp = await _http.PostAsync(uploadUrl, uploadContent);
        var uploadBody = await uploadResp.Content.ReadAsStringAsync();
    
        if (!uploadResp.IsSuccessStatusCode)
            throw new Exception($"Avatar upload failed: {uploadBody}");
    
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        var mxc = uploadDoc.RootElement.GetProperty("content_uri").GetString();
    
        // Set avatar_url
        var setPayload = new { avatar_url = mxc };
    
        var setResp = await _http.PutAsync(
            profileUrl,
            new StringContent(JsonSerializer.Serialize(setPayload),
                Encoding.UTF8,
                "application/json"));
    
        var setBody = await setResp.Content.ReadAsStringAsync();
    
        if (!setResp.IsSuccessStatusCode)
            throw new Exception($"Setting avatar failed: {setBody}");
    }
    
    public async Task RelayMessageFromOpenSimAsync(
        string groupUuid,
        string senderUuid,
        string senderName,
        string message)
    {

        if (senderUuid == ZERO_UUID) return; //stop echo-chamber dups

        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
    
        // Check if bridge enabled + get room
        var cmd = new MySqlCommand(
            "SELECT room_id FROM group_bridge_state WHERE group_uuid=@g AND enabled=1",
            conn);
    
        cmd.Parameters.AddWithValue("@g", groupUuid);
    
        var roomIdObj = await cmd.ExecuteScalarAsync();
        if (roomIdObj == null)
            return; // Bridge not enabled
    
        var roomId = roomIdObj.ToString()!;
    
        // Ensure puppet exists
        await EnsureUserExistsAsync(senderUuid);
    
        var puppetMxid = $"@os_{senderUuid.Replace("-", "")}:{_homeserver}";

        //Console.WriteLine($"puppetMxid: {puppetMxid}");

        // Get their Name
        await EnsurePuppetDisplayNameAsync(puppetMxid, senderName);

        // Get their profile pic
        await EnsurePuppetAvatarAsync(puppetMxid, senderUuid);
    
        // Ensure puppet joined room
        await EnsureUserJoinedAsync(roomId, puppetMxid);

        // Get their power level for display
        await SyncMatrixPowerLevelAsync(roomId, puppetMxid, groupUuid, senderUuid);

        // Send message as puppet
        var txnId = Guid.NewGuid().ToString();
    
        var payload = new
        {
            msgtype = "m.text",
            body = message
        };
    
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
    
        var sendResp = await _http.PutAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/m.room.message/{txnId}?user_id={Uri.EscapeDataString(puppetMxid)}",
            content);
        
        var respBody = await sendResp.Content.ReadAsStringAsync();
        
        Console.WriteLine($"SEND STATUS: {sendResp.StatusCode}");
        //Console.WriteLine($"SEND BODY: {respBody}");
        
        if (!sendResp.IsSuccessStatusCode)
        {
            throw new Exception($"Message send failed: {respBody}");
        }
        
    }

    private async Task EnsurePuppetDisplayNameAsync(
        string puppetMxid,
        string desiredDisplayName,
        bool force = false)
    {
        if (string.IsNullOrWhiteSpace(desiredDisplayName))
            return;
    
        desiredDisplayName = desiredDisplayName.Trim();
        if (desiredDisplayName.Length > 64)
            desiredDisplayName = desiredDisplayName[..64];
    
        var url =
            $"{_matrixBase}/_matrix/client/v3/profile/{Uri.EscapeDataString(puppetMxid)}/displayname" +
            $"?user_id={Uri.EscapeDataString(puppetMxid)}";
    
        if (!force)
        {
            // Only read/compare when not forcing
            var getResp = await _http.GetAsync(url);
            if (getResp.IsSuccessStatusCode)
            {
                var body = await getResp.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("displayname", out var dnEl))
                    {
                        var current = dnEl.GetString() ?? "";
                        if (current == desiredDisplayName)
                            return; // already correct
                    }
                }
                catch { }
            }
        }
    
        var payload = new { displayname = desiredDisplayName };
    
        var setResp = await _http.PutAsync(
            url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"));
    
        var setBody = await setResp.Content.ReadAsStringAsync();
    
        if (!setResp.IsSuccessStatusCode)
            throw new Exception($"Set displayname failed: {setBody}");
    }

    /* map OpenSim user to Matrix Power Levels so they get pretty icons - indicating
     *   Owner        100
     *   Officer       75
     *   Moderator     50
     *   Member         0
     * adjust as needed -> i have 100 or 0 in Holoneon.
     */

    private async Task<int> GetOpenSimPowerLevelAsync(string groupUuid, string agentUuid)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
    
        // Get this member's role power
        var cmd = new MySqlCommand(@"
            SELECT r.Powers
            FROM os_groups_membership m
            JOIN os_groups_roles r
              ON r.GroupID = m.GroupID
             AND r.RoleID  = m.SelectedRoleID
            WHERE m.GroupID = @g
              AND m.PrincipalID = @a
            LIMIT 1
        ", conn);
    
        cmd.Parameters.AddWithValue("@g", groupUuid);
        cmd.Parameters.AddWithValue("@a", agentUuid);
    
        var powerObj = await cmd.ExecuteScalarAsync();
        if (powerObj == null)
            return 0;
    
        ulong memberPower = Convert.ToUInt64(powerObj);
    
        // Get highest power in group
        var maxCmd = new MySqlCommand(@"
            SELECT MAX(r.Powers)
            FROM os_groups_membership m
            JOIN os_groups_roles r
              ON r.GroupID = m.GroupID
             AND r.RoleID  = m.SelectedRoleID
            WHERE m.GroupID = @g
        ", conn);
    
        maxCmd.Parameters.AddWithValue("@g", groupUuid);
    
        ulong maxPower = Convert.ToUInt64(await maxCmd.ExecuteScalarAsync());
    
        // Owner OR officer-level authority
        if (memberPower >= maxPower / 2)
            return 100;
    
        return 0;
    }
    
    private async Task SyncMatrixPowerLevelAsync(
        string roomId,
        string puppetMxid,
        string groupUuid,
        string agentUuid,
        bool force = false)
    {
        int desiredPower = await GetOpenSimPowerLevelAsync(groupUuid, agentUuid);
    
        // Fetch current power_levels
        var getResp = await _http.GetAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/state/m.room.power_levels");
    
        var json = await getResp.Content.ReadAsStringAsync();
    
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
    
        var users = new Dictionary<string, int>();
    
        if (root.TryGetProperty("users", out var usersEl))
        {
            foreach (var prop in usersEl.EnumerateObject())
                users[prop.Name] = prop.Value.GetInt32();
        }
    
        if (!force &&
            users.TryGetValue(puppetMxid, out var existing) &&
            existing == desiredPower)
        {
            return; // already correct and not forcing
        }
    
        users[puppetMxid] = desiredPower;
    
        var updatedPayload = new
        {
            users = users,
            users_default = root.GetProperty("users_default").GetInt32(),
            events_default = root.GetProperty("events_default").GetInt32(),
            state_default = root.GetProperty("state_default").GetInt32(),
            invite = root.GetProperty("invite").GetInt32(),
            kick = root.GetProperty("kick").GetInt32(),
            ban = root.GetProperty("ban").GetInt32(),
            redact = root.GetProperty("redact").GetInt32()
        };
    
        await _http.PutAsync(
            $"{_matrixBase}/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/state/m.room.power_levels?user_id=@opensim_bot:{_homeserver}",
            new StringContent(JsonSerializer.Serialize(updatedPayload), Encoding.UTF8, "application/json"));
    }

    public async Task ResyncGroupAsync(string groupUuid)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
    
        // Get room ID
        var roomCmd = new MySqlCommand(
            "SELECT room_id FROM group_bridge_state WHERE group_uuid=@g AND enabled=1",
            conn);
    
        roomCmd.Parameters.AddWithValue("@g", groupUuid);
    
        var roomIdObj = await roomCmd.ExecuteScalarAsync();
        if (roomIdObj == null)
            throw new Exception("Bridge not enabled for this group.");
    
        var roomId = roomIdObj.ToString()!;
    
        // Get group members
        var membersCmd = new MySqlCommand(@"
            SELECT PrincipalID
            FROM os_groups_membership
            WHERE GroupID = @g
        ", conn);
    
        membersCmd.Parameters.AddWithValue("@g", groupUuid);
    
        using var reader = await membersCmd.ExecuteReaderAsync();
    
        var members = new List<string>();
	while (await reader.ReadAsync())
	{
    		var principal = reader.GetString(0);
		var uuidPart = principal.Split(';')[0]; // HG-safe
		if (!Guid.TryParse(uuidPart, out _))
		    continue;
		members.Add(uuidPart);
	}

        reader.Close();
    
        foreach (var uuid in members)
        {
            var puppetMxid = $"@os_{uuid.Replace("-", "")}:{_homeserver}";
    
            await EnsureUserExistsAsync(uuid);
            await EnsurePuppetDisplayNameAsync(puppetMxid, uuid, true);
            await EnsurePuppetAvatarAsync(puppetMxid, uuid, true);
            await EnsureUserJoinedAsync(roomId, puppetMxid);
            await SyncMatrixPowerLevelAsync(roomId, puppetMxid, groupUuid, uuid, true);
        }
    }

    public async Task HandleMatrixTransactionAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
    
        if (!doc.RootElement.TryGetProperty("events", out var events))
            return;
    
        foreach (var ev in events.EnumerateArray())
        {
            // Only room messages
            if (!ev.TryGetProperty("type", out var typeProp))
                continue;
    
            if (typeProp.GetString() != "m.room.message")
                continue;
    
            var sender = ev.GetProperty("sender").GetString() ?? "";
            var roomId = ev.GetProperty("room_id").GetString() ?? "";
    
            // Ignore our own puppet users to avoid loops
            if (sender.StartsWith("@os_") || sender.StartsWith("@opensim_bot"))
                continue;
    
            if (!ev.TryGetProperty("content", out var content))
                continue;
    
            if (!content.TryGetProperty("msgtype", out var msgTypeProp))
                continue;
    
            if (msgTypeProp.GetString() != "m.text")
                continue;
    
            if (!content.TryGetProperty("body", out var bodyProp))
                continue;
    
            var message = bodyProp.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(message))
                continue;
    
            // Look up which OpenSim group this Matrix room is bound to
            var groupUuid = await GetGroupUuidForRoomAsync(roomId);
            if (string.IsNullOrEmpty(groupUuid))
                continue; // room not bridged
    
            // Optional: nicer name than raw mxid
            var fromName = sender;
    
            // If your Synapse includes an "unsigned" displayname (sometimes does), use it:
            if (ev.TryGetProperty("unsigned", out var unsigned) &&
                unsigned.ValueKind == JsonValueKind.Object &&
                unsigned.TryGetProperty("sender_display_name", out var dnProp))
            {
                var dn = dnProp.GetString();
                if (!string.IsNullOrWhiteSpace(dn))
                    fromName = dn;
            }
    
            // Send into Robust injection endpoint
            await RelayMessageToOpenSimAsync(groupUuid, fromName, message);
        }
    }

    private async Task<string?> GetGroupUuidForRoomAsync(string roomId)
    {
        using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync();
    
        using var cmd = new MySqlCommand(
            "SELECT group_uuid FROM group_bridge_state WHERE room_id=@r AND enabled=1 LIMIT 1",
            conn);
    
        cmd.Parameters.AddWithValue("@r", roomId);
    
        var result = await cmd.ExecuteScalarAsync();
        return result?.ToString();
    }
    
    public async Task RelayMessageToOpenSimAsync(string groupUuid, string fromName, string message)
    {
        var payload = new
        {
            group_uuid = groupUuid,
            from_name = fromName,
            message = message
        };
    
        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
    
        Console.WriteLine($"Sending request to: {_robustUrl}/matrix/group-message");
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_robustUrl}/matrix/group-message");
        req.Headers.Add("X-Bridge-Secret", _osBridgeSecret);
        req.Content = content;
    
        var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        Console.WriteLine($"SEND STATUS: {resp.StatusCode}");
        //Console.WriteLine($"BODY: {body}");
    
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"OpenSim injection failed: {body}");
    }
    
}

