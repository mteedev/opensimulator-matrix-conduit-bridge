"""
Lighthouse Bridge — Configuration
Loads config.yaml and provides typed access to settings.
"""
import os, sys, yaml, logging

logger = logging.getLogger("lighthouse.config")

class Config:
    """Typed configuration loaded from config.yaml."""

    def __init__(self, path: str = None):
        path = path or os.environ.get("LIGHTHOUSE_CONFIG", "./config.yaml")
        if not os.path.isfile(path):
            print(f"ERROR: Config file not found: {path}")
            print("  cp config.example.yaml config.yaml  # then edit it")
            sys.exit(1)

        with open(path) as f:
            d = yaml.safe_load(f) or {}

        # Matrix / Conduit
        m = d.get("matrix", {})
        self.matrix_base_url = m.get("base_url", "http://127.0.0.1:6167")
        self.homeserver = m.get("homeserver", "localhost")
        self.as_token = m.get("as_token", "")
        self.hs_token = m.get("hs_token", "")
        self.bot_localpart = m.get("bot_localpart", "opensim_bot")
        self.bot_mxid = f"@{self.bot_localpart}:{self.homeserver}"

        # OpenSim
        o = d.get("opensim", {})
        self.bridge_secret = o.get("bridge_secret", "")
        self.region_url = o.get("region_url", "http://127.0.0.1:9000")

        # Database
        db = d.get("database", {})
        self.db_host = db.get("host", "127.0.0.1")
        self.db_port = db.get("port", 3306)
        self.db_name = db.get("name", "opensim_matrix_bridge")
        self.db_user = db.get("user", "bridge")
        self.db_password = db.get("password", "")
        self.db_connection_string = (
            f"host={self.db_host}&port={self.db_port}&"
            f"database={self.db_name}&user={self.db_user}&"
            f"password={self.db_password}"
        )

        # Avatar
        av = d.get("avatar", {})
        self.avatar_base_url = av.get("base_url", "")
        self.avatar_cache_dir = av.get("cache_dir", "./data/avpic-cache")
        self.asset_service_url = av.get("asset_service_url", "http://127.0.0.1:8003")

        # Server
        s = d.get("server", {})
        self.appservice_port = s.get("appservice_port", 9009)
        self.appservice_host = s.get("appservice_host", "127.0.0.1")
        self.opensim_port = s.get("opensim_port", 9010)
        self.opensim_host = s.get("opensim_host", "0.0.0.0")
        self.log_level = s.get("log_level", "INFO")

        # Validate critical fields
        for field in ["as_token", "hs_token", "bridge_secret"]:
            val = getattr(self, field)
            if not val or val == "CHANGE_ME":
                print(f"ERROR: {field} must be set in config.yaml")
                sys.exit(1)

        logger.info(f"Config loaded: homeserver={self.homeserver}")
