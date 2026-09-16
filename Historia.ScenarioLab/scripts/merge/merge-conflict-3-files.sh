#!/usr/bin/env bash
# @short: Local1 must merge Local2's pushed commit: conflicts in 3 files

source "$SCENARIO_LIB"
lab_begin

# Start with shared base repository
lab_use_template _base

# ── Step 1: Local1 creates the initial files and pushes to mainRemote ──
mkdir -p "$SCENARIO_LOCAL1/config" "$SCENARIO_LOCAL1/src" "$SCENARIO_LOCAL1/docs"

cat << 'EOF' > "$SCENARIO_LOCAL1/config/app.ini"
; Application Configuration File
; Version 1.0.0
[server]
host = 127.0.0.1
port = 8080
timeout = 30
max_connections = 100

; Storage Configuration
[storage]
engine = sqlite
path = /var/data/app.db
pool_size = 5
max_overflow = 10
idle_timeout = 300
retry_limit = 3
auto_vacuum = true
wal_mode = true

[cache]
enabled = true
backend = memory
cache_size = 1024
ttl_seconds = 3600
eviction_policy = lru
cleanup_interval = 600
compression = false
stats_enabled = true
cluster_mode = false

[logging]
level = INFO
format = json
output = stdout
file_path = /var/log/app.log
rotate_size_mb = 50
backup_count = 5
buffered = true
color_console = false
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/src/greeting.py"
"""
Greeting service module providing personalized message generation.
"""

import sys
import datetime

DEFAULT_LOCALE = "en_US"
SUPPORTED_LOCALES = ["en_US", "fr_FR", "es_ES", "de_DE"]

# Format configuration
GREETING_PREFIX = "Hello"
DEFAULT_TITLE = "Guest"

def get_current_year():
    return datetime.datetime.now().year

def build_timestamp():
    return datetime.datetime.utcnow().isoformat()

def validate_locale(locale):
    return locale in SUPPORTED_LOCALES

def sanitize_name(name):
    if not name:
        return DEFAULT_TITLE
    return name.strip()

def format_name(name):
    clean = sanitize_name(name)
    return clean.title()

def get_greeting(name=None, locale="en_US"):
    display_name = format_name(name)
    if not validate_locale(locale):
        locale = DEFAULT_LOCALE
    return f"{GREETING_PREFIX}, {display_name}!"

def render_banner(text):
    line = "=" * len(text)
    return f"{line}\n{text}\n{line}"

def print_greeting(name=None):
    msg = get_greeting(name)
    print(render_banner(msg))

if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else None
    print_greeting(target)
EOF

cat << 'EOF' > "$SCENARIO_LOCAL1/docs/guide.md"
# Application User Guide

Welcome to the comprehensive documentation guide for our service.

## Introduction
The service provides reliable message formatting and configuration management.
Status: Production Ready v1.0
Maintainer: ScenarioLab Core Team

## System Requirements
- Python 3.10 or later
- 512 MB available RAM
- 100 MB free disk space
- Network connection for remote clustering
- Linux, macOS, or Windows 11

## Quick Start Installation
Run the following setup command to install:
pip install -r requirements.txt
python setup.py develop
cp config/app.ini.example config/app.ini

## Operational Details
The daemon runs as a foreground service by default.
Logs are streamed directly to standard output in JSON format.
Configuration values are loaded at startup.
Hot reloading is supported for route definitions.

## Troubleshooting
If connection timeout occurs:
Check that server port is open and reachable.
Verify local firewall rules for port 8080.
Review /var/log/app.log for initialization errors.

## Support
Open a GitHub issue for bugs or feature requests.
EOF

lab_commit "$SCENARIO_LOCAL1" "Add application configuration, greeting service, and guide"
git -C "$SCENARIO_LOCAL1" push origin main

# ── Step 2: Local2 pulls, edits 3/2/3 regions, and pushes to mainRemote ──
git -C "$SCENARIO_LOCAL2" pull

# Region 1: timeout=45, port=9000
# Region 2: cache_size=2048, backend=redis
# Region 3: level=DEBUG, format=text
cat << 'EOF' > "$SCENARIO_LOCAL2/config/app.ini"
; Application Configuration File
; Version 1.0.0
[server]
host = 127.0.0.1
port = 9000
timeout = 45
max_connections = 100

; Storage Configuration
[storage]
engine = sqlite
path = /var/data/app.db
pool_size = 5
max_overflow = 10
idle_timeout = 300
retry_limit = 3
auto_vacuum = true
wal_mode = true

