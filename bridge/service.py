"""
Lighthouse Bridge — Bridge Service
Direct port of Fiona Sweet's GroupBridgeService.cs for Conduit.

This module handles ALL Matrix API interactions:
- Puppet user registration and management
- Room creation and membership
- Message relay in both directions
- Avatar photos and display names
- Power level synchronization

All Matrix API calls use standard Client-Server spec endpoints.
AppService features use the ?user_id= parameter for puppet control.
"""

import logging
import hmac
import mysql.connector
from mysql.connector import pooling
from urllib.parse import quote
import requests
import uuid as uuid_lib

logger = logging.getLogger("lighthouse.bridge")

ZERO_UUID = "00000000-0000-0000-0000-000000000000"


class BridgeService:
    """
    Core bridge service.
    Port of GroupBridgeService.cs — every public method maps to Fiona's C#.
    """

    def __init__(self, config):
        self.cfg = config
        self._base = config.matrix_base_url.rstrip("/")
        self._hs = config.homeserver
        self._as_token = config.as_token
        self._avatar_base_url = config.avatar_base_url
        self._region_url = config.region_url.rstrip("/")
        self._bridge_secret = config.bridge_secret

        # HTTP session with AppService token (like Fiona's _http with Bearer)
        self._http = requests.Session()
        self._http.headers.update({
            "Authorization": f"Bearer {self._as_token}",
            "Content-Type": "application/json",
        })

        # Database connection pool
        self._pool = pooling.MySQLConnectionPool(
            pool_name="lighthouse",
            pool_size=5,
            host=config.db_host,
            port=config.db_port,
            database=config.db_name,
            user=config.db_user,
            password=config.db_password,
        )

        logger.info("BridgeService initialized")

    def _db(self):
        """Get a database connection from the pool."""
        return self._pool.get_connection()

    # ─── Room Alias Lookup ──────────────────────────────
    # Port of: GetRoomIdFromAliasAsync (line 66)

    def get_room_id_from_alias(self, alias_local: str) -> str | None:
        """Look up a Matrix room ID by its local alias."""
        alias = f"#{alias_local}:{self._hs}"
        resp = self._http.get(
            f"{self._base}/_matrix/client/v3/directory/room/{quote(alias, safe='')}"
        )
        if resp.status_code != 200:
            return None
        return resp.json().get("room_id")

    # ─── Enable Bridge ──────────────────────────────────
    # Port of: EnableBridgeAsync (line 82)

    def enable_bridge(self, group_uuid: str, group_name: str,
                      founder_avatar_uuid: str) -> str:
        """
        Enable bridging for an OpenSim group.
        Creates a Matrix room, registers founder puppet, stores mapping.
        Returns the Matrix room_id.
        """
        conn = self._db()
        try:
            cursor = conn.cursor(dictionary=True)

            # Check if already enabled
            cursor.execute(
                "SELECT room_id FROM group_bridge_state "
                "WHERE group_uuid=%s AND enabled=1",
                (group_uuid,)
            )
            existing = cursor.fetchone()
            if existing:
                return existing["room_id"]

            # Build alias from first 8 chars of UUID (no dashes)
            alias = f"os_{group_uuid.replace('-', '')[:8]}"

            # Check if room already exists with this alias
            existing_room_id = self.get_room_id_from_alias(alias)
            if existing_room_id:
                cursor.execute(
                    "INSERT INTO group_bridge_state "
                    "(group_uuid, enabled, room_id, enabled_by, enabled_at) "
                    "VALUES (%s, 1, %s, %s, NOW()) "
                    "ON DUPLICATE KEY UPDATE "
                    "enabled=1, room_id=%s, enabled_by=%s, enabled_at=NOW()",
                    (group_uuid, existing_room_id, founder_avatar_uuid,
                     existing_room_id, founder_avatar_uuid)
                )
                conn.commit()
                return existing_room_id

            # Create Matrix room
            create_payload = {
                "name": f"OpenSim | {group_name}",
                "topic": f"Bridged OpenSimulator group chat\nGroup UUID: {group_uuid}",
                "preset": "private_chat",
                "room_alias_name": alias,
                "visibility": "private",
            }

            resp = self._http.post(
                f"{self._base}/_matrix/client/v3/createRoom",
                json=create_payload
            )
            if resp.status_code != 200:
                raise Exception(f"Room creation failed: {resp.text}")

            room_id = resp.json()["room_id"]

            # Ensure founder puppet exists and joins
            founder_mxid = f"@os_{founder_avatar_uuid.replace('-', '')}:{self._hs}"
            self.ensure_user_exists(founder_avatar_uuid)

            self._http.post(
                f"{self._base}/_matrix/client/v3/rooms/"
                f"{quote(room_id, safe='')}/join"
                f"?user_id={quote(founder_mxid, safe='')}",
            )

            # Set power levels (bot=100, founder=100)
            bot_mxid = f"@{self.cfg.bot_localpart}:{self._hs}"
            power_payload = {
                "users": {
                    bot_mxid: 100,
                    founder_mxid: 100,
                },
                "state_default": 50,
                "users_default": 0,
                "events_default": 0,
                "invite": 50,
                "kick": 50,
                "ban": 75,
                "redact": 50,
            }

            self._http.put(
                f"{self._base}/_matrix/client/v3/rooms/"
                f"{quote(room_id, safe='')}/state/m.room.power_levels",
                json=power_payload
            )

            # Store mapping in database
            cursor.execute(
                "INSERT INTO group_bridge_state "
                "(group_uuid, enabled, room_id, enabled_by, enabled_at) "
                "VALUES (%s, 1, %s, %s, NOW()) "
                "ON DUPLICATE KEY UPDATE "
                "enabled=1, room_id=%s, enabled_by=%s, enabled_at=NOW()",
                (group_uuid, room_id, founder_avatar_uuid,
                 room_id, founder_avatar_uuid)
            )
            conn.commit()

            logger.info(f"Bridge enabled: {group_name} → {room_id}")
            return room_id

        finally:
            conn.close()

    # ─── Puppet User Registration ───────────────────────
    # Port of: EnsureUserExistsAsync (line 213)

    def ensure_user_exists(self, avatar_uuid: str):
        """Register a puppet Matrix user for an OpenSim avatar via AppService API."""
        localpart = f"os_{avatar_uuid.replace('-', '')}"
        payload = {
            "type": "m.login.application_service",
            "username": localpart,
        }
        resp = self._http.post(
            f"{self._base}/_matrix/client/v3/register?kind=user",
            json=payload
        )
        if not resp.ok and "M_USER_IN_USE" not in resp.text:
            raise Exception(f"Puppet registration failed: {resp.text}")

    # ─── Puppet Room Join ───────────────────────────────
    # Port of: EnsureUserJoinedAsync (line 244)

    def ensure_user_joined(self, room_id: str, user_id: str):
        """Invite and join a puppet user to a room."""
        # Invite
        resp = self._http.post(
            f"{self._base}/_matrix/client/v3/rooms/"
            f"{quote(room_id, safe='')}/invite",
            json={"user_id": user_id}
        )
        # Ignore "already invited" errors

        # Join as puppet
        resp = self._http.post(
            f"{self._base}/_matrix/client/v3/rooms/"
            f"{quote(room_id, safe='')}/join"
            f"?user_id={quote(user_id, safe='')}",
        )
        if not resp.ok and "M_ALREADY_JOINED" not in resp.text:
            raise Exception(f"Puppet join failed: {resp.text}")

    # ─── Puppet Display Name ────────────────────────────
    # Port of: EnsurePuppetDisplayNameAsync (line 425)

    def ensure_puppet_display_name(self, puppet_mxid: str,
                                   desired_name: str, force: bool = False):
        """Set display name for a puppet user."""
        if not desired_name or not desired_name.strip():
            return

        desired_name = desired_name.strip()[:64]

        url = (
            f"{self._base}/_matrix/client/v3/profile/"
            f"{quote(puppet_mxid, safe='')}/displayname"
            f"?user_id={quote(puppet_mxid, safe='')}"
        )

        if not force:
            resp = self._http.get(url)
            if resp.ok:
                current = resp.json().get("displayname", "")
                if current == desired_name:
                    return  # Already correct

        self._http.put(url, json={"displayname": desired_name})

    # ─── Puppet Avatar Photo ────────────────────────────
    # Port of: EnsurePuppetAvatarAsync (line 282)

    def ensure_puppet_avatar(self, puppet_mxid: str,
                             sender_uuid: str, force: bool = False):
        """Download avatar photo from OpenSim, upload to Matrix, set on puppet."""
        if not self._avatar_base_url:
            return

        profile_url = (
            f"{self._base}/_matrix/client/v3/profile/"
            f"{quote(puppet_mxid, safe='')}/avatar_url"
            f"?user_id={quote(puppet_mxid, safe='')}"
        )

        if not force:
            resp = self._http.get(profile_url)
            if resp.ok:
                existing = resp.json().get("avatar_url", "")
                if existing:
                    return  # Already set

        # Fetch avatar image from our photo endpoint
        src_url = self._avatar_base_url.replace("{uuid}", sender_uuid)
        try:
            img_resp = requests.get(src_url, timeout=10)
            if not img_resp.ok:
                return
            img_bytes = img_resp.content
        except Exception:
            return

        # Upload to Matrix media
        upload_url = (
            f"{self._base}/_matrix/media/v3/upload"
            f"?user_id={quote(puppet_mxid, safe='')}"
        )
        upload_resp = self._http.post(
            upload_url,
            data=img_bytes,
            headers={
                **self._http.headers,
                "Content-Type": "image/png",
            }
        )
        if not upload_resp.ok:
            logger.error(f"Avatar upload failed: {upload_resp.text}")
            return

        mxc = upload_resp.json().get("content_uri")

        # Set avatar_url on puppet profile
        self._http.put(profile_url, json={"avatar_url": mxc})

    # ─── OpenSim Power Level Mapping ────────────────────
    # Port of: GetOpenSimPowerLevelAsync (line 482)

    def get_opensim_power_level(self, group_uuid: str,
                                agent_uuid: str) -> int:
        """
        Map OpenSim group role powers to Matrix power levels.
        Owner/Officer → 100, Member → 0
        """
        conn = self._db()
        try:
            cursor = conn.cursor()

            # Get this member's role power
            cursor.execute("""
                SELECT r.Powers
                FROM os_groups_membership m
                JOIN os_groups_roles r
                  ON r.GroupID = m.GroupID AND r.RoleID = m.SelectedRoleID
                WHERE m.GroupID = %s AND m.PrincipalID = %s
                LIMIT 1
            """, (group_uuid, agent_uuid))

            row = cursor.fetchone()
            if not row:
                return 0
            member_power = int(row[0])

            # Get highest power in group
            cursor.execute("""
                SELECT MAX(r.Powers)
                FROM os_groups_membership m
                JOIN os_groups_roles r
                  ON r.GroupID = m.GroupID AND r.RoleID = m.SelectedRoleID
                WHERE m.GroupID = %s
            """, (group_uuid,))

            max_row = cursor.fetchone()
            max_power = int(max_row[0]) if max_row and max_row[0] else 1

            # Owner or officer-level → 100, else 0
            if member_power >= max_power / 2:
                return 100
            return 0

        finally:
            conn.close()

    # ─── Sync Matrix Power Level ────────────────────────
    # Port of: SyncMatrixPowerLevelAsync (line 529)

    def sync_matrix_power_level(self, room_id: str, puppet_mxid: str,
                                group_uuid: str, agent_uuid: str,
                                force: bool = False):
        """Sync an avatar's OpenSim group role to their Matrix power level."""
        desired = self.get_opensim_power_level(group_uuid, agent_uuid)

        # Get current power levels
        resp = self._http.get(
            f"{self._base}/_matrix/client/v3/rooms/"
            f"{quote(room_id, safe='')}/state/m.room.power_levels"
        )
        if not resp.ok:
            return

        pl = resp.json()
        users = pl.get("users", {})

        if not force and users.get(puppet_mxid) == desired:
            return  # Already correct

        users[puppet_mxid] = desired

        updated = {
            "users": users,
            "users_default": pl.get("users_default", 0),
            "events_default": pl.get("events_default", 0),
            "state_default": pl.get("state_default", 50),
            "invite": pl.get("invite", 50),
            "kick": pl.get("kick", 50),
            "ban": pl.get("ban", 75),
            "redact": pl.get("redact", 50),
        }

        bot_mxid = f"@{self.cfg.bot_localpart}:{self._hs}"
        self._http.put(
            f"{self._base}/_matrix/client/v3/rooms/"
            f"{quote(room_id, safe='')}/state/m.room.power_levels"
            f"?user_id={quote(bot_mxid, safe='')}",
            json=updated
        )

    # ─── Relay: OpenSim → Matrix ────────────────────────
    # Port of: RelayMessageFromOpenSimAsync (line 351)

    def relay_from_opensim(self, group_uuid: str, sender_uuid: str,
                           sender_name: str, message: str):
        """
        Relay a group chat message from OpenSim to Matrix.
        Creates puppet, sets profile, joins room, sends message AS puppet.
        """
        if sender_uuid == ZERO_UUID:
            return  # Echo prevention (line 358)

        conn = self._db()
        try:
            cursor = conn.cursor(dictionary=True)
            cursor.execute(
                "SELECT room_id FROM group_bridge_state "
                "WHERE group_uuid=%s AND enabled=1",
                (group_uuid,)
            )
            row = cursor.fetchone()
            if not row:
                return  # Bridge not enabled
            room_id = row["room_id"]
        finally:
            conn.close()

        # Ensure puppet exists
        self.ensure_user_exists(sender_uuid)
        puppet_mxid = f"@os_{sender_uuid.replace('-', '')}:{self._hs}"

        # Set display name and avatar
        self.ensure_puppet_display_name(puppet_mxid, sender_name)
        self.ensure_puppet_avatar(puppet_mxid, sender_uuid)

        # Ensure puppet is in the room
        self.ensure_user_joined(room_id, puppet_mxid)

        # Sync power level
        self.sync_matrix_power_level(
            room_id, puppet_mxid, group_uuid, sender_uuid
        )

        # Send message AS the puppet (the key AppService feature)
        txn_id = str(uuid_lib.uuid4())
        payload = {
            "msgtype": "m.text",
            "body": message,
        }

        resp = self._http.put(
            f"{self._base}/_matrix/client/v3/rooms/"
            f"{quote(room_id, safe='')}/send/m.room.message/{txn_id}"
            f"?user_id={quote(puppet_mxid, safe='')}",
            json=payload
        )

        if not resp.ok:
            raise Exception(f"Message send failed: {resp.text}")

        logger.info(f"OS→Matrix: [{sender_name}] {message[:80]}")

    # ─── Relay: Matrix → OpenSim ────────────────────────
    # Port of: HandleMatrixTransactionAsync (line 634)

    def handle_matrix_transaction(self, transaction_json: dict):
        """
        Process a transaction pushed by Conduit's AppService API.
        Extracts m.room.message events and relays them to OpenSim.
        """
        events = transaction_json.get("events", [])

        for ev in events:
            ev_type = ev.get("type")
            if ev_type != "m.room.message":
                continue

            sender = ev.get("sender", "")
            room_id = ev.get("room_id", "")

            # Skip our own puppets to prevent loops (line 654)
            if sender.startswith("@os_") or sender.startswith(f"@{self.cfg.bot_localpart}"):
                continue

            content = ev.get("content", {})
            if content.get("msgtype") != "m.text":
                continue

            message = content.get("body", "").strip()
            if not message:
                continue

            # Look up which OpenSim group this room bridges to
            group_uuid = self._get_group_for_room(room_id)
            if not group_uuid:
                continue

            # Get display name (check unsigned first, then use mxid)
            from_name = sender
            unsigned = ev.get("unsigned", {})
            if isinstance(unsigned, dict):
                dn = unsigned.get("sender_display_name", "")
                if dn:
                    from_name = dn

            # Relay to OpenSim
            self.relay_to_opensim(group_uuid, from_name, message)

    def _get_group_for_room(self, room_id: str) -> str | None:
        """Look up group_uuid for a bridged Matrix room."""
        conn = self._db()
        try:
            cursor = conn.cursor()
            cursor.execute(
                "SELECT group_uuid FROM group_bridge_state "
                "WHERE room_id=%s AND enabled=1 LIMIT 1",
                (room_id,)
            )
            row = cursor.fetchone()
            return row[0] if row else None
        finally:
            conn.close()

    # Port of: RelayMessageToOpenSimAsync (line 711)

    def relay_to_opensim(self, group_uuid: str, from_name: str,
                         message: str):
        """Send a message from Matrix into an OpenSim group chat."""
        payload = {
            "group_uuid": group_uuid,
            "from_name": from_name,
            "message": message,
        }

        logger.info(
            f"Matrix→OS: Sending to {self._region_url}/matrix/group-message"
        )

        resp = requests.post(
            f"{self._region_url}/matrix/group-message",
            json=payload,
            headers={"X-Bridge-Secret": self._bridge_secret},
            timeout=10,
        )

        if not resp.ok:
            raise Exception(f"OpenSim injection failed: {resp.text}")

        logger.info(f"Matrix→OS: [{from_name}] {message[:80]}")

    # ─── Resync Group ───────────────────────────────────
    # Port of: ResyncGroupAsync (line 581)

    def resync_group(self, group_uuid: str):
        """
        Force resync of all puppet users for a group.
        Re-registers puppets, refreshes names/avatars/power levels.
        """
        conn = self._db()
        try:
            cursor = conn.cursor(dictionary=True)

            # Get room ID
            cursor.execute(
                "SELECT room_id FROM group_bridge_state "
                "WHERE group_uuid=%s AND enabled=1",
                (group_uuid,)
            )
            row = cursor.fetchone()
            if not row:
                raise Exception("Bridge not enabled for this group.")
            room_id = row["room_id"]

            # Get all group members from OpenSim tables
            cursor.execute("""
                SELECT PrincipalID
                FROM os_groups_membership
                WHERE GroupID = %s
            """, (group_uuid,))

            members = []
            for member_row in cursor.fetchall():
                principal = member_row["PrincipalID"]
                # HG-safe: take UUID part before semicolon
                uuid_part = principal.split(";")[0]
                try:
                    uuid_lib.UUID(uuid_part)  # Validate
                    members.append(uuid_part)
                except ValueError:
                    continue

        finally:
            conn.close()

        # Resync each member
        for avatar_uuid in members:
            puppet_mxid = f"@os_{avatar_uuid.replace('-', '')}:{self._hs}"
            try:
                self.ensure_user_exists(avatar_uuid)
                self.ensure_puppet_display_name(puppet_mxid, avatar_uuid, force=True)
                self.ensure_puppet_avatar(puppet_mxid, avatar_uuid, force=True)
                self.ensure_user_joined(room_id, puppet_mxid)
                self.sync_matrix_power_level(
                    room_id, puppet_mxid, group_uuid, avatar_uuid, force=True
                )
            except Exception as e:
                logger.error(f"Resync failed for {avatar_uuid}: {e}")

        logger.info(
            f"Resync complete: {group_uuid} — {len(members)} members"
        )
