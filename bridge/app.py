"""
Lighthouse Bridge — Flask Application
Direct port of Fiona's Program.cs HTTP endpoints.

Endpoints (matching Fiona's exactly):
  PUT  /_matrix/app/v1/transactions/{txnId}  — AppService transaction push
  GET  /_matrix/app/v1/users/{userId}        — AppService user existence check
  POST /os/event                              — OpenSim group chat webhook
  POST /admin/bridge/enable                   — Enable bridge for a group
  POST /admin/bridge/resync                   — Resync group puppets

Future extensibility endpoints:
  POST /admin/oar/download                    — Trigger OAR backup for region owner
  GET  /admin/status                          — Bridge status and stats
"""

import hmac
import logging
from flask import Flask, request, jsonify
from .config import Config
from .service import BridgeService

logger = logging.getLogger("lighthouse.app")


def cryptographic_equals(a: str | None, b: str | None) -> bool:
    """Constant-time string comparison (port of Program.cs line 63)."""
    if a is None or b is None:
        return False
    return hmac.compare_digest(a.encode(), b.encode())


def create_app(config_path: str = None) -> Flask:
    """Application factory."""
    cfg = Config(config_path)

    # Setup logging
    logging.basicConfig(
        level=getattr(logging, cfg.log_level, logging.INFO),
        format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    app = Flask(__name__)
    bridge = BridgeService(cfg)
    app.config["bridge"] = bridge
    app.config["cfg"] = cfg

    logger.info("=" * 60)
    logger.info("🔦 Lighthouse Bridge starting...")
    logger.info(f"   Homeserver: {cfg.homeserver}")
    logger.info(f"   Bot: {cfg.bot_mxid}")
    logger.info("=" * 60)

    # ─── AppService: Transaction endpoint ─────────────
    # Port of Program.cs line 149
    # Conduit pushes Matrix events here via AppService API

    @app.route("/_matrix/app/v1/transactions/<txn_id>", methods=["PUT"])
    def appservice_transaction(txn_id):
        """Receive Matrix events from Conduit's AppService push."""
        # Validate HS token (Conduit authenticates with hs_token)
        auth = request.headers.get("Authorization", "")
        if not auth.startswith("Bearer "):
            return jsonify({}), 401

        token = auth[len("Bearer "):]
        if not cryptographic_equals(token, cfg.hs_token):
            return jsonify({}), 401

        logger.debug(f"Transaction {txn_id} received")

        body = request.get_json(silent=True) or {}

        try:
            bridge.handle_matrix_transaction(body)
        except Exception as e:
            logger.error(f"Transaction processing error: {e}", exc_info=True)

        # Always return empty JSON (AppService spec requirement)
        return jsonify({})

    # Also handle POST (some implementations use POST instead of PUT)
    @app.route("/transactions/<txn_id>", methods=["POST", "PUT"])
    def appservice_transaction_alt(txn_id):
        """Alternate transaction endpoint (compat)."""
        body = request.get_json(silent=True) or {}
        try:
            bridge.handle_matrix_transaction(body)
        except Exception as e:
            logger.error(f"Transaction error: {e}", exc_info=True)
        return jsonify({})

    # ─── AppService: User existence check ─────────────
    # Port of Program.cs line 140

    @app.route("/_matrix/app/v1/users/<path:user_id>", methods=["GET"])
    def appservice_user_check(user_id):
        """Conduit asks if we manage this user ID."""
        auth = request.headers.get("Authorization", "")
        if not auth.startswith("Bearer "):
            return jsonify({}), 401

        token = auth[len("Bearer "):]
        if not cryptographic_equals(token, cfg.hs_token):
            return jsonify({}), 401

        logger.debug(f"User existence check: {user_id}")
        # Return 200 = yes we manage this user
        return jsonify({})

    # ─── OpenSim: Event webhook ───────────────────────
    # Port of Program.cs line 182

    @app.route("/os/event", methods=["POST"])
    def opensim_event():
        """
        Receive group chat messages from OpenSim's MatrixGroupInjectModule.

        Expected POST body:
        {
            "type": "group_message",
            "group_uuid": "...",
            "from_uuid": "...",
            "from_name": "FirstName LastName",
            "message": "Hello world"
        }
        """
        # Verify shared secret
        secret = request.headers.get("X-Bridge-Secret", "")
        if not secret or not cryptographic_equals(secret, cfg.bridge_secret):
            return jsonify({"error": "unauthorized"}), 401

        evt = request.get_json(silent=True)
        if not evt:
            return jsonify({"error": "invalid payload"}), 400

        if evt.get("type") == "group_message":
            try:
                bridge.relay_from_opensim(
                    group_uuid=evt["group_uuid"],
                    sender_uuid=evt["from_uuid"],
                    sender_name=evt["from_name"],
                    message=evt["message"],
                )
                return jsonify({"ok": True})
            except Exception as e:
                logger.error(f"OS event error: {e}", exc_info=True)
                return jsonify({"error": str(e)}), 500

        return jsonify({"error": "unknown event type"}), 400

    # ─── Admin: Enable Bridge ─────────────────────────
    # Port of Program.cs line 132

    @app.route("/admin/bridge/enable", methods=["POST"])
    def admin_enable_bridge():
        """Create a Matrix room linked to an OpenSim group."""
        data = request.get_json(silent=True) or {}
        try:
            room_id = bridge.enable_bridge(
                group_uuid=data["GroupUuid"],
                group_name=data["GroupName"],
                founder_avatar_uuid=data["FounderAvatarUuid"],
            )
            return jsonify({"roomId": room_id})
        except KeyError as e:
            return jsonify({"error": f"missing field: {e}"}), 400
        except Exception as e:
            logger.error(f"Enable bridge error: {e}", exc_info=True)
            return jsonify({"error": str(e)}), 500

    # ─── Admin: Resync Group ──────────────────────────
    # Port of Program.cs line 117

    @app.route("/admin/bridge/resync", methods=["POST"])
    def admin_resync():
        """Force resync of puppet users for a group."""
        secret = request.headers.get("X-Bridge-Secret", "")
        if not cryptographic_equals(secret, cfg.bridge_secret):
            return jsonify({"error": "unauthorized"}), 401

        data = request.get_json(silent=True) or {}
        group_uuid = data.get("GroupUuid")
        if not group_uuid:
            return jsonify({"error": "GroupUuid required"}), 400

        try:
            bridge.resync_group(group_uuid)
            return jsonify({"status": "resynced"})
        except Exception as e:
            logger.error(f"Resync error: {e}", exc_info=True)
            return jsonify({"error": str(e)}), 500

    # ─── Admin: Status ────────────────────────────────
    # NEW — not in Fiona's bridge, but useful for ops

    @app.route("/admin/status", methods=["GET"])
    def admin_status():
        """Bridge status overview."""
        return jsonify({
            "service": "lighthouse-bridge",
            "version": "0.1.0",
            "homeserver": cfg.homeserver,
            "bot": cfg.bot_mxid,
        })

    # ─── Admin: List Bridges ──────────────────────────
    # NEW — useful for management

    @app.route("/admin/bridge/list", methods=["GET"])
    def admin_list_bridges():
        """List all active bridges."""
        secret = request.headers.get("X-Bridge-Secret", "")
        if not cryptographic_equals(secret, cfg.bridge_secret):
            return jsonify({"error": "unauthorized"}), 401

        conn = bridge._db()
        try:
            cursor = conn.cursor(dictionary=True)
            cursor.execute(
                "SELECT * FROM group_bridge_state WHERE enabled=1"
            )
            rows = cursor.fetchall()
            return jsonify({"bridges": rows, "count": len(rows)})
        finally:
            conn.close()

    # ─── Future: OAR Download Trigger ─────────────────
    # Placeholder for region backup management

    @app.route("/admin/oar/download", methods=["POST"])
    def admin_oar_download():
        """
        Future: Trigger OAR backup for a region.
        Will integrate with OpenSim's RemoteAdmin console commands.
        """
        return jsonify({
            "status": "not_implemented",
            "note": "OAR download support coming in Phase 3"
        }), 501

    # ─── Health Check ─────────────────────────────────

    @app.route("/health", methods=["GET"])
    def health():
        return jsonify({"status": "ok", "service": "lighthouse-bridge"})

    return app