[cache]
enabled = true
backend = redis
cache_size = 2048
ttl_seconds = 3600
eviction_policy = lru
cleanup_interval = 600
compression = false
stats_enabled = true
cluster_mode = false

[logging]
level = DEBUG
format = text
output = stdout
file_path = /var/log/app.log
rotate_size_mb = 50
backup_count = 5
buffered = true
color_console = false
EOF

# Region 1: GREETING_PREFIX = "Welcome"
# Region 2: format_name with uppercase
cat << 'EOF' > "$SCENARIO_LOCAL2/src/greeting.py"
"""
Greeting service module providing personalized message generation.
"""

import sys
import datetime

DEFAULT_LOCALE = "en_US"
SUPPORTED_LOCALES = ["en_US", "fr_FR", "es_ES", "de_DE"]

# Format configuration
GREETING_PREFIX = "Welcome"
DEFAULT_TITLE = "Guest"

def get_current_year():
    return datetime.datetime.now().year

def build_timestamp():
    return datetime.datetime.utcnow().isoformat()

def validate_locale(locale):
    return locale in SUPPORTED_LOCALES

def sanitize_name(name):
    if not name:
        return DEFAULT_TITLE
    return name.strip()

def format_name(name):
    clean = sanitize_name(name)
    return clean.upper()

def get_greeting(name=None, locale="en_US"):
    display_name = format_name(name)
    if not validate_locale(locale):
        locale = DEFAULT_LOCALE
    return f"{GREETING_PREFIX}, {display_name}!"

def render_banner(text):
    line = "=" * len(text)
    return f"{line}\n{text}\n{line}"

def print_greeting(name=None):
    msg = get_greeting(name)
    print(render_banner(msg))

if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else None
    print_greeting(target)
EOF

# Region 1: Status: Staging Preview v1.1
# Region 2: python -m pip install --upgrade .
# Region 3: Check that server port 9000 is open
cat << 'EOF' > "$SCENARIO_LOCAL2/docs/guide.md"
# Application User Guide

Welcome to the comprehensive documentation guide for our service.

## Introduction
The service provides reliable message formatting and configuration management.
Status: Staging Preview v1.1
Maintainer: ScenarioLab Core Team

## System Requirements
- Python 3.10 or later
- 512 MB available RAM
- 100 MB free disk space
- Network connection for remote clustering
- Linux, macOS, or Windows 11

## Quick Start Installation
Run the following setup command to install:
python -m pip install --upgrade .
cp config/app.ini.example config/app.ini

## Operational Details
The daemon runs as a foreground service by default.
Logs are streamed directly to standard output in JSON format.
Configuration values are loaded at startup.
Hot reloading is supported for route definitions.

## Troubleshooting
If connection timeout occurs:
Check that server port 9000 is open and reachable.
Verify local firewall rules for port 9000.
Review /var/log/app.log for initialization errors.

## Support
Open a GitHub issue for bugs or feature requests.
EOF

lab_commit "$SCENARIO_LOCAL2" "Local2 updates config, greeting, and guide"
git -C "$SCENARIO_LOCAL2" push origin main

# ── Step 3: Local1 edits the same regions differently, commits, and fetches ──
# Region 1: timeout=60, port=8443
# Region 2: cache_size=4096, backend=memcached
# Region 3: level=WARN, format=structured
cat << 'EOF' > "$SCENARIO_LOCAL1/config/app.ini"
; Application Configuration File
; Version 1.0.0
[server]
host = 127.0.0.1
port = 8443
timeout = 60
max_connections = 100

; Storage Configuration
[storage]
engine = sqlite
path = /var/data/app.db
pool_size = 5
max_overflow = 10
idle_timeout = 300
retry_limit = 3
auto_vacuum = true
wal_mode = true

[cache]
enabled = true
backend = memcached
cache_size = 4096
ttl_seconds = 3600
eviction_policy = lru
cleanup_interval = 600
compression = false
stats_enabled = true
cluster_mode = false

[logging]
level = WARN
format = structured
output = stdout
file_path = /var/log/app.log
rotate_size_mb = 50
backup_count = 5
buffered = true
color_console = false
EOF

# Region 1: GREETING_PREFIX = "Greetings"
# Region 2: format_name with lowercase
cat << 'EOF' > "$SCENARIO_LOCAL1/src/greeting.py"
"""
Greeting service module providing personalized message generation.
"""

import sys
import datetime

DEFAULT_LOCALE = "en_US"
SUPPORTED_LOCALES = ["en_US", "fr_FR", "es_ES", "de_DE"]

# Format configuration
GREETING_PREFIX = "Greetings"
DEFAULT_TITLE = "Guest"

def get_current_year():
    return datetime.datetime.now().year

def build_timestamp():
    return datetime.datetime.utcnow().isoformat()

def validate_locale(locale):
    return locale in SUPPORTED_LOCALES

def sanitize_name(name):
    if not name:
        return DEFAULT_TITLE
    return name.strip()

def format_name(name):
    clean = sanitize_name(name)
    return f"User({clean.lower()})"

def get_greeting(name=None, locale="en_US"):
    display_name = format_name(name)
    if not validate_locale(locale):
        locale = DEFAULT_LOCALE
    return f"{GREETING_PREFIX}, {display_name}!"

def render_banner(text):
    line = "=" * len(text)
    return f"{line}\n{text}\n{line}"

def print_greeting(name=None):
    msg = get_greeting(name)
    print(render_banner(msg))

if __name__ == "__main__":
    target = sys.argv[1] if len(sys.argv) > 1 else None
    print_greeting(target)
EOF

# Region 1: Status: Release Candidate v1.0-RC1
# Region 2: poetry install --no-dev
# Region 3: Check that SSL port 8443 is open
cat << 'EOF' > "$SCENARIO_LOCAL1/docs/guide.md"
# Application User Guide

Welcome to the comprehensive documentation guide for our service.

## Introduction
The service provides reliable message formatting and configuration management.
Status: Release Candidate v1.0-RC1
Maintainer: ScenarioLab Core Team

## System Requirements
- Python 3.10 or later
- 512 MB available RAM
- 100 MB free disk space
- Network connection for remote clustering
- Linux, macOS, or Windows 11

## Quick Start Installation
Run the following setup command to install:
poetry install --no-dev
cp config/app.ini.example config/app.ini

## Operational Details
The daemon runs as a foreground service by default.
Logs are streamed directly to standard output in JSON format.
Configuration values are loaded at startup.
Hot reloading is supported for route definitions.

## Troubleshooting
If connection timeout occurs:
Check that SSL port 8443 is open and reachable.
Verify local firewall rules for port 8443.
Review /var/log/app.log for initialization errors.

## Support
Open a GitHub issue for bugs or feature requests.
EOF

lab_commit "$SCENARIO_LOCAL1" "Local1 updates config, greeting, and guide"
git -C "$SCENARIO_LOCAL1" fetch origin

# ── Self-check ──
# 1. Local1 status is 1 ahead, 1 behind origin/main
AHEAD_BEHIND="$(git -C "$SCENARIO_LOCAL1" rev-list --left-right --count main...origin/main)"
lab_assert "Local1 is 1 ahead and 1 behind origin/main" [ "$AHEAD_BEHIND" = "1	1" ]

# 2. merge-tree shows conflicts in exactly 3 files
set +e
MT_OUT="$(git -C "$SCENARIO_LOCAL1" merge-tree --write-tree --name-only --no-messages main origin/main)"
MT_STATUS=$?
set -e

lab_assert "merge-tree exited with status 1 (conflict detected)" [ "$MT_STATUS" -eq 1 ]

TREE="$(echo "$MT_OUT" | head -n 1)"
CONFLICTED_FILES="$(echo "$MT_OUT" | tail -n +2 | sort | tr '\n' ' ' | xargs)"
lab_assert "Conflicted files match expected 3 files" [ "$CONFLICTED_FILES" = "config/app.ini docs/guide.md src/greeting.py" ]

# 3. Check conflict hunk counts: 3 / 2 / 3
H_INI="$(git -C "$SCENARIO_LOCAL1" cat-file -p "$TREE:config/app.ini" | grep -c '^<<<<<<< ' || true)"
H_PY="$(git -C "$SCENARIO_LOCAL1" cat-file -p "$TREE:src/greeting.py" | grep -c '^<<<<<<< ' || true)"
H_MD="$(git -C "$SCENARIO_LOCAL1" cat-file -p "$TREE:docs/guide.md" | grep -c '^<<<<<<< ' || true)"

lab_assert "config/app.ini has exactly 3 conflict hunks" [ "$H_INI" -eq 3 ]
lab_assert "src/greeting.py has exactly 2 conflict hunks" [ "$H_PY" -eq 2 ]
lab_assert "docs/guide.md has exactly 3 conflict hunks" [ "$H_MD" -eq 3 ]

lab_done
